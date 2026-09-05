using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Concurrent;
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
    bool UpdateAvailable,
    bool RepairRequired = false,
    IReadOnlyList<string>? RepairFiles = null,
    IReadOnlyList<PackageFileIntegrity>? ExpectedIntegrity = null,
    bool TrackInstallation = true);

public sealed record UpdateCheckResult(
    SignedUpdateManifest SignedManifest,
    IReadOnlyList<PackageUpdate> Packages,
    string TrustedKeyId)
{
    public bool HasUpdates => Packages.Any(package => package.UpdateAvailable);
}

public sealed record UpdateApplyResult(
    int InstalledPackages,
    int InstalledFiles,
    int DeletedFiles = 0);

public sealed record UpdateRollbackCandidate(
    string PackageId,
    string DisplayName,
    string? FromVersion,
    string ToVersion,
    string InstallRoot,
    string OperationId,
    DateTimeOffset InstalledAt,
    int PackageCount = 1);

public sealed record UpdateRollbackResult(
    string PackageId,
    string? RestoredVersion,
    int RestoredFiles);

public sealed class UpdateCoordinator
{
    // Schema 5 can carry hashes for a complete game and MO2 tree. Keep a hard
    // upper bound and enforce it while streaming so a response without a
    // Content-Length header cannot exhaust memory.
    private const int MaximumManifestBytes = 128 * 1024 * 1024;
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
        => await CheckCoreAsync(
            manifestSource,
            publicKeyPath,
            channel,
            stateRoot,
            null,
            null,
            cancellationToken);

