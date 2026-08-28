using System.Security.Cryptography;
using System.Text.Json;
using Anthology.Contracts;

namespace Anthology.Update.Core;

public enum UpdateStage
{
    Checking,
    Downloading,
    Verifying,
    Extracting,
    Installing,
    RollingBack,
    Completed,
}

public sealed record UpdateProgress(
    UpdateStage Stage,
    string Message,
    string? PackageId = null,
    long DownloadedBytes = 0,
    long TotalBytes = 0,
    string? Provider = null);

public sealed record PackageUpdate(
    PackageManifest Package,
    string? InstalledVersion,
    bool UpdateAvailable);

public sealed record UpdateCheckResult(
    SignedUpdateManifest SignedManifest,
    IReadOnlyList<PackageUpdate> Packages,
    string TrustedKeyId)
{
    public bool HasUpdates => Packages.Any(package => package.UpdateAvailable);
}

public sealed record UpdateApplyResult(int InstalledPackages, int InstalledFiles);

public sealed record UpdateRollbackCandidate(
    string PackageId,
    string DisplayName,
    string? FromVersion,
    string ToVersion,
    string InstallRoot,
    string OperationId,
    DateTimeOffset InstalledAt);

public sealed record UpdateRollbackResult(
    string PackageId,
    string? RestoredVersion,
    int RestoredFiles);

public sealed class UpdateCoordinator
{
    private const int MaximumManifestBytes = 4 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<IMirrorResolver>? _resolvers;

    public UpdateCoordinator(HttpClient httpClient, IEnumerable<IMirrorResolver>? resolvers = null)
    {
        _httpClient = httpClient;
        _resolvers = resolvers?.ToArray();
    }

    public async Task<UpdateCheckResult> CheckAsync(
        string manifestSource,
        string publicKeyPath,
        string channel,
        string stateRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);

        var signedManifest = await LoadManifestAsync(manifestSource, cancellationToken);
        ManifestValidator.ValidateAndThrow(signedManifest);
        if (!string.Equals(signedManifest.Payload.Channel, channel, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Manifest channel '{signedManifest.Payload.Channel}' does not match configured channel '{channel}'.");
        }

        var publicKeyFullPath = Path.GetFullPath(publicKeyPath);
        if (!File.Exists(publicKeyFullPath))
        {
            throw new FileNotFoundException("Trusted manifest public key was not found.", publicKeyFullPath);
        }

        using var publicKey = ECDsa.Create();
        publicKey.ImportFromPem(await File.ReadAllTextAsync(publicKeyFullPath, cancellationToken));
        if (!ManifestSecurity.Verify(signedManifest, publicKey))
        {
            throw new CryptographicException("Manifest signature is invalid for the selected trusted key.");
        }

        var installed = await ReadInstalledStateAsync(stateRoot, cancellationToken);
        var packages = signedManifest.Payload.Packages
            .Select(package =>
            {
                installed.Packages.TryGetValue(package.Id, out var currentVersion);
                return new PackageUpdate(
                    package,
                    currentVersion,
                    !string.Equals(currentVersion, package.Version, StringComparison.OrdinalIgnoreCase));
            })
            .ToArray();
        return new UpdateCheckResult(signedManifest, packages, signedManifest.Signature.KeyId);
    }

    public async Task<UpdateApplyResult> ApplyAsync(
        UpdateCheckResult check,
        IReadOnlyDictionary<string, string> installRoots,
        string stateRoot,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(installRoots);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);

        var pending = check.Packages.Where(package => package.UpdateAvailable).ToArray();
        if (pending.Length == 0)
        {
            return new UpdateApplyResult(0, 0);
        }

