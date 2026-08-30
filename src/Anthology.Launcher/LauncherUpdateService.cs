using Anthology.Update.Core;
using System.Net.Http;
using System.Text.Json;
using Anthology.Contracts;
using System.IO;

namespace Anthology.Launcher;

public sealed class LauncherUpdateService(
    HttpClient httpClient,
    LauncherSettingsStore settingsStore)
{
    private readonly UpdateCoordinator _coordinator = new(httpClient);

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.Current;
        var result = await _coordinator.CheckAsync(
            settings.ManifestSource,
            settings.PublicKeyPath,
            settings.UpdateChannel,
            settingsStore.UpdaterStateRoot,
            cancellationToken);
        await CacheVerifiedManifestAsync(result.SignedManifest, cancellationToken);
        return result;
    }

    public async Task<ContentCatalog?> LoadCachedContentAsync(CancellationToken cancellationToken = default)
    {
        var cachePath = GetManifestCachePath();
        var settings = settingsStore.Current;
        if (!File.Exists(cachePath)
            || string.IsNullOrWhiteSpace(settings.PublicKeyPath)
            || !File.Exists(settings.PublicKeyPath))
        {
            return null;
        }

        try
        {
            var check = await _coordinator.CheckAsync(
                cachePath,
                settings.PublicKeyPath,
                settings.UpdateChannel,
                settingsStore.UpdaterStateRoot,
                cancellationToken);
            return check.SignedManifest.Payload.Content;
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidDataException
                                           or UnauthorizedAccessException
                                           or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public async Task<string> DownloadContentAsync(
        ContentDocument content,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var download = content.Download ?? throw new InvalidOperationException("У материала нет файла для загрузки.");
        var fileName = Path.GetFileName(download.FileName);
        if (string.IsNullOrWhiteSpace(fileName)
            || !string.Equals(fileName, download.FileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Имя файла материала небезопасно.");
        }

        var settings = settingsStore.Current;
        var downloadRoot = !string.IsNullOrWhiteSpace(settings.ModpackRoot)
            ? Path.Combine(settings.ModpackRoot, "downloads")
            : Path.Combine(settingsStore.DataRoot, "Downloads");
        Directory.CreateDirectory(downloadRoot);
        var destination = Path.Combine(downloadRoot, fileName);
        var package = new PackageManifest(
            $"content-{content.Id}",
            content.Title,
            "content",
            PackageKind.Mod,
            "mods",
            "archive",
            download.Size,
            download.Sha256,
            download.Mirrors,
            [fileName]);
        var downloader = new ArtifactDownloader(httpClient);
        await downloader.DownloadAsync(
            package,
            destination,
            progress,
            settings.PreferredMirrorProvider,
            cancellationToken);
        return destination;
    }

    public Task<UpdateApplyResult> ApplyAsync(
        UpdateCheckResult check,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.Current;
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddRoots(roots, settings.GameRoot, "game", "engine", "database");
        AddRoots(roots, settings.ModpackRoot, "modpack", "mods", "tools");
        return _coordinator.ApplyAsync(
            check,
            roots,
            settingsStore.UpdaterStateRoot,
            progress,
            settings.PreferredMirrorProvider,
            cancellationToken);
    }

    public Task<UpdateApplyResult> ApplyLauncherOnlyAsync(
        UpdateCheckResult check,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        var launcherPackages = check.Packages
            .Where(update => update.UpdateAvailable && update.Package.Kind == PackageKind.Launcher)
            .ToArray();
        if (launcherPackages.Length == 0)
        {
            return Task.FromResult(new UpdateApplyResult(0, 0, 0));
        }

        return ApplyAsync(
            new UpdateCheckResult(check.SignedManifest, launcherPackages, check.TrustedKeyId),
            progress,
            cancellationToken);
    }

    public Task<UpdateRollbackCandidate?> GetLatestRollbackAsync(CancellationToken cancellationToken = default) =>
        UpdateCoordinator.GetLatestRollbackAsync(settingsStore.UpdaterStateRoot, cancellationToken);

    public Task<UpdateRollbackResult> RollbackLatestAsync(
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.Current;
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddRoots(roots, settings.GameRoot, "game", "engine", "database");
        AddRoots(roots, settings.ModpackRoot, "modpack", "mods", "tools");
        return UpdateCoordinator.RollbackLatestAsync(
            roots,
            settingsStore.UpdaterStateRoot,
            progress,
            cancellationToken);
    }

    private static void AddRoots(
        Dictionary<string, string> roots,
        string? path,
        params string[] names)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        foreach (var name in names)
        {
            roots[name] = path;
        }
    }

    private async Task CacheVerifiedManifestAsync(
        SignedUpdateManifest manifest,
        CancellationToken cancellationToken)
    {
        var path = GetManifestCachePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, ManifestJson.Options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporary, path, true);
    }

    private string GetManifestCachePath() =>
        Path.Combine(settingsStore.UpdaterStateRoot, "last-verified-manifest.json");
}
