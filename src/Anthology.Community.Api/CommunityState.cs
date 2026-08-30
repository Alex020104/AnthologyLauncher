using System.Security.Cryptography;
using System.IO.Compression;
using System.Globalization;
using Anthology.Contracts;

namespace Anthology.Community.Api;

public sealed class CommunityState
{
    private readonly object _stateLock = new();
    private readonly CommunityFeed _seed = DemoContent.CreateFeed();
    private readonly Dictionary<string, MutablePoll> _polls;
    private readonly Dictionary<string, StoredReport> _reports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<ChatMessage> _messages = new();
    private readonly CommunityDatabase _database;
    private readonly string _legacyStatePath;
    private readonly string _attachmentsRoot;
    private readonly string _backupsRoot;

    public CommunityState()
    {
        var communityRoot = CommunityPaths.CommunityRoot;
        _legacyStatePath = Path.Combine(communityRoot, "state.json");
        _attachmentsRoot = Path.Combine(communityRoot, "attachments");
        _backupsRoot = Path.Combine(CommunityPaths.ResolveDataRoot(), "Backups");
        _database = new CommunityDatabase(communityRoot);
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
        var requiredFields = new (string Name, string? Value, int MinimumLength, int MaximumLength)[]
        {
            ("Заголовок", report.Title, 12, 160),
            ("Описание ситуации", report.Description, 30, 10_000),
            ("Шаги воспроизведения", report.ReproductionSteps, 20, 10_000),
            ("Ожидаемый результат", report.ExpectedResult, 5, 5_000),
            ("Фактический результат", report.ActualResult, 5, 5_000),
            ("Версия лаунчера", report.LauncherVersion, 3, 128),
            ("Версия сборки", report.GameVersion, 5, 256),
            ("Лог или пояснение об отсутствии вылета", report.LogExcerpt, 10, 30_000),
            ("Контакт для ответа", report.Contact, 3, 512),
            ("Автоматическая диагностика", report.SystemSpecs, 20, 10_000),
            ("Ссылка на полный пакет", report.EvidenceUrl, 12, 2048),
        };
        foreach (var field in requiredFields)
        {
            var length = field.Value?.Trim().Length ?? 0;
            if (length < field.MinimumLength)
            {
                throw new ArgumentException($"Поле «{field.Name}» обязательно и заполнено недостаточно подробно.");
            }
            if (length > field.MaximumLength)
            {
                throw new ArgumentException($"Поле «{field.Name}» превышает допустимый размер.");
            }
        }

        if (report.Title.Length > 160 || report.Description.Length > 10_000)
        {
            throw new ArgumentException("Баг-репорт превышает допустимый размер.");
        }

        if (!Uri.TryCreate(report.EvidenceUrl, UriKind.Absolute, out var evidenceUri)
                || evidenceUri.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(evidenceUri.UserInfo)
                || report.EvidenceUrl.Length > 2048)
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
                ExpectedResult = report.ExpectedResult.Trim(),
                ActualResult = report.ActualResult.Trim(),
                LauncherVersion = report.LauncherVersion.Trim(),
                GameVersion = report.GameVersion.Trim(),
                LogExcerpt = report.LogExcerpt!.Trim(),
                Contact = report.Contact!.Trim(),
                SystemSpecs = report.SystemSpecs!.Trim(),
                EvidenceUrl = report.EvidenceUrl!.Trim(),
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

    public bool DeleteReport(string reportId)
    {
        lock (_stateLock)
        {
            if (!_reports.Remove(reportId))
            {
                return false;
            }
            SaveState();
            var reportRoot = Path.Combine(_attachmentsRoot, reportId);
            if (Directory.Exists(reportRoot))
            {
                Directory.Delete(reportRoot, true);
            }
            return true;
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

    public ChatMessage CreateMessage(string channelId, ChatMessageRequest request, bool isDeveloper)
    {
        if (!ChannelExists(channelId))
        {
            throw new KeyNotFoundException(channelId);
        }

        var authorId = RequireText(request.AuthorId, 96, "Не указан пользователь.");
        var authorName = RequireText(request.AuthorName, 64, "Не указано имя.");
        var text = RequireText(request.Text, 2_000, "Сообщение пустое.");
        var message = new ChatMessage(
            Guid.NewGuid().ToString("N"),
            channelId,
            isDeveloper ? $"developer:{authorName}" : authorId,
            authorName,
            text,
            DateTimeOffset.UtcNow,
            isDeveloper);
        AppendMessage(message);
        return message;
    }

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

    public bool DeleteMessage(string channelId, string messageId)
    {
        lock (_stateLock)
        {
            var retained = _messages
                .Where(message => !string.Equals(message.ChannelId, channelId, StringComparison.OrdinalIgnoreCase)
                                  || !string.Equals(message.Id, messageId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (retained.Length == _messages.Count)
            {
                return false;
            }

            _messages.Clear();
            foreach (var message in retained)
            {
                _messages.Enqueue(message);
            }
            SaveState();
            return true;
        }
    }

    public CommunityStorageStatus GetStorageStatus()
    {
        lock (_stateLock)
        {
            return new CommunityStorageStatus(
                "sqlite",
                _database.DatabasePath,
                _reports.Count,
                _messages.Count,
                Directory.Exists(_attachmentsRoot)
                    ? Directory.EnumerateFiles(_attachmentsRoot, "*", SearchOption.AllDirectories).LongCount()
                    : 0);
        }
    }

    public CommunityBackupResult CreateBackup()
    {
        lock (_stateLock)
        {
            SaveState();
            Directory.CreateDirectory(_backupsRoot);
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
            var temporaryRoot = Path.Combine(_backupsRoot, $".tmp-{Guid.NewGuid():N}");
            var archivePath = Path.Combine(_backupsRoot, $"anthology-community-{stamp}.zip");
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                _database.CreateSnapshot(Path.Combine(temporaryRoot, "community.db"));
                if (Directory.Exists(_attachmentsRoot))
                {
                    CopyDirectory(_attachmentsRoot, Path.Combine(temporaryRoot, "attachments"));
                }
                ZipFile.CreateFromDirectory(temporaryRoot, archivePath, CompressionLevel.Optimal, false);
                return new CommunityBackupResult(archivePath, new FileInfo(archivePath).Length, DateTimeOffset.UtcNow);
            }
            finally
            {
                if (Directory.Exists(temporaryRoot))
                {
                    Directory.Delete(temporaryRoot, true);
                }
            }
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

    private static string RequireText(string? value, int maximumLength, string error)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > maximumLength)
        {
            throw new ArgumentException(error);
        }
        return normalized;
    }

    private PersistedState LoadState()
    {
        return _database.Load(_legacyStatePath, PersistedState.Empty);
    }

    private void SaveState()
    {
        var state = new PersistedState(
            _polls.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Export(),
                StringComparer.OrdinalIgnoreCase),
            _reports.Values.OrderBy(report => report.Receipt.CreatedAt).ToArray(),
            _messages.ToArray());
        _database.Save(state);
    }

    private static void CopyDirectory(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
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

public sealed record CommunityStorageStatus(
    string Engine,
    string DatabasePath,
    int Reports,
    int Messages,
    long Attachments);

public sealed record CommunityBackupResult(
    string Path,
    long Size,
    DateTimeOffset CreatedAt);
