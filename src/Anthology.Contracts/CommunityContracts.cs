namespace Anthology.Contracts;

public sealed record CommunityFeed(
    IReadOnlyList<NewsItem> News,
    IReadOnlyList<VideoItem> Videos,
    IReadOnlyList<ModEntry> Mods,
    IReadOnlyList<PollItem> Polls,
    IReadOnlyList<CommunityChannel> Channels);

public sealed record NewsItem(
    string Id,
    string Title,
    string Summary,
    DateTimeOffset PublishedAt,
    string Category,
    string? CoverUrl = null,
    string? ActionUrl = null);

public sealed record VideoItem(
    string Id,
    string Title,
    string Provider,
    string EmbedUrl,
    string? PosterUrl,
    TimeSpan? Duration);

public sealed record ModEntry(
    string Id,
    string Name,
    string Author,
    string Summary,
    string Version,
    IReadOnlyList<string> Tags,
    string? CoverUrl = null,
    string Section = "modmakers");

public sealed record PollItem(
    string Id,
    string Question,
    IReadOnlyList<PollOption> Options,
    DateTimeOffset ClosesAt,
    bool MultipleChoice = false);

public sealed record PollOption(
    string Id,
    string Text,
    int Votes);

public sealed record CommunityChannel(
    string Id,
    string Name,
    string Description,
    bool IsDeveloperChannel = false);

public sealed record PollVoteRequest(
    string UserId,
    IReadOnlyList<string> OptionIds);

public sealed record BugReportRequest(
    string Title,
    string Description,
    string ReproductionSteps,
    string ExpectedResult,
    string ActualResult,
    string LauncherVersion,
    string GameVersion,
    string? LogExcerpt,
    string? Contact,
    string? SystemSpecs = null,
    string? EvidenceUrl = null,
    string ReporterId = "",
    string ReporterName = "",
    string InterfaceLanguage = "ru");

public sealed record BugReportReceipt(
    string Id,
    DateTimeOffset CreatedAt,
    string Status,
    string? AccessToken = null);

public sealed record BugReportAttachment(
    string FileName,
    long Size,
    string Sha256);

public sealed record BugReportMessage(
    string Id,
    string AuthorId,
    string AuthorName,
    string AuthorRole,
    string Text,
    DateTimeOffset CreatedAt,
    string Language = "ru");

public sealed record BugReportDetails(
    BugReportReceipt Receipt,
    BugReportRequest Report,
    IReadOnlyList<BugReportAttachment> Attachments,
    IReadOnlyList<BugReportMessage> Messages,
    DateTimeOffset UpdatedAt);

public sealed record BugReportReplyRequest(
    string AuthorId,
    string AuthorName,
    string Text,
    string Language = "ru");

public sealed record BugReportStatusRequest(
    string Status,
    string DeveloperName);

public static class BugReportStatuses
{
    public const string New = "new";
    public const string InProgress = "in-progress";
    public const string WaitingForPlayer = "waiting-player";
    public const string Resolved = "resolved";
    public const string Closed = "closed";

    public static IReadOnlyList<string> All { get; } =
    [
        New,
        InProgress,
        WaitingForPlayer,
        Resolved,
        Closed,
    ];

    public static bool IsSupported(string? status) =>
        All.Contains(status ?? string.Empty, StringComparer.OrdinalIgnoreCase);
}

public sealed record ChatMessage(
    string Id,
    string ChannelId,
    string AuthorId,
    string AuthorName,
    string Text,
    DateTimeOffset CreatedAt,
    bool IsDeveloper = false);

public sealed record ChatMessageRequest(
    string AuthorId,
    string AuthorName,
    string Text);
