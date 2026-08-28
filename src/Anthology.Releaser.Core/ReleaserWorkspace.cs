using Anthology.Contracts;

namespace Anthology.Releaser.Core;

public sealed class ReleaserWorkspace
{
    public int SchemaVersion { get; set; } = 1;

    public int Revision { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string UpdatedBy { get; set; } = string.Empty;

    public string Version { get; set; } = "2.1.131";

    public string Channel { get; set; } = "next";

    public List<ReleaseMirrorSet> Mirrors { get; set; } =
    [
        new() { Provider = "github", Priority = 10 },
        new() { Provider = "yandex-disk", Priority = 20 },
        new() { Provider = "google-drive", Priority = 30 },
        new() { Provider = "http", Priority = 40 },
    ];

    public List<ContentDraft> Content { get; set; } = [];
}

public sealed class ReleaseMirrorSet
{
    public string Id { get; set; } = $"source-{Guid.NewGuid():N}";

    public string Provider { get; set; } = "http";

    public string GameUrl { get; set; } = string.Empty;

    public string Mo2Url { get; set; } = string.Empty;

    public string ContentUrl { get; set; } = string.Empty;

    public int Priority { get; set; } = 100;
}

public sealed class ContentDraft
{
    public string Id { get; set; } = $"material-{Guid.NewGuid():N}";

    public ContentKind Kind { get; set; } = ContentKind.News;

    public string Section { get; set; } = "general";

    public string Title { get; set; } = "Новый материал";

    public string Summary { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public string Images { get; set; } = string.Empty;

    public string Videos { get; set; } = string.Empty;

    public string DownloadFileName { get; set; } = string.Empty;

    public long DownloadSize { get; set; }

    public string DownloadSha256 { get; set; } = string.Empty;

    public string DownloadMirrors { get; set; } = string.Empty;

    public bool IsPublished { get; set; } = true;
}

public sealed class ReleaserMachineSettings
{
    public string DeveloperName { get; set; } = Environment.UserName;

    public string GameSourceRoot { get; set; } = string.Empty;

    public string Mo2SourceRoot { get; set; } = string.Empty;

    public string OutputRoot { get; set; } = string.Empty;

    public string PrivateKeyPath { get; set; } = string.Empty;

    public string PublicKeyPath { get; set; } = string.Empty;

    public string KeyId { get; set; } = "anthology-production-01";

    public string SharedWorkspaceRoot { get; set; } = string.Empty;

    public bool AutoSync { get; set; }

    public int AutoSyncSeconds { get; set; } = 60;

    public string LastSyncedHash { get; set; } = string.Empty;

    // Local paths never enter the shared workspace.
    public Dictionary<string, string> ContentArchivePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> PublicationRoots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record UnifiedReleaseRequest(
    ReleaserWorkspace Workspace,
    ReleaserMachineSettings Machine);

public sealed record UnifiedReleaseResult(
    string Version,
    string ManifestPath,
    IReadOnlyList<string> Artifacts,
    int Files,
    long Bytes,
    int ContentItems);

public sealed record PublicationResult(
    int Targets,
    int Files,
    long Bytes,
    IReadOnlyList<string> Destinations);

public sealed record AddonPublicationResult(
    string AddonId,
    string ArtifactPath,
    string ManifestPath,
    PublicationResult Publication);

public enum WorkspaceSyncDirection
{
    None,
    Published,
    Received,
}

public sealed record WorkspaceSyncResult(
    WorkspaceSyncDirection Direction,
    ReleaserWorkspace Workspace,
    string Hash,
    string Message);
