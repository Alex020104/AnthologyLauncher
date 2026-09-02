using Anthology.Update.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using Anthology.Contracts;
using System.IO;

namespace Anthology.Launcher;

public sealed class LauncherUpdateService(
    HttpClient httpClient,
    LauncherSettingsStore settingsStore,
    LauncherOperationGate operationGate)
{
    public const string LauncherPackageId = "anthology-launcher";
    public const string IntegrityPackageId = "anthology-integrity";

    private readonly UpdateCoordinator _coordinator = new(httpClient);

    public LegacyMo2LayoutMigrationResult? LastLegacyLayoutMigration { get; private set; }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var transferLease = operationGate.EnterTransfer();
        var settings = settingsStore.Current;
        ProductionTrustAnchor.ValidatePublicKey(settings.PublicKeyPath);
        var result = await _coordinator.CheckAsync(
            settings.ManifestSource,
            settings.PublicKeyPath,
            settings.UpdateChannel,
            settingsStore.UpdaterStateRoot,
            CreateInstallRoots(settings),
            cancellationToken);
        ProductionTrustAnchor.ValidateManifest(result.SignedManifest);
        await CacheVerifiedManifestAsync(result.SignedManifest, cancellationToken);
        await RunLegacyLayoutMigrationAsync(settings, cancellationToken);
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
            ProductionTrustAnchor.ValidatePublicKey(settings.PublicKeyPath);
            var check = await _coordinator.CheckAsync(
                cachePath,
                settings.PublicKeyPath,
                settings.UpdateChannel,
                settingsStore.UpdaterStateRoot,
                cancellationToken);
            ProductionTrustAnchor.ValidateManifest(check.SignedManifest);
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

    public async Task<UpdateApplyResult> ApplyAsync(
        UpdateCheckResult check,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var transferLease = operationGate.EnterTransfer();
        var settings = settingsStore.Current;
        var result = await _coordinator.ApplyAsync(
            check,
            CreateInstallRoots(settings),
            settingsStore.UpdaterStateRoot,
            progress,
            settings.PreferredMirrorProvider,
            cancellationToken);
        await RunLegacyLayoutMigrationAsync(settings, cancellationToken);
        return result;
    }

    public Task<UpdateApplyResult> ApplyLauncherOnlyAsync(
        UpdateCheckResult check,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        var launcherPackages = check.Packages
            .Where(update => update.UpdateAvailable
                             && update.Package.Kind == PackageKind.Launcher
                             && string.Equals(update.Package.Id, LauncherPackageId, StringComparison.OrdinalIgnoreCase))
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

    public Task<UpdateApplyResult> ApplyStartupPrerequisitesAsync(
        UpdateCheckResult check,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        var prerequisites = check.Packages
            .Where(update => update.UpdateAvailable
                             && (string.Equals(update.Package.Id, LauncherPackageId, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(update.Package.Id, IntegrityPackageId, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return ApplySelectedAsync(check, prerequisites, progress, cancellationToken);
    }

    public Task<UpdateApplyResult> ApplyRepairsOnlyAsync(
        UpdateCheckResult check,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        var repairs = check.Packages
            .Where(update => update.UpdateAvailable && update.RepairRequired)
            .ToArray();
        return ApplySelectedAsync(check, repairs, progress, cancellationToken);
    }

    private Task<UpdateApplyResult> ApplySelectedAsync(
        UpdateCheckResult check,
        PackageUpdate[] packages,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (packages.Length == 0)
        {
            return Task.FromResult(new UpdateApplyResult(0, 0, 0));
        }

        return ApplyAsync(
            new UpdateCheckResult(check.SignedManifest, packages, check.TrustedKeyId),
            progress,
            cancellationToken);
    }

    public LauncherActionResult RestartToApplyPendingUpdate()
    {
        var launcherRoot = FindPendingLauncherRoot();
        if (launcherRoot is null)
        {
            return new LauncherActionResult(
                false,
                "Обновление лаунчера загружено, но его файл запуска не найден. Запустите лаунчер через AnomalyLauncher.exe.");
        }

        var application = System.Windows.Application.Current;
        if (application is null)
        {
            return new LauncherActionResult(false, "Не удалось получить процесс лаунчера для автоматического перезапуска.");
        }

        try
        {
            if (!IsManagedByBootstrap(launcherRoot))
            {
                StartBootstrapAfterCurrentProcess(launcherRoot);
            }

            // Shutdown must happen only after the fallback bootstrap was started.
            // When the launcher was already started by the bootstrap, that parent
            // process is waiting for this process and will apply the pending payload.
            operationGate.AuthorizeRestartShutdown();
            _ = application.Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(application.Shutdown));
            return new LauncherActionResult(true, "Лаунчер автоматически перезапускается для применения обновления.");
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidOperationException
                                           or UnauthorizedAccessException
                                           or Win32Exception)
        {
            operationGate.RevokeRestartShutdownAuthorization();
            return new LauncherActionResult(false, $"Не удалось автоматически перезапустить лаунчер: {exception.Message}");
        }
    }

    public Task<UpdateRollbackCandidate?> GetLatestRollbackAsync(CancellationToken cancellationToken = default) =>
        UpdateCoordinator.GetLatestRollbackAsync(settingsStore.UpdaterStateRoot, cancellationToken);

    public Task<UpdateRollbackResult> RollbackLatestAsync(
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.Current;
        return UpdateCoordinator.RollbackLatestAsync(
            CreateInstallRoots(settings),
            settingsStore.UpdaterStateRoot,
            progress,
            cancellationToken);
    }

    private static Dictionary<string, string> CreateInstallRoots(LauncherSettings settings)
    {
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddRoots(roots, settings.GameRoot, "game", "engine", "database");
        AddRoots(roots, settings.ModpackRoot, "modpack", "tools");
        if (!string.IsNullOrWhiteSpace(settings.ModpackRoot))
        {
            // "modpack" addresses the portable MO2 instance itself, while "mods"
            // is deliberately constrained to its mods directory. Keeping these
            // aliases distinct prevents a mods-scoped package from ever writing
            // next to ModOrganizer.exe.
            roots["mods"] = Path.Combine(Path.GetFullPath(settings.ModpackRoot), "mods");
        }
        return roots;
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

    private async Task RunLegacyLayoutMigrationAsync(
        LauncherSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ModpackRoot)
            || !Directory.Exists(settings.ModpackRoot))
        {
            LastLegacyLayoutMigration = null;
            return;
        }

        LastLegacyLayoutMigration = await LegacyMo2LayoutMigrator.MigrateAsync(
            settings.ModpackRoot,
            settingsStore.UpdaterStateRoot,
            cancellationToken);
    }

    private string? FindPendingLauncherRoot()
    {
        string currentExecutablePath;
        try
        {
            currentExecutablePath = GetCurrentProcessPath();
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return null;
        }
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("ANTHOLOGY_LAUNCHER_ROOT"),
            !string.IsNullOrWhiteSpace(settingsStore.Current.GameRoot)
                ? Path.Combine(settingsStore.Current.GameRoot, "AnthologyLauncher")
                : null,
            Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))?.FullName,
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string root;
            try
            {
                root = Path.GetFullPath(candidate);
            }
            catch (Exception exception) when (exception is ArgumentException
                                               or IOException
                                               or NotSupportedException)
            {
                continue;
            }

            if (PathsEqual(
                    currentExecutablePath,
                    Path.Combine(root, "App", "AnthologyLauncher.Next.exe"))
                && File.Exists(Path.Combine(root, "Start-AnthologyLauncherNext.ps1"))
                && File.Exists(Path.Combine(root, "Update", "LauncherPending", "launcher-update.json")))
            {
                return root;
            }
        }

        return null;
    }

    private static bool IsManagedByBootstrap(string launcherRoot)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ANTHOLOGY_LAUNCHER_BOOTSTRAPPED"),
                "1",
                StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(
                Environment.GetEnvironmentVariable("ANTHOLOGY_LAUNCHER_BOOTSTRAP_PID"),
                out var processId)
            || !long.TryParse(
                Environment.GetEnvironmentVariable("ANTHOLOGY_LAUNCHER_BOOTSTRAP_STARTED_AT_UTC_TICKS"),
                out var expectedStartTimeUtcTicks)
            || expectedStartTimeUtcTicks <= 0)
        {
            return false;
        }

        var expectedProcessPath = Environment.GetEnvironmentVariable("ANTHOLOGY_LAUNCHER_BOOTSTRAP_PROCESS_PATH");
        var lockPath = Environment.GetEnvironmentVariable("ANTHOLOGY_LAUNCHER_BOOTSTRAP_LOCK_PATH");
        var expectedLockPath = Path.Combine(launcherRoot, "Update", "launcher-bootstrap.lock");
        if (string.IsNullOrWhiteSpace(expectedProcessPath)
            || string.IsNullOrWhiteSpace(lockPath)
            || !PathsEqual(lockPath, expectedLockPath))
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var actualProcessPath = process.MainModule?.FileName;
            var processFileName = Path.GetFileName(actualProcessPath);
            if (process.HasExited
                || process.StartTime.ToUniversalTime().Ticks != expectedStartTimeUtcTicks
                || string.IsNullOrWhiteSpace(actualProcessPath)
                || !PathsEqual(actualProcessPath, expectedProcessPath)
                || (!string.Equals(processFileName, "powershell.exe", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(processFileName, "pwsh.exe", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (!File.Exists(lockPath))
            {
                return false;
            }

            try
            {
                using var unlocked = new FileStream(
                    lockPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidOperationException
                                           or IOException
                                           or NotSupportedException
                                           or System.Security.SecurityException
                                           or UnauthorizedAccessException
                                           or Win32Exception)
        {
            return false;
        }
    }

    private static void StartBootstrapAfterCurrentProcess(string launcherRoot)
    {
        var scriptPath = Path.Combine(launcherRoot, "Start-AnthologyLauncherNext.ps1");
        using var currentProcess = Process.GetCurrentProcess();
        var currentProcessPath = GetCurrentProcessPath(currentProcess);
        var currentProcessStartTimeUtcTicks = currentProcess.StartTime.ToUniversalTime().Ticks;
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = launcherRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-WindowStyle");
        startInfo.ArgumentList.Add("Hidden");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-RestartAfterProcessId");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-RestartAfterProcessStartTimeUtcTicks");
        startInfo.ArgumentList.Add(currentProcessStartTimeUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-RestartAfterProcessPath");
        startInfo.ArgumentList.Add(currentProcessPath);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить служебный процесс обновления лаунчера.");
        if (process.WaitForExit(250))
        {
            throw new InvalidOperationException(
                $"Служебный процесс обновления завершился раньше времени (код {process.ExitCode}).");
        }
    }

    private static string GetCurrentProcessPath()
    {
        using var process = Process.GetCurrentProcess();
        return GetCurrentProcessPath(process);
    }

    private static string GetCurrentProcessPath(Process process)
    {
        var processPath = Environment.ProcessPath ?? process.MainModule?.FileName;
        return string.IsNullOrWhiteSpace(processPath)
            ? throw new InvalidOperationException("Не удалось определить путь процесса лаунчера.")
            : Path.GetFullPath(processPath);
    }

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

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
