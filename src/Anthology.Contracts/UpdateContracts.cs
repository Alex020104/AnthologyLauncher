namespace Anthology.Contracts;

public sealed record SignedUpdateManifest(
    UpdateManifest Payload,
    ManifestSignature Signature);

public sealed record UpdateManifest(
    int SchemaVersion,
    string Channel,
    string Version,
    DateTimeOffset PublishedAt,
    string? MinimumLauncherVersion,
    IReadOnlyList<PackageManifest> Packages,
    ContentCatalog? Content = null);

public sealed record ManifestSignature(
    string Algorithm,
    string KeyId,
    string Value);

public sealed record PackageManifest(
    string Id,
    string DisplayName,
    string Version,
    PackageKind Kind,
    string InstallRoot,
    string ArchiveFormat,
    long Size,
    string Sha256,
    IReadOnlyList<MirrorManifest> Mirrors,
    IReadOnlyList<string> Files,
    PackageUpdateMode UpdateMode = PackageUpdateMode.Merge,
    bool PruneInstallRoot = false,
    IReadOnlyList<string>? PreservedPaths = null);

public sealed record MirrorManifest(
    string Provider,
    string Url,
    int Priority = 100,
    string? Region = null);

public enum PackageKind
{
    Launcher,
    Game,
    Modpack,
    Database,
    Engine,
    Mod,
    Tool,
}

public enum PackageUpdateMode
{
    Merge,
    ManagedExact,
}

public sealed record ContentCatalog(
    int SchemaVersion,
    string Version,
    DateTimeOffset PublishedAt,
    IReadOnlyList<ContentDocument> Items);

public sealed record ContentDocument(
    string Id,
    ContentKind Kind,
    string Section,
    string Title,
    string Summary,
    string Body,
    IReadOnlyList<string> Images,
    IReadOnlyList<ContentVideo> Videos,
    ContentDownload? Download = null);

public sealed record ContentVideo(
    string Title,
    string Url);

public sealed record ContentDownload(
    string FileName,
    long Size,
    string Sha256,
    IReadOnlyList<MirrorManifest> Mirrors);

public enum ContentKind
{
    Mod,
    News,
    Information,
}