    public async Task<UpdateCheckResult> CheckAsync(
        string manifestSource,
        string publicKeyPath,
        string channel,
        string stateRoot,
        IReadOnlyDictionary<string, string> installRoots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installRoots);
        return await CheckCoreAsync(
            manifestSource,
            publicKeyPath,
            channel,
            stateRoot,
            installRoots,
            GetDefaultIntegrityCatalogPath(installRoots),
            cancellationToken);
    }

    public async Task<UpdateCheckResult> CheckAsync(
        string manifestSource,
        string publicKeyPath,
        string channel,
        string stateRoot,
        IReadOnlyDictionary<string, string> installRoots,
        string? integrityCatalogPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installRoots);
        return await CheckCoreAsync(
            manifestSource,
            publicKeyPath,
            channel,
            stateRoot,
            installRoots,
            integrityCatalogPath,
            cancellationToken);
    }

    private async Task<UpdateCheckResult> CheckCoreAsync(
        string manifestSource,
        string publicKeyPath,
        string channel,
        string stateRoot,
        IReadOnlyDictionary<string, string>? installRoots,
        string? integrityCatalogPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);

        // A terminated process cannot clean its own operation directory. Remove
        // only old updater workspaces; active/new directories are left alone.
        CleanupStaleWorkDirectories(stateRoot);

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
        var existingHistory = await ReadHistoryAsync(stateRoot, cancellationToken);
        CompactActiveRollbackTransactions(stateRoot, existingHistory);
        CleanupRollbackStorage(
            stateRoot,
            existingHistory,
            TimeSpan.FromHours(1));
        var integrityCatalogLoad = await ReadVerifiedIntegrityCatalogAsync(
            integrityCatalogPath,
            signedManifest,
            publicKey,
            cancellationToken);
        var integrityCatalog = integrityCatalogLoad.Catalog;
        var packages = new List<PackageUpdate>(signedManifest.Payload.Packages.Count);
        var regularUpdates = new Dictionary<string, PackageUpdate>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in signedManifest.Payload.Packages)
        {
            installed.Packages.TryGetValue(package.Id, out var currentVersion);
            var versionChanged = !string.Equals(currentVersion, package.Version, StringComparison.OrdinalIgnoreCase);
            var expectedIntegrity = package.LooseFiles?.Select(file => new PackageFileIntegrity(
                    PathSafety.NormalizeRelativePath(file.Path),
                    file.Size,
                    file.Sha256.ToLowerInvariant()))
                .ToArray()
                ?? FindArchiveIntegrity(integrityCatalog, package);
            var assessment = IntegrityAssessment.Intact;
            IReadOnlyList<string>? looseDeltaFiles = null;
            if (package.LooseFiles is not null
                && installRoots is not null
                && package.Kind != PackageKind.Launcher)
            {
                assessment = await AssessIntegrityAsync(
                    package.InstallRoot,
                    expectedIntegrity ?? [],
                    installRoots,
                    cancellationToken);
                // For a new version this is a delta install list, while for the
                // same version it is the ordinary repair list. An empty list is
                // meaningful: every signed file is already present and valid.
                looseDeltaFiles = assessment.RepairFiles;
            }
            else if (!versionChanged
                && string.Equals(package.Id, "anthology-integrity", StringComparison.OrdinalIgnoreCase)
                && integrityCatalogLoad.RequiresCatalogRepair)
            {
                assessment = new IntegrityAssessment(
                    true,
                    package.Files.Select(PathSafety.NormalizeRelativePath).ToArray());
            }
            else if (!versionChanged
                && installRoots is not null
                && package.Kind != PackageKind.Launcher
                && integrityCatalog is null)
            {
                // A baseline written from a previously verified archive is safe to
                // use. Never create a baseline from files that merely happen to be
                // present on the player's machine.
                var localBaseline = await ReadManagedIntegrityAsync(stateRoot, package.Id, cancellationToken);
                if (localBaseline is not null
                    && string.Equals(localBaseline.PackageVersion, package.Version, StringComparison.OrdinalIgnoreCase))
                {
                    var currentPaths = package.GetFilePaths()
                        .Select(PathSafety.NormalizeRelativePath)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var expectedCurrentFiles = localBaseline.Files
                        .Where(file => currentPaths.Contains(PathSafety.NormalizeRelativePath(file.Path)))
                        .ToArray();
                    assessment = await AssessIntegrityAsync(
                        package.InstallRoot,
                        expectedCurrentFiles,
                        installRoots,
                        cancellationToken);
                    expectedIntegrity ??= expectedCurrentFiles;
                }
            }

            var repairRequired = !versionChanged && assessment.RequiresRepair;
            var update = new PackageUpdate(
                package,
                currentVersion,
                versionChanged || assessment.RequiresRepair,
                repairRequired,
                looseDeltaFiles ?? (assessment.RequiresRepair ? assessment.RepairFiles : null),
                expectedIntegrity);
            packages.Add(update);
            regularUpdates[package.Id] = update;
        }

        if (integrityCatalog is not null && installRoots is not null)
        {
            foreach (var artifact in integrityCatalog.Payload.Artifacts)
            {
                var owner = signedManifest.Payload.Packages.FirstOrDefault(package =>
                    string.Equals(package.Id, artifact.PackageId, StringComparison.OrdinalIgnoreCase));
                if (owner is null
                    || owner.Kind == PackageKind.Launcher
                    || !regularUpdates.TryGetValue(owner.Id, out var ownerUpdate)
                    || ownerUpdate.UpdateAvailable
                    || !installed.Packages.TryGetValue(owner.Id, out var installedVersion)
                    || !string.Equals(installedVersion, artifact.RequiredPackageVersion, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(owner.Version, artifact.RequiredPackageVersion, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var archiveFiles = artifact.ArchiveFiles.ToDictionary(
                    file => PathSafety.NormalizeRelativePath(file.Path),
                    StringComparer.OrdinalIgnoreCase);
                var expectedManagedFiles = artifact.ManagedFiles
                    .Select(path => archiveFiles[PathSafety.NormalizeRelativePath(path)])
                    .ToArray();
                var assessment = await AssessIntegrityAsync(
                    artifact.InstallRoot,
                    expectedManagedFiles,
                    installRoots,
                    cancellationToken);
                if (!assessment.RequiresRepair)
                {
                    continue;
                }

                var repairPackage = CreateRepairPackage(artifact);
                packages.Add(new PackageUpdate(
                    repairPackage,
                    installedVersion,
                    true,
                    true,
                    assessment.RepairFiles,
                    artifact.ArchiveFiles,
                    false));
            }
        }
        return new UpdateCheckResult(signedManifest, packages, signedManifest.Signature.KeyId);
    }

    private static string? GetDefaultIntegrityCatalogPath(
        IReadOnlyDictionary<string, string> installRoots)
    {
        if (!installRoots.TryGetValue("game", out var gameRoot) || string.IsNullOrWhiteSpace(gameRoot))
        {
            return null;
        }

        return Path.Combine(
            Path.GetFullPath(gameRoot),
            "AnthologyLauncher",
            "Update",
            "Integrity",
            "package-integrity.json");
    }

    private static IReadOnlyList<PackageFileIntegrity>? FindArchiveIntegrity(
        SignedPackageIntegrityCatalog? catalog,
        PackageManifest package) =>
        catalog?.Payload.Artifacts.FirstOrDefault(artifact =>
            string.Equals(artifact.PackageId, package.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(artifact.PackageVersion, package.Version, StringComparison.OrdinalIgnoreCase)
            && string.Equals(artifact.InstallRoot, package.InstallRoot, StringComparison.OrdinalIgnoreCase)
            && string.Equals(artifact.ArchiveSha256, package.Sha256, StringComparison.OrdinalIgnoreCase))?.ArchiveFiles;

    private static PackageManifest CreateRepairPackage(PackageArtifactIntegrity artifact) => new(
        $"repair-{artifact.ArtifactId}",
        $"Восстановление файлов {artifact.PackageId}",
        artifact.PackageVersion,
        artifact.Kind,
        artifact.InstallRoot,
        artifact.ArchiveFormat,
        artifact.ArchiveSize,
        artifact.ArchiveSha256,
        artifact.Mirrors,
        artifact.ArchiveFiles.Select(file => file.Path).ToArray(),
        PackageUpdateMode.Merge);

    private static async Task<IntegrityCatalogLoadResult> ReadVerifiedIntegrityCatalogAsync(
        string? catalogPath,
        SignedUpdateManifest manifest,
        ECDsa publicKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(catalogPath))
        {
            return new IntegrityCatalogLoadResult(null, false);
        }

        var fullPath = Path.GetFullPath(catalogPath);
        if (!File.Exists(fullPath))
        {
            return new IntegrityCatalogLoadResult(null, true);
        }
        try
        {
            var info = new FileInfo(fullPath);
            if (info.Length > 64L * 1024 * 1024)
            {
                return new IntegrityCatalogLoadResult(null, true);
            }

            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var catalog = await JsonSerializer.DeserializeAsync<SignedPackageIntegrityCatalog>(
                stream,
                ManifestJson.Options,
                cancellationToken);
            if (catalog is null)
            {
                return new IntegrityCatalogLoadResult(null, true);
            }
            PackageIntegrityCatalogValidator.ValidateAndThrow(catalog);
            if (!string.Equals(catalog.Signature.KeyId, manifest.Signature.KeyId, StringComparison.Ordinal)
                || !ManifestSecurity.Verify(catalog, publicKey))
            {
                return new IntegrityCatalogLoadResult(null, true);
            }

            // Content-only releases may keep the exact same signed integrity package.
            // The per-artifact RequiredPackageVersion check below prevents a catalog
            // from auditing a package whose binary payload changed, so a release
            // number mismatch alone does not make an otherwise valid catalog stale.
            if (!string.Equals(catalog.Payload.Channel, manifest.Payload.Channel, StringComparison.OrdinalIgnoreCase))
            {
                return new IntegrityCatalogLoadResult(null, false);
            }

            return new IntegrityCatalogLoadResult(catalog, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException
                                           or InvalidDataException
                                           or CryptographicException)
        {
            // Do not trust the damaged catalog. Its ordinary signed package is
            // repaired below from the archive hash in the main manifest.
            return new IntegrityCatalogLoadResult(null, true);
        }
    }

    private static async Task<IntegrityAssessment> AssessIntegrityAsync(
        string installRoot,
        IReadOnlyCollection<PackageFileIntegrity> expectedFiles,
        IReadOnlyDictionary<string, string> installRoots,
        CancellationToken cancellationToken)
    {
        if (expectedFiles.Count == 0)
        {
            return IntegrityAssessment.Intact;
        }
        if (!installRoots.TryGetValue(installRoot, out var configuredRoot)
            || string.IsNullOrWhiteSpace(configuredRoot)
            || !Directory.Exists(Path.GetFullPath(configuredRoot)))
        {
            return IntegrityAssessment.Intact;
        }

        var root = Path.GetFullPath(configuredRoot);
        var damaged = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(
            expectedFiles,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 4,
            },
            async (expected, token) =>
            {
                var normalized = PathSafety.NormalizeRelativePath(expected.Path);
                var path = PathSafety.ResolveUnderRoot(root, normalized);
                if (!File.Exists(path))
                {
                    damaged.Add(normalized);
                    return;
                }

                try
                {
                    if (new FileInfo(path).Length != expected.Size
                        || !string.Equals(
                            await ArtifactHash.ComputeSha256Async(path, token),
                            expected.Sha256,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        damaged.Add(normalized);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A managed file that cannot be read is not known-good. Let
                    // the transactional repair report the concrete access error.
                    damaged.Add(normalized);
                }
            });

        return damaged.IsEmpty
            ? IntegrityAssessment.Intact
            : new IntegrityAssessment(
                true,
                damaged.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task<UpdateApplyResult> ApplyAsync(
        UpdateCheckResult check,
        IReadOnlyDictionary<string, string> installRoots,
        string stateRoot,
        IProgress<UpdateProgress>? progress = null,
        string? preferredMirrorProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(installRoots);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);

        var pending = check.Packages.Where(package => package.UpdateAvailable).ToArray();
        if (pending.Length == 0)
        {
            return new UpdateApplyResult(0, 0, 0);
        }

        foreach (var update in pending)
        {
            // ApplyAsync is public, so do not assume every caller obtained this
            // result from CheckAsync in the same process.
            PackageInstallScopePolicy.ValidateAndThrow(update.Package);
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
        var looseDownloader = new LoosePackageDownloader(_httpClient, _resolvers);
        var installedState = await ReadInstalledStateAsync(stateRoot, cancellationToken);
        var history = await ReadHistoryAsync(stateRoot, cancellationToken);
        var historyBeforeApply = history.Entries.ToArray();
        var batchId = $"release-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}";
        var applied = new List<AppliedPackage>();
        var validatedRollbackArchives = new HashSet<string>(StringComparer.Ordinal);
        var installedPackages = 0;
        var installedFiles = 0;
        var deletedFiles = 0;
        var updateSucceeded = false;

        try
        {
            foreach (var update in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var package = update.Package;
                var packageRoot = Path.Combine(operationRoot, package.Id);
                var artifactPath = Path.Combine(packageRoot, "artifact.zip");
                var stagingRoot = Path.Combine(packageRoot, "staging");
                Directory.CreateDirectory(packageRoot);
                var declaredFiles = package.GetFilePaths();
                var installPaths = update.RepairFiles is not null
                    ? update.RepairFiles
                    : declaredFiles;
                if (package.LooseFiles is not null)
                {
                    var selected = installPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var downloadSize = package.LooseFiles
                        .Where(file => selected.Contains(PathSafety.NormalizeRelativePath(file.Path)))
                        .Aggregate(0L, static (total, file) => checked(total + file.Size));
                    progress?.Report(new UpdateProgress(
                        UpdateStage.Downloading,
                        $"Загрузка файлов {package.DisplayName}",
                        package.Id,
                        0,
                        downloadSize));
                    var looseProgress = progress is null
                        ? null
                        : new Progress<DownloadProgress>(value => progress.Report(new UpdateProgress(
                            UpdateStage.Downloading,
                            $"Загрузка файлов {package.DisplayName}",
                            package.Id,
                            value.DownloadedBytes,
                            value.TotalBytes,
                            value.Provider)));
                    await looseDownloader.DownloadAsync(
                        package,
                        stagingRoot,
                        installPaths,
                        looseProgress,
                        preferredMirrorProvider,
                        cancellationToken);
                    progress?.Report(new UpdateProgress(
                        UpdateStage.Verifying,
                        $"Файлы {package.DisplayName} проверены",
                        package.Id,
                        downloadSize,
                        downloadSize));
                }
                else
                {
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
                    await downloader.DownloadAsync(
                        package,
                        artifactPath,
                        downloadProgress,
                        preferredMirrorProvider,
                        cancellationToken);
                    progress?.Report(new UpdateProgress(UpdateStage.Verifying, $"Проверка {package.DisplayName}", package.Id, package.Size, package.Size));
                    progress?.Report(new UpdateProgress(UpdateStage.Extracting, $"Распаковка {package.DisplayName}", package.Id));
                    await SafeZipExtractor.ExtractAsync(
                        artifactPath,
                        stagingRoot,
                        package,
                        update.ExpectedIntegrity,
                        installPaths,
                        cancellationToken);
                }

                var previousManagedFiles = update.TrackInstallation
                    ? await ReadManagedFilesAsync(stateRoot, package.Id, cancellationToken)
                    : [];
                var previousIntegrity = update.TrackInstallation
                    ? await ReadManagedIntegrityAsync(stateRoot, package.Id, cancellationToken)
                    : null;
                var nextIntegrity = update.TrackInstallation
                    ? update.ExpectedIntegrity is { Count: > 0 }
                        ? CreateManagedIntegrity(package, update.ExpectedIntegrity, previousIntegrity)
                        : await ComputeManagedIntegrityAsync(package, stagingRoot, previousIntegrity, cancellationToken)
                    : null;
                List<string> obsoleteFiles = update.RepairRequired
                    ? []
                    : (package.DeletedFiles ?? []).ToList();
                foreach (var directory in update.RepairRequired
                    ? Array.Empty<string>()
                    : package.DeletedDirectories ?? [])
                {
                    obsoleteFiles.AddRange(EnumerateDirectoryFiles(resolvedRoots[package.Id], directory));
                }
                if (!update.RepairRequired && package.UpdateMode == PackageUpdateMode.ManagedExact)
                {
                    obsoleteFiles.AddRange(
                        previousManagedFiles.Except(declaredFiles, StringComparer.OrdinalIgnoreCase));
                }
                if (!update.RepairRequired && package.PruneInstallRoot)
                {
                    obsoleteFiles.AddRange(EnumeratePrunableFiles(
                        resolvedRoots[package.Id],
                        package.PreservedPaths ?? [])
                        .Except(declaredFiles, StringComparer.OrdinalIgnoreCase));
                }
                var distinctObsoleteFiles = obsoleteFiles
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                PackageInstallScopePolicy.ValidateResolvedTargetsAndThrow(
                    package,
                    installPaths.Concat(distinctObsoleteFiles));
                progress?.Report(new UpdateProgress(UpdateStage.Installing, $"Установка {package.DisplayName}", package.Id));
                var installResult = await TransactionalFileInstaller.ApplyAsync(
                    stagingRoot,
                    resolvedRoots[package.Id],
                    stateRoot,
                    installPaths,
                    distinctObsoleteFiles,
                    cancellationToken);

                applied.Add(new AppliedPackage(
                    update,
                    installResult,
                    previousManagedFiles,
                    previousIntegrity,
                    nextIntegrity));
                foreach (var directory in update.RepairRequired
                    ? Array.Empty<string>()
                    : package.DeletedDirectories ?? [])
                {
                    DeleteEmptyDirectoryTree(resolvedRoots[package.Id], directory);
                }
            }

            foreach (var item in applied)
            {
                var package = item.Update.Package;
                if (item.Update.TrackInstallation && !item.Update.RepairRequired)
                {
                    await WriteManagedSnapshotAsync(stateRoot, item.Install.OperationId, item.PreviousManagedFiles, cancellationToken);
                    await WriteManagedIntegritySnapshotAsync(
                        stateRoot,
                        item.Install.OperationId,
                        item.PreviousIntegrity,
                        cancellationToken);
                    await ArchiveTransactionAsync(
                        stateRoot,
                        item.Install.OperationId,
                        cancellationToken);
                    validatedRollbackArchives.Add(item.Install.OperationId);
                }
                if (item.Update.TrackInstallation)
                {
                    var nextManagedFiles = MergeManagedFiles(package, item.PreviousManagedFiles);
                    await WriteManagedFilesAsync(stateRoot, package.Id, nextManagedFiles, cancellationToken);
                    await WriteManagedIntegrityAsync(stateRoot, package.Id, item.NextIntegrity, cancellationToken);
                    installedState.Packages[package.Id] = package.Version;
                    if (!item.Update.RepairRequired)
                    {
                        history.Entries.Add(new UpdateHistoryEntry(
                            package.Id,
                            package.DisplayName,
                            item.Update.InstalledVersion,
                            package.Version,
                            package.InstallRoot,
                            item.Install.OperationId,
                            DateTimeOffset.UtcNow,
                            null,
                            batchId,
                            item.Install.DeletedFiles));
                    }
                }
                installedPackages++;
                installedFiles += item.Install.InstalledFiles;
                deletedFiles += item.Install.DeletedFiles;
            }

            await WriteInstalledStateAsync(stateRoot, installedState, cancellationToken);
            PruneHistoryToLatestBatch(
                history,
                applied.Any(item => item.Update.TrackInstallation && !item.Update.RepairRequired) ? batchId : null);
            await WriteHistoryAsync(stateRoot, history, cancellationToken);
            CompactActiveRollbackTransactions(stateRoot, history, validatedRollbackArchives);
            CleanupRollbackStorage(stateRoot, history);
            updateSucceeded = true;
        }
        catch (Exception updateError)
        {
            progress?.Report(new UpdateProgress(UpdateStage.RollingBack, "Ошибка обновления — возвращаем всю предыдущую сборку"));
            var rollbackErrors = new List<Exception>();
            foreach (var item in applied.AsEnumerable().Reverse())
            {
                var package = item.Update.Package;
                try
                {
                    await TransactionalFileInstaller.RollbackAsync(
                        resolvedRoots[package.Id],
                        stateRoot,
                        item.Install.OperationId,
                        CancellationToken.None);
                }
                catch (Exception rollbackError) when (rollbackError is IOException
                                                       or InvalidDataException
                                                       or InvalidOperationException
                                                       or UnauthorizedAccessException)
                {
                    rollbackErrors.Add(rollbackError);
                }
                finally
                {
                    if (item.Update.TrackInstallation)
                    {
                        await WriteManagedFilesAsync(stateRoot, package.Id, item.PreviousManagedFiles, CancellationToken.None);
                        await WriteManagedIntegrityAsync(
                            stateRoot,
                            package.Id,
                            item.PreviousIntegrity,
                            CancellationToken.None);
                        if (item.Update.InstalledVersion is null)
                        {
                            installedState.Packages.Remove(package.Id);
                        }
                        else installedState.Packages[package.Id] = item.Update.InstalledVersion;
                    }
                }
            }

            // Pruning the new history is part of the commit. If any later commit
            // step fails, restore the previous rollback batch verbatim so a failed
            // update can never destroy the player's last known-good rollback.
            history.Entries.Clear();
            history.Entries.AddRange(historyBeforeApply);

            await WriteInstalledStateAsync(stateRoot, installedState, CancellationToken.None);
            await WriteHistoryAsync(stateRoot, history, CancellationToken.None);
            if (rollbackErrors.Count > 0)
            {
                throw new AggregateException(
                    "Обновление остановлено, но часть резервных копий не удалось восстановить.",
                    [updateError, .. rollbackErrors]);
            }

            throw;
        }
        finally
        {
            TryDeleteStateDirectory(operationRoot, stateRoot);
            CleanupRollbackStorage(stateRoot, history);
            if (updateSucceeded)
            {
                CleanupStaleWorkDirectories(stateRoot);
            }
        }

        progress?.Report(new UpdateProgress(UpdateStage.Completed, "Обновление установлено"));
        return new UpdateApplyResult(installedPackages, installedFiles, deletedFiles);
    }

    public static async Task<UpdateRollbackCandidate?> GetLatestRollbackAsync(
        string stateRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        var history = await ReadHistoryAsync(stateRoot, cancellationToken);
        var entry = history.Entries.LastOrDefault(item => item.RolledBackAt is null);
        if (entry is null)
        {
            return null;
        }

        var batchId = entry.BatchId ?? entry.OperationId;
        var batch = history.Entries
            .Where(item => item.RolledBackAt is null
                           && string.Equals(item.BatchId ?? item.OperationId, batchId, StringComparison.Ordinal))
            .ToArray();
        return new UpdateRollbackCandidate(
            entry.PackageId,
            batch.Length > 1 ? $"Anthology {entry.ToVersion}" : entry.DisplayName,
            batch.Select(item => item.FromVersion).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                ? entry.FromVersion
                : "смешанная версия",
            entry.ToVersion,
            string.Join(" + ", batch.Select(item => item.InstallRoot).Distinct(StringComparer.OrdinalIgnoreCase)),
            batchId,
            batch.Max(item => item.InstalledAt),
            batch.Length);
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
        var latestIndex = history.Entries.FindLastIndex(item => item.RolledBackAt is null);
        if (latestIndex < 0)
        {
            throw new InvalidOperationException("Нет обновлений, доступных для отката.");
        }

        var latest = history.Entries[latestIndex];
        var batchId = latest.BatchId ?? latest.OperationId;
        var indexes = history.Entries
            .Select((entry, index) => (entry, index))
            .Where(item => item.entry.RolledBackAt is null
                           && string.Equals(item.entry.BatchId ?? item.entry.OperationId, batchId, StringComparison.Ordinal))
            .ToArray();
        foreach (var item in indexes)
        {
            if (!installRoots.TryGetValue(item.entry.InstallRoot, out var root)
                || string.IsNullOrWhiteSpace(root))
            {
                throw new InvalidOperationException($"Корень установки '{item.entry.InstallRoot}' не настроен.");
            }
        }

        progress?.Report(new UpdateProgress(
            UpdateStage.RollingBack,
            $"Откат Anthology {latest.ToVersion}",
            latest.PackageId));
        var installedState = await ReadInstalledStateAsync(stateRoot, cancellationToken);
        var restoredFiles = 0;
        foreach (var item in indexes.Reverse())
        {
            var entry = item.entry;
            var targetRoot = Path.GetFullPath(installRoots[entry.InstallRoot]);
            await EnsureTransactionAvailableAsync(stateRoot, entry.OperationId, cancellationToken);
            var rollback = await TransactionalFileInstaller.RollbackAsync(
                targetRoot,
                stateRoot,
                entry.OperationId,
                cancellationToken);
            restoredFiles += rollback.RestoredFiles;
            var previousManagedFiles = await ReadManagedSnapshotAsync(stateRoot, entry.OperationId, cancellationToken);
            await WriteManagedFilesAsync(stateRoot, entry.PackageId, previousManagedFiles, cancellationToken);
            var previousIntegrity = await ReadManagedIntegritySnapshotAsync(
                stateRoot,
                entry.OperationId,
                cancellationToken);
            await WriteManagedIntegrityAsync(
                stateRoot,
                entry.PackageId,
                previousIntegrity,
                cancellationToken);
            if (entry.FromVersion is null)
            {
                installedState.Packages.Remove(entry.PackageId);
            }
            else
            {
                installedState.Packages[entry.PackageId] = entry.FromVersion;
            }

            history.Entries[item.index] = entry with { RolledBackAt = DateTimeOffset.UtcNow };
        }

        await WriteInstalledStateAsync(stateRoot, installedState, cancellationToken);
        PruneHistoryToLatestBatch(history, null);
        await WriteHistoryAsync(stateRoot, history, cancellationToken);
        CleanupRollbackStorage(stateRoot, history);
        progress?.Report(new UpdateProgress(UpdateStage.Completed, "Предыдущая версия восстановлена"));
        return new UpdateRollbackResult(latest.PackageId, latest.FromVersion, restoredFiles);
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

            var downloadUri = uri.Host.EndsWith("disk.yandex.ru", StringComparison.OrdinalIgnoreCase)
                ? await YandexDiskMirrorResolver.ResolvePublicDownloadAsync(_httpClient, source, cancellationToken)
                : uri;
            using var request = CreateManifestRequest(downloadUri);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumManifestBytes)
            {
                throw new InvalidDataException("Manifest exceeds the 128 MiB safety limit.");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var bytes = await ReadBoundedManifestAsync(responseStream, cancellationToken);

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
                throw new InvalidDataException("Manifest exceeds the 128 MiB safety limit.");
            }

            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        }

        await using (stream)
        {
            return await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(stream, ManifestJson.Options, cancellationToken)
                ?? throw new InvalidDataException("Manifest is empty or invalid JSON.");
        }
    }

    private static async Task<byte[]> ReadBoundedManifestAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        var block = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(block, cancellationToken);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > MaximumManifestBytes)
            {
                throw new InvalidDataException("Manifest exceeds the 128 MiB safety limit.");
            }
            await buffer.WriteAsync(block.AsMemory(0, read), cancellationToken);
        }
        return buffer.ToArray();
    }

    private static HttpRequestMessage CreateManifestRequest(Uri source)
    {
        var requestUri = source;
        var bypassSharedCache = string.Equals(
            source.Host,
            "raw.githubusercontent.com",
            StringComparison.OrdinalIgnoreCase);
        if (bypassSharedCache)
        {
            var builder = new UriBuilder(source);
            var cacheBuster = $"anthology_cb={Guid.NewGuid():N}";
            var existingQuery = builder.Query.TrimStart('?');
            builder.Query = string.IsNullOrEmpty(existingQuery)
                ? cacheBuster
                : $"{existingQuery}&{cacheBuster}";
            requestUri = builder.Uri;
        }

        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        if (bypassSharedCache)
        {
            request.Headers.CacheControl = new()
            {
                NoCache = true,
                NoStore = true,
                MaxAge = TimeSpan.Zero,
            };
            request.Headers.Pragma.ParseAdd("no-cache");
        }

        return request;
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

    private static IEnumerable<string> EnumeratePrunableFiles(
        string targetRoot,
        IReadOnlyList<string> preservedPaths)
    {
        var root = Path.GetFullPath(targetRoot);
        var preserved = preservedPaths
            .Select(PathSafety.NormalizeRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
            })
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Select(PathSafety.NormalizeRelativePath)
            .Where(path => !preserved.Any(item =>
                string.Equals(path, item, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(item + "/", StringComparison.OrdinalIgnoreCase)));
    }

    private static string[] EnumerateDirectoryFiles(string targetRoot, string relativeDirectory)
    {
        var root = Path.GetFullPath(targetRoot);
        var normalizedDirectory = PathSafety.NormalizeRelativePath(relativeDirectory);
        var directory = PathSafety.ResolveUnderRoot(root, normalizedDirectory);
        if (!Directory.Exists(directory))
        {
            // Keep one safe no-op path so a directory-deletion-only package succeeds
            // even when this player has already removed the addon.
            return [normalizedDirectory];
        }

        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Directory deletion cannot follow a reparse point: {normalizedDirectory}");
        }

        var files = Directory.EnumerateFiles(directory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
            })
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Select(PathSafety.NormalizeRelativePath)
            .ToArray();
        return files.Length == 0 ? [normalizedDirectory] : files;
    }

    private static void DeleteEmptyDirectoryTree(string targetRoot, string relativeDirectory)
    {
        var root = Path.GetFullPath(targetRoot);
        var normalizedDirectory = PathSafety.NormalizeRelativePath(relativeDirectory);
        var directory = PathSafety.ResolveUnderRoot(root, normalizedDirectory);
        if (!Directory.Exists(directory))
        {
            return;
        }

        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Directory deletion cannot follow a reparse point: {normalizedDirectory}");
        }

        var descendants = Directory.EnumerateDirectories(directory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
            })
            .OrderByDescending(path => path.Length)
            .ToArray();
        foreach (var child in descendants)
        {
            if (!Directory.EnumerateFileSystemEntries(child).Any())
            {
                Directory.Delete(child);
            }
        }

        if (!Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }
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

    private static async Task<string[]> ReadManagedFilesAsync(
        string stateRoot,
        string packageId,
        CancellationToken cancellationToken)
    {
        var path = GetManagedFilesPath(stateRoot, packageId);
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, FileOptions.Asynchronous);
        var files = await JsonSerializer.DeserializeAsync<string[]>(stream, ManifestJson.Options, cancellationToken) ?? [];
        return NormalizeManagedPaths(packageId, files);
    }

    private static Task WriteManagedFilesAsync(
        string stateRoot,
        string packageId,
        IEnumerable<string> files,
        CancellationToken cancellationToken) =>
        WriteStringArrayAtomicallyAsync(
            GetManagedFilesPath(stateRoot, packageId),
            NormalizeManagedPaths(packageId, files),
            cancellationToken);

    private static Task WriteManagedSnapshotAsync(
        string stateRoot,
        string operationId,
        IEnumerable<string> files,
        CancellationToken cancellationToken) =>
        WriteStringArrayAtomicallyAsync(
            Path.Combine(Path.GetFullPath(stateRoot), "managed-snapshots", operationId + ".json"),
            files,
            cancellationToken);

    private static async Task<string[]> ReadManagedSnapshotAsync(
        string stateRoot,
        string operationId,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetFullPath(stateRoot), "managed-snapshots", operationId + ".json");
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<string[]>(stream, ManifestJson.Options, cancellationToken) ?? [];
    }

    private static async Task WriteStringArrayAtomicallyAsync(
        string path,
        IEnumerable<string> files,
        CancellationToken cancellationToken)
    {
        var normalized = files
            .Select(PathSafety.NormalizeRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, normalized, ManifestJson.Options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporary, path, true);
    }

    private static string GetManagedFilesPath(string stateRoot, string packageId) =>
        Path.Combine(Path.GetFullPath(stateRoot), "managed-files", packageId + ".json");

    private static string[] MergeManagedFiles(
        PackageManifest package,
        IReadOnlyList<string> previousFiles)
    {
        var files = package.UpdateMode == PackageUpdateMode.ManagedExact || package.PruneInstallRoot
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : NormalizeManagedPaths(package.Id, previousFiles)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        RemoveDeletedPaths(files, package.DeletedFiles, package.DeletedDirectories);
        files.UnionWith(package.GetFilePaths().Select(PathSafety.NormalizeRelativePath));
        return files.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static ManagedIntegrityState CreateManagedIntegrity(
        PackageManifest package,
        IReadOnlyList<PackageFileIntegrity> archiveIntegrity,
        ManagedIntegrityState? previous)
    {
        var previousFiles = SanitizeManagedIntegrityState(package.Id, previous)?.Files ?? [];
        var files = package.UpdateMode == PackageUpdateMode.ManagedExact || package.PruneInstallRoot
            ? new Dictionary<string, PackageFileIntegrity>(StringComparer.OrdinalIgnoreCase)
            : previousFiles.ToDictionary(
                    file => file.Path,
                    file => file,
                    StringComparer.OrdinalIgnoreCase);
        RemoveDeletedPaths(files, package.DeletedFiles, package.DeletedDirectories);
        var archiveFiles = archiveIntegrity.ToDictionary(
            file => PathSafety.NormalizeRelativePath(file.Path),
            StringComparer.OrdinalIgnoreCase);
        foreach (var path in package.GetFilePaths().Select(PathSafety.NormalizeRelativePath))
        {
            if (!archiveFiles.TryGetValue(path, out var integrity))
            {
                throw new InvalidDataException($"Verified integrity metadata is missing for '{path}'.");
            }
            files[path] = integrity with { Path = path, Sha256 = integrity.Sha256.ToLowerInvariant() };
        }

        return new ManagedIntegrityState(
            package.Version,
            package.Sha256.ToLowerInvariant(),
            files.Values.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static async Task<ManagedIntegrityState> ComputeManagedIntegrityAsync(
        PackageManifest package,
        string stagingRoot,
        ManagedIntegrityState? previous,
        CancellationToken cancellationToken)
    {
        var declaredFiles = package.GetFilePaths();
        var integrity = new List<PackageFileIntegrity>(declaredFiles.Count);
        foreach (var relativePath in declaredFiles.Select(PathSafety.NormalizeRelativePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = PathSafety.ResolveUnderRoot(stagingRoot, relativePath);
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new FileNotFoundException("Verified staged file is missing.", path);
            }
            integrity.Add(new PackageFileIntegrity(
                relativePath,
                info.Length,
                await ArtifactHash.ComputeSha256Async(path, cancellationToken)));
        }
        return CreateManagedIntegrity(package, integrity, previous);
    }

    private static async Task<ManagedIntegrityState?> ReadManagedIntegrityAsync(
        string stateRoot,
        string packageId,
        CancellationToken cancellationToken)
    {
        var path = GetManagedIntegrityPath(stateRoot, packageId);
        if (!File.Exists(path))
        {
            return null;
        }
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var state = await JsonSerializer.DeserializeAsync<ManagedIntegrityState>(
            stream,
            ManifestJson.Options,
            cancellationToken);
        if (state is null)
        {
            throw new InvalidDataException($"Managed integrity state for '{packageId}' is invalid.");
        }
        return SanitizeManagedIntegrityState(packageId, state);
    }

    private static async Task WriteManagedIntegrityAsync(
        string stateRoot,
        string packageId,
        ManagedIntegrityState? state,
        CancellationToken cancellationToken)
    {
        var path = GetManagedIntegrityPath(stateRoot, packageId);
        if (state is null)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return;
        }
        await WriteJsonAtomicallyAsync(
            path,
            SanitizeManagedIntegrityState(packageId, state),
            cancellationToken);
    }

    private static string[] NormalizeManagedPaths(
        string packageId,
        IEnumerable<string> paths)
    {
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!TryNormalizeManagedPath(packageId, path, out var candidate))
            {
                continue;
            }
            normalized.Add(candidate);
        }

        return normalized.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static ManagedIntegrityState? SanitizeManagedIntegrityState(
        string packageId,
        ManagedIntegrityState? state)
    {
        if (state is null)
        {
            return null;
        }

        var files = new Dictionary<string, PackageFileIntegrity>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in state.Files ?? [])
        {
            if (TryNormalizeManagedPath(packageId, file.Path, out var path))
            {
                files.TryAdd(path, file with { Path = path });
            }
        }

        return state with
        {
            Files = files.Values
                .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };
    }

    private static bool TryNormalizeManagedPath(
        string packageId,
        string path,
        out string normalized)
    {
        var restricted = PackageInstallScopePolicy.IsMo2ModsOnlyPackage(packageId);
        try
        {
            normalized = PathSafety.NormalizeRelativePath(path);
        }
        catch (ArgumentException) when (restricted)
        {
            normalized = string.Empty;
            return false;
        }

        return !restricted || PackageInstallScopePolicy.IsAllowedMo2ModsPath(normalized);
    }

    private static Task WriteManagedIntegritySnapshotAsync(
        string stateRoot,
        string operationId,
        ManagedIntegrityState? state,
        CancellationToken cancellationToken)
    {
        if (state is null)
        {
            return Task.CompletedTask;
        }
        return WriteJsonAtomicallyAsync(
            GetManagedIntegritySnapshotPath(stateRoot, operationId),
            state,
            cancellationToken);
    }

    private static async Task<ManagedIntegrityState?> ReadManagedIntegritySnapshotAsync(
        string stateRoot,
        string operationId,
        CancellationToken cancellationToken)
    {
        var path = GetManagedIntegritySnapshotPath(stateRoot, operationId);
        if (!File.Exists(path))
        {
            return null;
        }
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<ManagedIntegrityState>(
            stream,
            ManifestJson.Options,
            cancellationToken);
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                32 * 1024,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, ManifestJson.Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string GetManagedIntegrityPath(string stateRoot, string packageId) =>
        Path.Combine(Path.GetFullPath(stateRoot), "managed-integrity", packageId + ".json");

    private static string GetManagedIntegritySnapshotPath(string stateRoot, string operationId) =>
        Path.Combine(Path.GetFullPath(stateRoot), "managed-integrity-snapshots", operationId + ".json");

    private static void RemoveDeletedPaths<T>(
        Dictionary<string, T> files,
        IReadOnlyList<string>? deletedFiles,
        IReadOnlyList<string>? deletedDirectories)
    {
        foreach (var path in deletedFiles ?? [])
        {
            files.Remove(PathSafety.NormalizeRelativePath(path));
        }
        foreach (var directory in deletedDirectories ?? [])
        {
            var prefix = PathSafety.NormalizeRelativePath(directory).TrimEnd('/') + "/";
            foreach (var path in files.Keys.Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                files.Remove(path);
            }
        }
    }

    private static void RemoveDeletedPaths(
        HashSet<string> files,
        IReadOnlyList<string>? deletedFiles,
        IReadOnlyList<string>? deletedDirectories)
    {
        foreach (var path in deletedFiles ?? [])
        {
            files.Remove(PathSafety.NormalizeRelativePath(path));
        }
        foreach (var directory in deletedDirectories ?? [])
        {
            var prefix = PathSafety.NormalizeRelativePath(directory).TrimEnd('/') + "/";
            files.RemoveWhere(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
    }

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

    private static void PruneHistoryToLatestBatch(UpdateHistory history, string? preferredBatchId)
    {
        var keepBatchId = preferredBatchId;
        if (string.IsNullOrWhiteSpace(keepBatchId))
        {
            keepBatchId = history.Entries
                .LastOrDefault(entry => entry.RolledBackAt is null) is { } latest
                    ? latest.BatchId ?? latest.OperationId
                    : null;
        }

        history.Entries.RemoveAll(entry =>
            entry.RolledBackAt is not null
            || keepBatchId is null
            || !string.Equals(entry.BatchId ?? entry.OperationId, keepBatchId, StringComparison.Ordinal));
    }

    private static void CleanupRollbackStorage(
        string stateRoot,
        UpdateHistory history,
        TimeSpan? minimumAge = null)
    {
        var referenced = history.Entries
            .Where(entry => entry.RolledBackAt is null)
            .Select(entry => entry.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        CleanupUnreferencedDirectories(
            Path.Combine(Path.GetFullPath(stateRoot), "transactions"),
            referenced,
            minimumAge);
        CleanupUnreferencedFiles(
            Path.Combine(Path.GetFullPath(stateRoot), "managed-snapshots"),
            referenced,
            minimumAge: minimumAge);
        CleanupUnreferencedFiles(
            Path.Combine(Path.GetFullPath(stateRoot), "managed-integrity-snapshots"),
            referenced,
            minimumAge: minimumAge);
        CleanupUnreferencedFiles(
            Path.Combine(Path.GetFullPath(stateRoot), "rollback-archives"),
            referenced,
            ".zip",
            minimumAge);
        var temporaryFileAge = minimumAge ?? TimeSpan.FromHours(1);
        foreach (var temporaryRoot in new[]
                 {
                     Path.GetFullPath(stateRoot),
                     Path.Combine(Path.GetFullPath(stateRoot), "managed-files"),
                     Path.Combine(Path.GetFullPath(stateRoot), "managed-integrity"),
                     Path.Combine(Path.GetFullPath(stateRoot), "managed-snapshots"),
                     Path.Combine(Path.GetFullPath(stateRoot), "managed-integrity-snapshots"),
                     Path.Combine(Path.GetFullPath(stateRoot), "rollback-archives"),
                 })
        {
            CleanupStaleTemporaryFiles(temporaryRoot, temporaryFileAge);
        }
    }

    private static async Task ArchiveTransactionAsync(
        string stateRoot,
        string operationId,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(stateRoot);
        var transactionRoot = Path.Combine(root, "transactions", operationId);
        var journalPath = Path.Combine(transactionRoot, "journal.json");
        if (!File.Exists(journalPath))
        {
            throw new FileNotFoundException("Update transaction journal was not found.", journalPath);
        }

        var archiveRoot = Path.Combine(root, "rollback-archives");
        Directory.CreateDirectory(archiveRoot);
        var archivePath = Path.Combine(archiveRoot, operationId + ".zip");
        var temporary = archivePath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var file in Directory.EnumerateFiles(
                                 transactionRoot,
                                 "*",
                                 new EnumerationOptions
                                 {
                                     RecurseSubdirectories = true,
                                     IgnoreInaccessible = false,
                                     AttributesToSkip = FileAttributes.ReparsePoint,
                                 }))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relative = PathSafety.NormalizeRelativePath(
                            Path.GetRelativePath(transactionRoot, file).Replace('\\', '/'));
                        var entry = archive.CreateEntry(relative, CompressionLevel.SmallestSize);
                        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        await using var source = new FileStream(
                            file,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            1024 * 1024,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await using var target = entry.Open();
                        await source.CopyToAsync(target, cancellationToken);
                    }
                }
            }
            if (!IsValidRollbackArchive(temporary, operationId))
            {
                throw new InvalidDataException("Compressed update rollback failed validation.");
            }
            File.Move(temporary, archivePath, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task EnsureTransactionAvailableAsync(
        string stateRoot,
        string operationId,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(stateRoot);
        var transactionRoot = Path.Combine(root, "transactions", operationId);
        if (File.Exists(Path.Combine(transactionRoot, "journal.json")))
        {
            return;
        }

        var archivePath = Path.Combine(root, "rollback-archives", operationId + ".zip");
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("Compressed update rollback was not found.", archivePath);
        }

        var temporaryRoot = transactionRoot + $".restore-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }
                var relative = PathSafety.NormalizeRelativePath(entry.FullName);
                if (!string.Equals(relative, "journal.json", StringComparison.OrdinalIgnoreCase)
                    && !relative.StartsWith("backup/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Compressed rollback contains unexpected file '{relative}'.");
                }
                var destination = PathSafety.ResolveUnderRoot(temporaryRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var source = entry.Open();
                await using var target = new FileStream(
                    destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await source.CopyToAsync(target, cancellationToken);
            }
            if (!File.Exists(Path.Combine(temporaryRoot, "journal.json")))
            {
                throw new InvalidDataException("Compressed rollback has no transaction journal.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(transactionRoot)!);
            Directory.Move(temporaryRoot, transactionRoot);
        }
        catch
        {
            TryDeleteStateDirectory(temporaryRoot, stateRoot);
            throw;
        }
    }

    private static void CompactActiveRollbackTransactions(
        string stateRoot,
        UpdateHistory history,
        HashSet<string>? prevalidatedArchives = null)
    {
        var root = Path.GetFullPath(stateRoot);
        foreach (var operationId in history.Entries
                     .Where(entry => entry.RolledBackAt is null)
                     .Select(entry => entry.OperationId)
                     .Distinct(StringComparer.Ordinal))
        {
            var archivePath = Path.Combine(root, "rollback-archives", operationId + ".zip");
            var transactionRoot = Path.Combine(root, "transactions", operationId);
            if (Directory.Exists(transactionRoot)
                && (prevalidatedArchives?.Contains(operationId) == true
                    || IsValidRollbackArchive(archivePath, operationId)))
            {
                TryDeleteStateDirectory(transactionRoot, stateRoot);
            }
        }
    }

    private static bool IsValidRollbackArchive(string archivePath, string operationId)
    {
        if (!File.Exists(archivePath))
        {
            return false;
        }
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            FileTransactionJournal? journal = null;
            var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }
                var relative = PathSafety.NormalizeRelativePath(entry.FullName);
                if (!entries.Add(relative)
                    || !string.Equals(relative, "journal.json", StringComparison.OrdinalIgnoreCase)
                       && !relative.StartsWith("backup/", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                using var source = entry.Open();
                if (string.Equals(relative, "journal.json", StringComparison.OrdinalIgnoreCase))
                {
                    if (journal is not null || entry.Length > MaximumManifestBytes)
                    {
                        return false;
                    }
                    journal = JsonSerializer.Deserialize<FileTransactionJournal>(source, ManifestJson.Options);
                }
                else
                {
                    source.CopyTo(Stream.Null);
                }
            }

            if (journal is null
                || !string.Equals(journal.OperationId, operationId, StringComparison.Ordinal)
                || !string.Equals(journal.Status, "completed", StringComparison.Ordinal))
            {
                return false;
            }
            return journal.Files.All(item =>
                !item.TargetExisted
                || entries.Contains("backup/" + PathSafety.NormalizeRelativePath(item.RelativePath)));
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or JsonException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            return false;
        }
    }

    private static void CleanupStaleWorkDirectories(string stateRoot)
    {
        var workRoot = Path.Combine(Path.GetFullPath(stateRoot), "work");
        if (!Directory.Exists(workRoot))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
        foreach (var directory in Directory.EnumerateDirectories(workRoot, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) <= cutoff)
                {
                    TryDeleteStateDirectory(directory, stateRoot);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Cleanup is best-effort and must never make update checking fail.
            }
        }
    }

    private static void CleanupUnreferencedDirectories(
        string serviceRoot,
        HashSet<string> referenced,
        TimeSpan? minimumAge = null)
    {
        if (!Directory.Exists(serviceRoot))
        {
            return;
        }
        var stateRoot = Directory.GetParent(serviceRoot)?.FullName;
        if (stateRoot is null)
        {
            return;
        }
        foreach (var directory in Directory.EnumerateDirectories(serviceRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (!referenced.Contains(Path.GetFileName(directory))
                && IsOldEnough(Directory.GetLastWriteTimeUtc(directory), minimumAge))
            {
                TryDeleteStateDirectory(directory, stateRoot);
            }
        }
    }

    private static void CleanupUnreferencedFiles(
        string serviceRoot,
        HashSet<string> referenced,
        string extension = ".json",
        TimeSpan? minimumAge = null)
    {
        if (!Directory.Exists(serviceRoot))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(serviceRoot, "*" + extension, SearchOption.TopDirectoryOnly))
        {
            if (referenced.Contains(Path.GetFileNameWithoutExtension(file)))
            {
                continue;
            }
            try
            {
                if (!IsOldEnough(File.GetLastWriteTimeUtc(file), minimumAge))
                {
                    continue;
                }
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Cleanup is bounded to updater-owned metadata and is best-effort.
            }
        }
    }

    private static void CleanupStaleTemporaryFiles(string serviceRoot, TimeSpan minimumAge)
    {
        if (!Directory.Exists(serviceRoot))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(serviceRoot, "*.tmp-*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (!IsOldEnough(File.GetLastWriteTimeUtc(file), minimumAge))
                {
                    continue;
                }
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A future startup check retries stale updater-owned temporary files.
            }
        }
    }

    private static bool IsOldEnough(DateTime lastWriteUtc, TimeSpan? minimumAge) =>
        minimumAge is null || DateTime.UtcNow - lastWriteUtc >= minimumAge.Value;

    private static void TryDeleteStateDirectory(string directory, string stateRoot)
    {
        try
        {
            var root = Path.GetFullPath(stateRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || string.Equals(target + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(target)
                || ContainsReparsePoint(target))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(target, true);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            // Never fail an otherwise valid update because old cache cleanup was blocked.
        }
    }

    private static bool ContainsReparsePoint(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            return true;
        }
        return Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
    }

    private sealed record InstalledState(Dictionary<string, string> Packages);

    private sealed record UpdateHistory(List<UpdateHistoryEntry> Entries);

    private sealed record ManagedIntegrityState(
        string PackageVersion,
        string ArchiveSha256,
        IReadOnlyList<PackageFileIntegrity> Files);

    private sealed record IntegrityAssessment(bool RequiresRepair, IReadOnlyList<string> RepairFiles)
    {
        public static IntegrityAssessment Intact { get; } = new(false, []);
    }

    private sealed record IntegrityCatalogLoadResult(
        SignedPackageIntegrityCatalog? Catalog,
        bool RequiresCatalogRepair);

    private sealed record AppliedPackage(
        PackageUpdate Update,
        InstallResult Install,
        IReadOnlyList<string> PreviousManagedFiles,
        ManagedIntegrityState? PreviousIntegrity,
        ManagedIntegrityState? NextIntegrity);

    private sealed record UpdateHistoryEntry(
        string PackageId,
        string DisplayName,
        string? FromVersion,
        string ToVersion,
        string InstallRoot,
        string OperationId,
        DateTimeOffset InstalledAt,
        DateTimeOffset? RolledBackAt,
        string? BatchId = null,
        int DeletedFiles = 0);
}