        var resolvedRoots = pending.ToDictionary(
            update => update.Package.Id,
            update => ResolveInstallRoot(update.Package, installRoots),
            StringComparer.OrdinalIgnoreCase);
        var operationRoot = Path.Combine(
            Path.GetFullPath(stateRoot),
            "work",
            $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(operationRoot);

        var downloader = new ArtifactDownloader(_httpClient, _resolvers);
        var installedState = await ReadInstalledStateAsync(stateRoot, cancellationToken);
        var history = await ReadHistoryAsync(stateRoot, cancellationToken);
        var installedPackages = 0;
        var installedFiles = 0;

        foreach (var update in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var package = update.Package;
            var packageRoot = Path.Combine(operationRoot, package.Id);
            var artifactPath = Path.Combine(packageRoot, "artifact.zip");
            var stagingRoot = Path.Combine(packageRoot, "staging");
            Directory.CreateDirectory(packageRoot);

            progress?.Report(new UpdateProgress(UpdateStage.Downloading, $"Загрузка {package.DisplayName}", package.Id, 0, package.Size));
            var downloadProgress = progress is null
                ? null
                : new Progress<DownloadProgress>(value => progress.Report(new UpdateProgress(
                    UpdateStage.Downloading,
                    $"Загрузка {package.DisplayName}",
                    package.Id,
                    value.DownloadedBytes,
                    value.TotalBytes,
                    value.Provider)));
            await downloader.DownloadAsync(package, artifactPath, downloadProgress, cancellationToken);

            progress?.Report(new UpdateProgress(UpdateStage.Verifying, $"Проверка {package.DisplayName}", package.Id, package.Size, package.Size));
            progress?.Report(new UpdateProgress(UpdateStage.Extracting, $"Распаковка {package.DisplayName}", package.Id));
            await SafeZipExtractor.ExtractAsync(artifactPath, stagingRoot, package, cancellationToken);

            progress?.Report(new UpdateProgress(UpdateStage.Installing, $"Установка {package.DisplayName}", package.Id));
            var installResult = await TransactionalFileInstaller.ApplyAsync(
                stagingRoot,
                resolvedRoots[package.Id],
                stateRoot,
                package.Files,
                cancellationToken);

            var previousVersion = update.InstalledVersion;
            try
            {
                installedState.Packages[package.Id] = package.Version;
                await WriteInstalledStateAsync(stateRoot, installedState, cancellationToken);
                history.Entries.Add(new UpdateHistoryEntry(
                    package.Id,
                    package.DisplayName,
                    previousVersion,
                    package.Version,
                    package.InstallRoot,
                    installResult.OperationId,
                    DateTimeOffset.UtcNow,
                    null));
                await WriteHistoryAsync(stateRoot, history, cancellationToken);
                installedPackages++;
                installedFiles += installResult.InstalledFiles;
            }
            catch
            {
                await TransactionalFileInstaller.RollbackAsync(
                    resolvedRoots[package.Id],
                    stateRoot,
                    installResult.OperationId,
                    CancellationToken.None);
                if (previousVersion is null)
                {
                    installedState.Packages.Remove(package.Id);
                }
                else
                {
                    installedState.Packages[package.Id] = previousVersion;
                }

                await WriteInstalledStateAsync(stateRoot, installedState, CancellationToken.None);
                throw;
            }
        }

        progress?.Report(new UpdateProgress(UpdateStage.Completed, "Обновление установлено"));
        return new UpdateApplyResult(installedPackages, installedFiles);
    }

    public static async Task<UpdateRollbackCandidate?> GetLatestRollbackAsync(
        string stateRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        var history = await ReadHistoryAsync(stateRoot, cancellationToken);
        var entry = history.Entries.LastOrDefault(item => item.RolledBackAt is null);
        return entry is null
            ? null
            : new UpdateRollbackCandidate(
                entry.PackageId,
                entry.DisplayName,
                entry.FromVersion,
                entry.ToVersion,
                entry.InstallRoot,
                entry.OperationId,
                entry.InstalledAt);
    }

    public static async Task<UpdateRollbackResult> RollbackLatestAsync(
        IReadOnlyDictionary<string, string> installRoots,
        string stateRoot,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installRoots);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        var history = await ReadHistoryAsync(stateRoot, cancellationToken);
        var index = history.Entries.FindLastIndex(item => item.RolledBackAt is null);
        if (index < 0)
        {
            throw new InvalidOperationException("Нет обновлений, доступных для отката.");
        }

        var entry = history.Entries[index];
        if (!installRoots.TryGetValue(entry.InstallRoot, out var targetRoot)
            || string.IsNullOrWhiteSpace(targetRoot))
        {
            throw new InvalidOperationException($"Корень установки '{entry.InstallRoot}' не настроен.");
        }

        progress?.Report(new UpdateProgress(
            UpdateStage.RollingBack,
            $"Откат {entry.DisplayName}",
            entry.PackageId));
        var rollback = await TransactionalFileInstaller.RollbackAsync(
            Path.GetFullPath(targetRoot),
            stateRoot,
            entry.OperationId,
            cancellationToken);

