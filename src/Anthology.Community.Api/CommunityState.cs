using System.Text.Json;
using Anthology.Contracts;

namespace Anthology.Community.Api;

public sealed class CommunityState
{
    private readonly object _stateLock = new();
    private readonly CommunityFeed _seed = DemoContent.CreateFeed();
    private readonly Dictionary<string, MutablePoll> _polls;
    private readonly Dictionary<string, StoredReport> _reports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<ChatMessage> _messages = new();
    private readonly string _statePath;

    public CommunityState()
    {
        var dataRoot = Environment.GetEnvironmentVariable("ANTHOLOGY_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            dataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AnthologyLauncherNext");
        }

        _statePath = Path.Combine(Path.GetFullPath(dataRoot), "Community", "state.json");
        var persisted = LoadState();
        _polls = _seed.Polls.ToDictionary(
            poll => poll.Id,
            poll =>
            {
                persisted.Polls.TryGetValue(poll.Id, out var saved);
                return new MutablePoll(poll, saved);
            },
            StringComparer.OrdinalIgnoreCase);
        foreach (var report in persisted.Reports)
        {
            _reports[report.Receipt.Id] = report;
        }

        foreach (var message in persisted.Messages.TakeLast(500))
        {
            _messages.Enqueue(message);
        }
    }

    public CommunityFeed GetFeed()
    {
        lock (_stateLock)
        {
            return _seed with
            {
                Polls = _polls.Values.Select(value => value.Snapshot()).ToArray(),
            };
        }
    }

    public PollItem? GetPoll(string pollId)
    {
        lock (_stateLock)
        {
            return _polls.TryGetValue(pollId, out var poll) ? poll.Snapshot() : null;
        }
    }

    public PollItem? Vote(string pollId, PollVoteRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserId);
        if (request.OptionIds.Count == 0)
        {
            throw new ArgumentException("Нужно выбрать хотя бы один вариант.");
        }

        lock (_stateLock)
        {
            if (!_polls.TryGetValue(pollId, out var poll))
            {
                return null;
            }

            poll.Vote(request);
            SaveState();
            return poll.Snapshot();
        }
    }

    public BugReportReceipt CreateReport(BugReportRequest report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(report.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(report.Description);
        ArgumentException.ThrowIfNullOrWhiteSpace(report.ReproductionSteps);
        if (report.Title.Length > 160 || report.Description.Length > 10_000)
        {
            throw new ArgumentException("Баг-репорт превышает допустимый размер.");
        }

        lock (_stateLock)
        {
            var receipt = new BugReportReceipt(
                $"BUG-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                DateTimeOffset.UtcNow,
                "new");
            _reports[receipt.Id] = new StoredReport(receipt, report);
            SaveState();
            return receipt;
        }
    }

    public bool ChannelExists(string channelId) =>
        _seed.Channels.Any(channel => string.Equals(channel.Id, channelId, StringComparison.OrdinalIgnoreCase));

    public void AppendMessage(ChatMessage message)
    {
        lock (_stateLock)
        {
            _messages.Enqueue(message);
            while (_messages.Count > 500)
            {
                _messages.Dequeue();
            }

            SaveState();
        }
    }

    public IReadOnlyList<ChatMessage> GetMessages(string channelId, int take = 100)
    {
        lock (_stateLock)
        {
            return _messages
                .Where(message => string.Equals(message.ChannelId, channelId, StringComparison.OrdinalIgnoreCase))
                .TakeLast(Math.Clamp(take, 1, 200))
                .ToArray();
        }
    }

    private PersistedState LoadState()
    {
        if (!File.Exists(_statePath))
        {
            return PersistedState.Empty;
        }

        try
        {
            var state = JsonSerializer.Deserialize<PersistedState>(
                File.ReadAllText(_statePath),
                ManifestJson.Options);
            return state ?? PersistedState.Empty;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return PersistedState.Empty;
        }
    }

    private void SaveState()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var state = new PersistedState(
            _polls.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Export(),
                StringComparer.OrdinalIgnoreCase),
            _reports.Values.OrderBy(report => report.Receipt.CreatedAt).ToArray(),
            _messages.ToArray());
        var temporary = _statePath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, ManifestJson.Options));
            File.Move(temporary, _statePath, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private sealed class MutablePoll
    {
        private readonly PollItem _source;
        private readonly Dictionary<string, int> _votes;
        private readonly HashSet<string> _voters;

        public MutablePoll(PollItem source, PersistedPoll? persisted)
        {
            _source = source;
            _votes = source.Options.ToDictionary(
                option => option.Id,
                option => persisted?.Votes.GetValueOrDefault(option.Id, option.Votes) ?? option.Votes,
                StringComparer.OrdinalIgnoreCase);
            _voters = persisted is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(persisted.Voters, StringComparer.OrdinalIgnoreCase);
        }

        public void Vote(PollVoteRequest request)
        {
            if (_source.ClosesAt <= DateTimeOffset.UtcNow)
            {
                throw new ArgumentException("Опрос уже завершён.");
            }

            if (_voters.Contains(request.UserId))
            {
                throw new ArgumentException("Этот пользователь уже голосовал.");
            }

            var selected = request.OptionIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (!_source.MultipleChoice && selected.Length != 1)
            {
                throw new ArgumentException("В этом опросе можно выбрать только один вариант.");
            }

            if (selected.Any(optionId => !_votes.ContainsKey(optionId)))
            {
                throw new ArgumentException("Выбран неизвестный вариант ответа.");
            }

            _voters.Add(request.UserId);
            foreach (var optionId in selected)
            {
                _votes[optionId]++;
            }
        }

        public PollItem Snapshot() => _source with
        {
            Options = _source.Options
                .Select(option => option with { Votes = _votes[option.Id] })
                .ToArray(),
        };

        public PersistedPoll Export() => new(
            new Dictionary<string, int>(_votes, StringComparer.OrdinalIgnoreCase),
            _voters.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private sealed record PersistedState(
        Dictionary<string, PersistedPoll> Polls,
        IReadOnlyList<StoredReport> Reports,
        IReadOnlyList<ChatMessage> Messages)
    {
        public static PersistedState Empty { get; } = new(
            new Dictionary<string, PersistedPoll>(StringComparer.OrdinalIgnoreCase),
            [],
            []);
    }

    private sealed record PersistedPoll(
        Dictionary<string, int> Votes,
        IReadOnlyList<string> Voters);

    private sealed record StoredReport(BugReportReceipt Receipt, BugReportRequest Report);
}
