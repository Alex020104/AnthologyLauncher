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
    string? CoverUrl = null);

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
    string? Contact);

public sealed record BugReportReceipt(
    string Id,
    DateTimeOffset CreatedAt,
    string Status);

public sealed record ChatMessage(
    string Id,
    string ChannelId,
    string AuthorId,
    string AuthorName,
    string Text,
    DateTimeOffset CreatedAt,
    bool IsDeveloper = false);