        var installedState = await ReadInstalledStateAsync(stateRoot, cancellationToken);
        if (entry.FromVersion is null)
        {
            installedState.Packages.Remove(entry.PackageId);
        }
        else
        {
            installedState.Packages[entry.PackageId] = entry.FromVersion;
        }

        history.Entries[index] = entry with { RolledBackAt = DateTimeOffset.UtcNow };
        await WriteInstalledStateAsync(stateRoot, installedState, cancellationToken);
        await WriteHistoryAsync(stateRoot, history, cancellationToken);
        progress?.Report(new UpdateProgress(UpdateStage.Completed, "Предыдущая версия восстановлена"));
        return new UpdateRollbackResult(entry.PackageId, entry.FromVersion, rollback.RestoredFiles);
    }

    private async Task<SignedUpdateManifest> LoadManifestAsync(
        string source,
        CancellationToken cancellationToken)
    {
        Stream stream;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
            {
                throw new InvalidDataException("Remote manifest must use HTTPS.");
            }

            using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumManifestBytes)
            {
                throw new InvalidDataException("Manifest exceeds the 4 MiB safety limit.");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length > MaximumManifestBytes)
            {
                throw new InvalidDataException("Manifest exceeds the 4 MiB safety limit.");
            }

            stream = new MemoryStream(bytes, writable: false);
        }
        else
        {
            var path = Path.GetFullPath(source);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Update manifest was not found.", path);
            }

            var file = new FileInfo(path);
            if (file.Length > MaximumManifestBytes)
            {
                throw new InvalidDataException("Manifest exceeds the 4 MiB safety limit.");
            }

            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        }

        await using (stream)
        {
            return await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(stream, ManifestJson.Options, cancellationToken)
                ?? throw new InvalidDataException("Manifest is empty or invalid JSON.");
        }
    }

    private static string ResolveInstallRoot(
        PackageManifest package,
        IReadOnlyDictionary<string, string> installRoots)
    {
        if (!installRoots.TryGetValue(package.InstallRoot, out var root) || string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException(
                $"Install root '{package.InstallRoot}' is not configured for package '{package.Id}'.");
        }

        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Install root does not exist: {fullRoot}");
        }

        return fullRoot;
    }

    private static async Task<InstalledState> ReadInstalledStateAsync(
        string stateRoot,
        CancellationToken cancellationToken)
    {
        var path = GetInstalledStatePath(stateRoot);
        if (!File.Exists(path))
        {
            return new InstalledState(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, FileOptions.Asynchronous);
        var state = await JsonSerializer.DeserializeAsync<InstalledState>(stream, ManifestJson.Options, cancellationToken)
            ?? new InstalledState([]);
        return new InstalledState(new Dictionary<string, string>(state.Packages, StringComparer.OrdinalIgnoreCase));
    }

    private static async Task WriteInstalledStateAsync(
        string stateRoot,
        InstalledState state,
        CancellationToken cancellationToken)
    {
        var path = GetInstalledStatePath(stateRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, state, ManifestJson.Options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporary, path, true);
    }

    private static string GetInstalledStatePath(string stateRoot) =>
        Path.Combine(Path.GetFullPath(stateRoot), "installed-packages.json");

    private static async Task<UpdateHistory> ReadHistoryAsync(
        string stateRoot,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetFullPath(stateRoot), "update-history.json");
        if (!File.Exists(path))
        {
            return new UpdateHistory([]);
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<UpdateHistory>(stream, ManifestJson.Options, cancellationToken)
            ?? new UpdateHistory([]);
    }

    private static async Task WriteHistoryAsync(
        string stateRoot,
        UpdateHistory history,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetFullPath(stateRoot), "update-history.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, history, ManifestJson.Options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporary, path, true);
    }

    private sealed record InstalledState(Dictionary<string, string> Packages);

    private sealed record UpdateHistory(List<UpdateHistoryEntry> Entries);

    private sealed record UpdateHistoryEntry(
        string PackageId,
        string DisplayName,
        string? FromVersion,
        string ToVersion,
        string InstallRoot,
        string OperationId,
        DateTimeOffset InstalledAt,
        DateTimeOffset? RolledBackAt);
}
