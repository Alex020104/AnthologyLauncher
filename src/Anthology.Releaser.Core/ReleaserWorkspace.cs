using Anthology.Contracts;

namespace Anthology.Releaser.Core;

public sealed class ReleaserWorkspace
{
    public int SchemaVersion { get; set; } = 3;

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

    public DateTimeOffset? PublishedAt { get; set; }

    public string SourceLanguage { get; set; } = "auto";

    public Dictionary<string, ContentTranslationDraft> Translations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string TitleEn { get; set; } = string.Empty;

    public string SummaryEn { get; set; } = string.Empty;

    public string BodyEn { get; set; } = string.Empty;

    public string TitleDe { get; set; } = string.Empty;

    public string SummaryDe { get; set; } = string.Empty;

    public string BodyDe { get; set; } = string.Empty;

    public string Images { get; set; } = string.Empty;

    public string Videos { get; set; } = string.Empty;

    public List<ContentBlockDraft> Blocks { get; set; } = [];

    public string DownloadFileName { get; set; } = string.Empty;

    public long DownloadSize { get; set; }

    public string DownloadSha256 { get; set; } = string.Empty;

    public string DownloadMirrors { get; set; } = string.Empty;

    public string InstallFolderName { get; set; } = string.Empty;

    public bool IsPublished { get; set; }

    public ContentTranslationDraft Translation(string language)
    {
        var normalized = AnthologyLanguages.Normalize(language);
        if (!Translations.TryGetValue(normalized, out var translation))
        {
            translation = new ContentTranslationDraft();
            Translations[normalized] = translation;
        }
        return translation;
    }
}

public sealed class ContentTranslationDraft
{
    public string Title { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;
}

public sealed class ContentBlockDraft
{
    public string Id { get; set; } = $"block-{Guid.NewGuid():N}";

    public ContentBlockKind Kind { get; set; } = ContentBlockKind.Section;

    public string Title { get; set; } = "Новый заголовок";

    public string Body { get; set; } = string.Empty;

    public Dictionary<string, ContentBlockTranslationDraft> Translations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string TitleEn { get; set; } = string.Empty;

    public string BodyEn { get; set; } = string.Empty;

    public string TitleDe { get; set; } = string.Empty;

    public string BodyDe { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public ContentBlockTranslationDraft Translation(string language)
    {
        var normalized = AnthologyLanguages.Normalize(language);
        if (!Translations.TryGetValue(normalized, out var translation))
        {
            translation = new ContentBlockTranslationDraft();
            Translations[normalized] = translation;
        }
        return translation;
    }
}

public sealed class ContentBlockTranslationDraft
{
    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;
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

    public string TranslationApiUrl { get; set; } = "http://127.0.0.1:5000";

    public string TranslationApiKey { get; set; } = string.Empty;

    // Local paths never enter the shared workspace.
    public Dictionary<string, string> ContentArchivePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Image sources stay local to this developer machine. The signed catalog only receives
    // HTTPS URLs after the files have been copied into every configured publication root.
    public Dictionary<string, List<string>> ContentImagePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<QuickReleaseFileDraft> QuickReleaseFiles { get; set; } = [];

    public List<QuickDeleteFileDraft> QuickDeleteFiles { get; set; } = [];

    public Dictionary<string, string> PublicationRoots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class QuickReleaseFileDraft
{
    public string Id { get; set; } = $"file-{Guid.NewGuid():N}";

    public string SourcePath { get; set; } = string.Empty;

    public string InstallRoot { get; set; } = "game";

    public string RelativePath { get; set; } = string.Empty;
}

public sealed class QuickDeleteFileDraft
{
    public string Id { get; set; } = $"delete-{Guid.NewGuid():N}";

    public string InstallRoot { get; set; } = "game";

    public string RelativePath { get; set; } = string.Empty;
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

public sealed record QuickReleaseResult(
    string ManifestPath,
    int AddedFiles,
    int DeletedFiles,
    IReadOnlyList<string> Artifacts,
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
