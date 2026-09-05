using Anthology.Update.Core;

namespace Anthology.Releaser.Core;

/// <summary>
/// Defines the public paths of mutable channel documents independently from
/// immutable version artifacts. Empty directory preserves the legacy root layout.
/// </summary>
public static class ReleaseChannelLayout
{
    public const string ManifestFileName = "manifest.json";

    public static string NormalizeStableChannelDirectory(string? value)
    {
        var trimmed = value?.Trim().TrimEnd('/', '\\') ?? string.Empty;
        return trimmed.Length == 0
            ? string.Empty
            : PathSafety.NormalizeRelativePath(trimmed);
    }

    public static bool UsesDedicatedStableChannel(ReleaserWorkspace workspace) =>
        NormalizeStableChannelDirectory(workspace?.StableChannelDirectory).Length > 0;

    public static string GetStableManifestRelativePath(ReleaserWorkspace workspace) =>
        CombineStablePath(workspace, ManifestFileName);

    public static string GetStableHistoryRelativePath(ReleaserWorkspace workspace) =>
        CombineStablePath(workspace, ReleaseHistoryCatalogBuilder.FileName);

    public static string ResolveStableManifestPath(string root, ReleaserWorkspace workspace) =>
        PathSafety.ResolveUnderRoot(root, GetStableManifestRelativePath(workspace));

    public static string ResolveStableHistoryPath(string root, ReleaserWorkspace workspace) =>
        PathSafety.ResolveUnderRoot(root, GetStableHistoryRelativePath(workspace));

    public static void ValidateLauncherManifestSource(
        ReleaserWorkspace workspace,
        string? manifestSource)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!UsesDedicatedStableChannel(workspace))
        {
            return;
        }

        var expected = GetStableManifestRelativePath(workspace);
        if (string.IsNullOrWhiteSpace(manifestSource)
            || !ContainsPathAtBoundary(manifestSource.Trim(), expected))
        {
            throw new InvalidDataException(
                $"Dedicated channel '{NormalizeStableChannelDirectory(workspace.StableChannelDirectory)}' " +
                $"requires the launcher ManifestUrl to point at '{expected}'. " +
                "Keep ArtifactUrl at the version root '{version}/{file}'.");
        }
    }

    private static string CombineStablePath(ReleaserWorkspace workspace, string fileName)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var directory = NormalizeStableChannelDirectory(workspace.StableChannelDirectory);
        return directory.Length == 0 ? fileName : $"{directory}/{fileName}";
    }

    private static bool ContainsPathAtBoundary(string address, string expected)
    {
        var marker = address.LastIndexOf(expected, StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return false;
        }

        var beforeIsBoundary = marker == 0 || address[marker - 1] is '/' or '=';
        var after = marker + expected.Length;
        var afterIsBoundary = after == address.Length || address[after] is '?' or '&' or '#';
        return beforeIsBoundary && afterIsBoundary;
    }
}
