using System.Collections.Concurrent;
using Anthology.Contracts;

namespace Anthology.Community.Api;

public sealed class CommunityState
{
    private readonly object _pollLock = new();
    private readonly CommunityFeed _seed = DemoContent.CreateFeed();
    private readonly Dictionary<string, MutablePoll> _polls;
    private readonly ConcurrentDictionary<string, StoredReport> _reports = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<ChatMessage> _messages = new();

    public CommunityState()
    {
        _polls = _seed.Polls.ToDictionary(
            poll => poll.Id,
            poll => new MutablePoll(poll),
            StringComparer.OrdinalIgnoreCase);
    }

    public CommunityFeed GetFeed() => _seed with
    {
        Polls = _polls.Values.Select(value => value.Snapshot()).ToArray(),
    };

    public PollItem? GetPoll(string pollId) =>
        _polls.TryGetValue(pollId, out var poll) ? poll.Snapshot() : null;

    public PollItem? Vote(string pollId, PollVoteRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserId);
        if (request.OptionIds.Count == 0)
        {
            throw new ArgumentException("Нужно выбрать хотя бы один вариант.");
        }

        if (!_polls.TryGetValue(pollId, out var poll))
        {
            return null;
        }

        lock (_pollLock)
        {
            poll.Vote(request);
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

        var receipt = new BugReportReceipt(
            $"BUG-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            DateTimeOffset.UtcNow,
            "new");
        _reports[receipt.Id] = new StoredReport(receipt, report);
        return receipt;
    }

    public bool ChannelExists(string channelId) =>
        _seed.Channels.Any(channel => string.Equals(channel.Id, channelId, StringComparison.OrdinalIgnoreCase));

    public void AppendMessage(ChatMessage message)
    {
        _messages.Enqueue(message);
        while (_messages.Count > 500 && _messages.TryDequeue(out _))
        {
        }
    }

    private sealed class MutablePoll
    {
        private readonly PollItem _source;
        private readonly Dictionary<string, int> _votes;
        private readonly HashSet<string> _voters = new(StringComparer.OrdinalIgnoreCase);

        public MutablePoll(PollItem source)
        {
            _source = source;
            _votes = source.Options.ToDictionary(option => option.Id, option => option.Votes, StringComparer.OrdinalIgnoreCase);
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
    }

    private sealed record StoredReport(BugReportReceipt Receipt, BugReportRequest Report);
}
