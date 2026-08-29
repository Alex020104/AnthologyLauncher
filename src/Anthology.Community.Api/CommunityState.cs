using System.Text.Json;
using System.Security.Cryptography;
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
    private readonly string _attachmentsRoot;

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
        _attachmentsRoot = Path.Combine(Path.GetDirectoryName(_statePath)!, "attachments");
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

        if (!string.IsNullOrWhiteSpace(report.EvidenceUrl)
            && (!Uri.TryCreate(report.EvidenceUrl, UriKind.Absolute, out var evidenceUri)
                || evidenceUri.Scheme != Uri.UriSchemeHttps
                || report.EvidenceUrl.Length > 2048))
        {
            throw new ArgumentException("Ссылка на полный пакет должна быть корректной HTTPS-ссылкой.");
        }

        lock (_stateLock)
        {
            var now = DateTimeOffset.UtcNow;
            var receipt = new BugReportReceipt(
                $"BUG-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                now,
                BugReportStatuses.New,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant());
            var normalized = report with
            {
                Title = report.Title.Trim(),
                Description = report.Description.Trim(),
                ReproductionSteps = report.ReproductionSteps.Trim(),
                ReporterId = string.IsNullOrWhiteSpace(report.ReporterId) ? "anonymous" : report.ReporterId.Trim(),
                ReporterName = string.IsNullOrWhiteSpace(report.ReporterName) ? "Игрок" : report.ReporterName.Trim(),
                InterfaceLanguage = AnthologyLanguages.IsSupported(report.InterfaceLanguage)
                    ? AnthologyLanguages.Normalize(report.InterfaceLanguage)
                    : "ru",
            };
            _reports[receipt.Id] = new StoredReport(receipt, normalized, [], [], now);
            SaveState();
            return receipt;
        }
    }

    public IReadOnlyList<BugReportDetails> GetReports(string? status = null)
    {
        lock (_stateLock)
        {
            return _reports.Values
                .Where(report => string.IsNullOrWhiteSpace(status)
                                 || string.Equals(report.Receipt.Status, status, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(ReportUpdatedAt)
                .Select(report => ToDetails(report, includeAccessToken: false))
                .ToArray();
        }
    }

    public BugReportDetails? GetReport(string reportId, bool includeAccessToken = false)
    {
        lock (_stateLock)
        {
            return _reports.TryGetValue(reportId, out var report)
                ? ToDetails(report, includeAccessToken)
                : null;
        }
    }

    public bool ReportTokenMatches(string reportId, string? accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return false;
        }

        lock (_stateLock)
        {
            if (!_reports.TryGetValue(reportId, out var report)
                || string.IsNullOrWhiteSpace(report.Receipt.AccessToken))
            {
                return false;
            }

            var expected = System.Text.Encoding.UTF8.GetBytes(report.Receipt.AccessToken);
            var supplied = System.Text.Encoding.UTF8.GetBytes(accessToken.Trim());
            return expected.Length == supplied.Length
                   && CryptographicOperations.FixedTimeEquals(expected, supplied);
        }
    }

    public BugReportDetails AddReportMessage(
        string reportId,
        BugReportReplyRequest request,
        bool isDeveloper)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        if (request.Text.Length > 10_000)
        {
            throw new ArgumentException("Ответ превышает допустимый размер.");
        }

        lock (_stateLock)
        {
            if (!_reports.TryGetValue(reportId, out var stored))
            {
                throw new KeyNotFoundException(reportId);
            }
            if (string.Equals(stored.Receipt.Status, BugReportStatuses.Closed, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Закрытое обращение нельзя дополнять. Разработчик может открыть его повторно.");
            }

            var now = DateTimeOffset.UtcNow;
            var authorId = isDeveloper
                ? $"developer:{NormalizeDeveloperName(request.AuthorName)}"
                : stored.Report.ReporterId;
            var authorName = isDeveloper
                ? NormalizeDeveloperName(request.AuthorName)
                : stored.Report.ReporterName;
            var message = new BugReportMessage(
                Guid.NewGuid().ToString("N"),
                authorId,
                authorName,
                isDeveloper ? "developer" : "player",
                request.Text.Trim(),
                now,
                AnthologyLanguages.IsSupported(request.Language)
                    ? AnthologyLanguages.Normalize(request.Language)
                    : "ru");
            var status = isDeveloper
                ? BugReportStatuses.WaitingForPlayer
                : stored.Receipt.Status == BugReportStatuses.New
                    ? BugReportStatuses.New
                    : BugReportStatuses.InProgress;
            var updated = stored with
            {
                Receipt = stored.Receipt with { Status = status },
                Messages = (stored.Messages ?? []).Append(message).ToArray(),
                UpdatedAt = now,
            };
            _reports[reportId] = updated;
            SaveState();
            return ToDetails(updated, includeAccessToken: false);
        }
    }

    public BugReportDetails SetReportStatus(
        string reportId,
        BugReportStatusRequest request)
    {
        if (!BugReportStatuses.IsSupported(request.Status))
        {
            throw new ArgumentException("Неизвестный статус обращения.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DeveloperName);

        lock (_stateLock)
        {
            if (!_reports.TryGetValue(reportId, out var stored))
            {
                throw new KeyNotFoundException(reportId);
            }

            var now = DateTimeOffset.UtcNow;
            var status = request.Status.Trim().ToLowerInvariant();
            var systemMessage = new BugReportMessage(
                Guid.NewGuid().ToString("N"),
                $"developer:{NormalizeDeveloperName(request.DeveloperName)}",
                NormalizeDeveloperName(request.DeveloperName),
                "system",
                $"Статус изменён: {status}",
                now,
                "ru");
            var updated = stored with
            {
                Receipt = stored.Receipt with { Status = status },
                Messages = (stored.Messages ?? []).Append(systemMessage).ToArray(),
                UpdatedAt = now,
            };
            _reports[reportId] = updated;
            SaveState();
            return ToDetails(updated, includeAccessToken: false);
        }
    }

    public string? GetAttachmentPath(string reportId, string fileName)
    {
        lock (_stateLock)
        {
            if (!_reports.TryGetValue(reportId, out var report))
            {
                return null;
            }
            var safeName = Path.GetFileName(fileName);
            if (!(report.Attachments ?? []).Any(item =>
                    string.Equals(item.FileName, safeName, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }
            var path = Path.Combine(_attachmentsRoot, reportId, safeName);
            return File.Exists(path) ? path : null;
        }
    }

    public async Task<IReadOnlyList<BugReportAttachment>> SaveAttachmentsAsync(
        string reportId,
        IFormFileCollection files,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportId);
        ArgumentNullException.ThrowIfNull(files);
        lock (_stateLock)
        {
            if (!_reports.ContainsKey(reportId))
            {
                throw new KeyNotFoundException(reportId);
            }
        }

        if (files.Count is < 1 or > 5)
        {
            throw new ArgumentException("Можно приложить от 1 до 5 небольших файлов.");
        }

        const long maximumFileSize = 5 * 1024 * 1024;
        const long maximumTotalSize = 15 * 1024 * 1024;
        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".ltx", ".xml", ".script", ".log", ".txt", ".json", ".cfg", ".ini", ".zip", ".7z",
        };
        if (files.Sum(file => file.Length) > maximumTotalSize)
        {
            throw new ArgumentException("Общий размер вложений превышает 15 МБ.");
        }

        var reportRoot = Path.Combine(_attachmentsRoot, reportId);
        Directory.CreateDirectory(reportRoot);
        var saved = new List<BugReportAttachment>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Length is <= 0 or > maximumFileSize)
            {
                throw new ArgumentException($"Файл '{file.FileName}' пуст или превышает 5 МБ.");
            }

            var fileName = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(fileName)
                || !allowedExtensions.Contains(Path.GetExtension(fileName)))
            {
                throw new ArgumentException($"Тип файла '{file.FileName}' не разрешён.");
            }

            var safeName = string.Concat(fileName.Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
            var destination = Path.Combine(reportRoot, safeName);
            if (File.Exists(destination))
            {
                destination = Path.Combine(
                    reportRoot,
                    $"{Path.GetFileNameWithoutExtension(safeName)}-{Guid.NewGuid().ToString("N")[..8]}{Path.GetExtension(safeName)}");
            }

            var temporary = destination + $".tmp-{Guid.NewGuid():N}";
            try
            {
                await using (var source = file.OpenReadStream())
                await using (var target = new FileStream(
                                 temporary,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 64 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await source.CopyToAsync(target, cancellationToken);
                    await target.FlushAsync(cancellationToken);
                }

                File.Move(temporary, destination);
                await using var hashStream = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
                var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken)).ToLowerInvariant();
                saved.Add(new BugReportAttachment(Path.GetFileName(destination), file.Length, sha256));
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        lock (_stateLock)
        {
            var stored = _reports[reportId];
            var attachments = (stored.Attachments ?? []).Concat(saved).ToArray();
            _reports[reportId] = stored with
            {
                Attachments = attachments,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            SaveState();
        }

        return saved;
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

    private static BugReportDetails ToDetails(StoredReport stored, bool includeAccessToken)
    {
        var receipt = includeAccessToken
            ? stored.Receipt
            : stored.Receipt with { AccessToken = null };
        return new BugReportDetails(
            receipt,
            stored.Report,
            stored.Attachments ?? [],
            stored.Messages ?? [],
            ReportUpdatedAt(stored));
    }

    private static DateTimeOffset ReportUpdatedAt(StoredReport stored) =>
        stored.UpdatedAt ?? stored.Receipt.CreatedAt;

    private static string NormalizeDeveloperName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Разработчик Anthology" : value.Trim();

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

    private sealed record StoredReport(
        BugReportReceipt Receipt,
        BugReportRequest Report,
        IReadOnlyList<BugReportAttachment>? Attachments = null,
        IReadOnlyList<BugReportMessage>? Messages = null,
        DateTimeOffset? UpdatedAt = null);
}
