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
    IReadOnlyList<PackageManifest> Packages);

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
    IReadOnlyList<string> Files);

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
