using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthology.Contracts;
using Anthology.Update.Core;

namespace Anthology.Releaser.Core;

public sealed record RcloneCommand(
    string FileName,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments);

public sealed record RcloneCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public interface IRcloneCommandRunner
{
    Task<RcloneCommandResult> RunAsync(
        RcloneCommand command,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes rclone without a command shell. Every argument is passed through
/// ProcessStartInfo.ArgumentList, so local and remote paths remain one argument
/// even when they contain spaces or non-ASCII characters.
/// </summary>
public sealed class RcloneProcessRunner : IRcloneCommandRunner
{
    public async Task<RcloneCommandResult> RunAsync(
        RcloneCommand command,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Не удалось запустить rclone.");
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var outputTask = PumpAsync(process.StandardOutput, standardOutput, progress, cancellationToken);
        var errorTask = PumpAsync(process.StandardError, standardError, progress, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited between HasExited and Kill.
            }

            try
            {
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(outputTask, errorTask);
            }
            catch (OperationCanceledException)
            {
                // The original cancellation is rethrown below.
            }
            throw;
        }

        return new RcloneCommandResult(
            process.ExitCode,
            standardOutput.ToString(),
            standardError.ToString());
    }

    private static async Task PumpAsync(
        StreamReader reader,
        StringBuilder destination,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            destination.AppendLine(line);
            if (!string.IsNullOrWhiteSpace(line))
            {
                progress?.Report(line.Trim());
            }
        }
    }
}

public sealed record GoogleDriveProjectInfo(
    string RemoteTarget,
    string PublicUrl);

public sealed record GoogleDriveSyncResult(
    IReadOnlyList<string> RemoteTargets);

public sealed record GoogleDriveRemoteFile(
    string Path,
    string Id,
    long Size,
    string ShareUrl);

/// <summary>
/// Publishes Anthology directly from the existing game/MO2/output folders to a
/// configured Google Drive rclone remote. No local Google Drive mirror or
/// temporary copy is created.
/// </summary>
public sealed partial class GoogleDrivePublisher(IRcloneCommandRunner? commandRunner = null)
{
    public const string GamePackageId = "anthology-game";
    public const string Mo2PackageId = "anthology-mo2";
    public const string Provider = "google-drive";
    public const string AccountHomeUrl = "https://drive.google.com/drive/home";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly string[] CommonExcludedRoots =
    [
        ".git",
        ".anthology-releaser",
        "$RECYCLE.BIN",
        "System Volume Information",
    ];

    // Keep this list aligned with UnifiedReleaseBuilder's managed game package.
    private static readonly string[] GameExcludedRoots =
    [
        "appdata",
        "logs",
        "screenshots",
        "crashdumps",
        "webcache",
        "AnthologyLauncher",
        "AnomalyLauncher.cfg",
        "commandline.txt",
    ];

    // Keep this list aligned with UnifiedReleaseBuilder's managed MO2 package.
    private static readonly string[] Mo2ExcludedRoots =
    [
        "downloads",
        "overwrite",
        "logs",
        "crash_dumps",
        "webcache",
        "ModOrganizer.ini",
    ];

    private readonly IRcloneCommandRunner _commandRunner = commandRunner ?? new RcloneProcessRunner();

    /// <summary>
    /// Performs local-only validation. It checks paths and file existence but
    /// never opens rclone.conf or contacts Google.
    /// </summary>
    public static bool IsConfigured(ReleaserMachineSettings? machine)
    {
        if (machine is null)
        {
            return false;
        }
        try
        {
            var configuration = ResolveConfiguration(machine);
            EnsureDisjointManagedRoots(configuration);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or IOException
                                           or InvalidOperationException
                                           or NotSupportedException
                                           or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads only the names reported by <c>rclone listremotes</c>. The rclone
    /// configuration file itself is never opened or echoed by the releaser.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListRemoteNamesAsync(
        ReleaserMachineSettings machine,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var rclonePath = RequireExecutable(machine.GoogleDriveRclonePath);
        var configPath = string.IsNullOrWhiteSpace(machine.GoogleDriveRcloneConfigPath)
            ? null
            : RequireSourceFile(machine.GoogleDriveRcloneConfigPath, "конфигурацию rclone");
        var arguments = new List<string> { "listremotes" };
        if (configPath is not null)
        {
            arguments.Add("--config");
            arguments.Add(configPath);
        }

        progress?.Report("Google Drive: проверка настроенных rclone remote…");
        var result = await _commandRunner.RunAsync(
            new RcloneCommand(
                rclonePath,
                Path.GetDirectoryName(rclonePath)!,
                arguments),
            progress is null ? null : new RedactingProgress(progress, configPath),
            cancellationToken);
        if (result.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput.Trim()
                : result.StandardError.Trim();
            details = RedactDiagnostic(details, configPath);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(details)
                    ? "rclone не смог проверить список remote."
                    : $"rclone не смог проверить список remote. {details}");
        }

        var names = new List<string>();
        foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = line.EndsWith(':') ? line[..^1].Trim() : line.Trim();
            if (name.Length > 0)
            {
                names.Add(NormalizeRemoteName(name));
            }
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Idempotently creates the remote project folder and makes that folder
    /// link-readable. The returned folder URL is informational; downloads use
    /// exact file IDs discovered after upload.
    /// </summary>
    public async Task<GoogleDriveProjectInfo> EnsureProjectAsync(
        ReleaserMachineSettings machine,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var configuration = ResolveConfiguration(machine);
        progress?.Report("Google Drive: создание или проверка папки проекта…");
        await RunRequiredAsync(
            configuration,
            ["mkdir", configuration.ProjectTarget],
            "rclone не смог создать папку проекта Google Drive.",
            progress,
            cancellationToken);

        progress?.Report("Google Drive: включение доступа по ссылке для проекта…");
        var link = await RunRequiredAsync(
            configuration,
            ["link", configuration.ProjectTarget],
            "rclone не смог создать публичную ссылку на проект Google Drive.",
            progress,
            cancellationToken);
        var publicUrl = ParseGoogleDrivePublicUrl(link.StandardOutput);
        machine.GoogleDriveProjectPublicUrl = publicUrl;
        return new GoogleDriveProjectInfo(configuration.ProjectTarget, publicUrl);
    }

    /// <summary>
    /// Synchronizes the existing source roots in place. rclone reads those roots
    /// directly and removes remote files missing from the corresponding source.
    /// </summary>
    public async Task<GoogleDriveSyncResult> SyncSourcesAsync(
        ReleaserMachineSettings machine,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var configuration = ResolveConfiguration(machine, requireSources: true);
        var gameRoot = RequireSourceDirectory(machine.GameSourceRoot, "корень игры");
        var mo2Root = RequireSourceDirectory(machine.Mo2SourceRoot, "корень MO2");
        EnsureDisjointManagedRoots(configuration);

        progress?.Report("Google Drive: синхронизация корня игры без локальной копии…");
        await SyncDirectoryCoreAsync(
            configuration,
            gameRoot,
            configuration.GameTarget,
            CommonExcludedRoots.Concat(GameExcludedRoots),
            progress,
            cancellationToken);

        progress?.Report("Google Drive: синхронизация корня MO2 без локальной копии…");
        await SyncDirectoryCoreAsync(
            configuration,
            mo2Root,
            configuration.Mo2Target,
            CommonExcludedRoots.Concat(Mo2ExcludedRoots),
            progress,
            cancellationToken);

        return new GoogleDriveSyncResult([configuration.GameTarget, configuration.Mo2Target]);
    }

    /// <summary>
    /// Synchronizes the release channel/output tree. To keep remote deletion
    /// contained, the requested path must be the configured release path; use
    /// UploadFileAsync for files elsewhere under the project.
    /// </summary>
    public async Task<GoogleDriveSyncResult> SyncDirectoryAsync(
        ReleaserMachineSettings machine,
        string localRoot,
        string remoteRelativePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var configuration = ResolveConfiguration(machine);
        var sourceRoot = RequireSourceDirectory(localRoot, "локальную папку публикации");
        var remotePath = NormalizeRequiredRemotePath(remoteRelativePath, nameof(remoteRelativePath));
        EnsureDisjointManagedRoots(configuration);
        if (!remotePath.Equals(configuration.ReleaseRelativePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Синхронизация с удалением разрешена только для настроенной папки релизов Google Drive.");
        }
        var target = configuration.ReleaseTarget;
        await SyncDirectoryCoreAsync(
            configuration,
            sourceRoot,
            target,
            [],
            progress,
            cancellationToken);
        return new GoogleDriveSyncResult([target]);
    }

    public Task<GoogleDriveSyncResult> SyncReleaseDirectoryAsync(
        ReleaserMachineSettings machine,
        string localRoot,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return SyncDirectoryAsync(
            machine,
            localRoot,
            machine.GoogleDriveReleasePath,
            progress,
            cancellationToken);
    }

    /// <summary>
    /// Streams one existing file to an exact project-relative path. rclone uses
    /// Google Drive's resumable upload path and does not stage another local copy.
    /// </summary>
    public async Task<GoogleDriveRemoteFile> UploadFileAsync(
        ReleaserMachineSettings machine,
        string localFile,
        string remoteRelativePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var configuration = ResolveConfiguration(machine);
        var source = RequireSourceFile(localFile, "файл публикации");
        var relativePath = NormalizeRequiredRemotePath(remoteRelativePath, nameof(remoteRelativePath));
        var target = CombineRemoteTarget(configuration, relativePath);
        progress?.Report($"Google Drive: загрузка {Path.GetFileName(source)}…");
        await RunRequiredAsync(
            configuration,
            [
                "copyto",
                source,
                target,
                "--drive-chunk-size", "64M",
                "--retries", "5",
                "--low-level-retries", "20",
                "--stats", "2s",
                "--stats-one-line",
            ],
            $"rclone не смог загрузить {Path.GetFileName(source)} в Google Drive.",
            progress,
            cancellationToken);

        return await DiscoverFileAsync(machine, relativePath, progress, cancellationToken)
               ?? throw new InvalidDataException(
                   $"Google Drive не вернул ID загруженного файла {relativePath}.");
    }

    public async Task DeleteFileAsync(
        ReleaserMachineSettings machine,
        string remoteRelativePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var configuration = ResolveConfiguration(machine);
        var relativePath = NormalizeRequiredRemotePath(remoteRelativePath, nameof(remoteRelativePath));
        var target = CombineRemoteTarget(configuration, relativePath);
        progress?.Report($"Google Drive: удаление {relativePath}…");
        await RunRequiredAsync(
            configuration,
            ["deletefile", target],
            $"rclone не смог удалить файл Google Drive: {relativePath}.",
            progress,
            cancellationToken);
    }

    /// <summary>
    /// Removes one exact release-version directory. Purging the release root,
    /// project root, parent traversal, or a nested path is intentionally impossible.
    /// </summary>
    public async Task DeleteReleaseVersionAsync(
        ReleaserMachineSettings machine,
        string version,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var configuration = ResolveConfiguration(machine);
        EnsureDisjointManagedRoots(configuration);
        var versionSegment = NormalizeReleaseVersion(version);
        var relativePath = CombineRemotePaths(configuration.ReleaseRelativePath, versionSegment);
        var target = CombineRemoteTarget(configuration, relativePath);
        progress?.Report($"Google Drive: удаление версии {versionSegment} из канала релизов…");
        await RunRequiredAsync(
            configuration,
            ["purge", target],
            $"rclone не смог удалить версию {versionSegment} из Google Drive.",
            progress,
            cancellationToken);
    }

    /// <summary>
    /// Lists files recursively and returns the provider's stable file IDs. Paths
    /// in the result are relative to the requested project child directory.
    /// </summary>
    public async Task<IReadOnlyList<GoogleDriveRemoteFile>> ListFilesAsync(
        ReleaserMachineSettings machine,
        string? remoteRelativePath = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var configuration = ResolveConfiguration(machine);
        var target = string.IsNullOrWhiteSpace(remoteRelativePath)
            ? configuration.ProjectTarget
            : CombineRemoteTarget(
                configuration,
                NormalizeRequiredRemotePath(remoteRelativePath, nameof(remoteRelativePath)));
        var result = await RunRequiredAsync(
            configuration,
            [
                "lsjson",
                target,
                "--recursive",
                "--files-only",
                "--no-mimetype",
                "--no-modtime",
            ],
            $"rclone не смог прочитать список файлов Google Drive: {target}.",
            progress,
            cancellationToken,
            forwardToolOutput: false);
        return ParseRemoteFiles(result.StandardOutput);
    }

    public async Task<GoogleDriveRemoteFile?> DiscoverFileAsync(
        ReleaserMachineSettings machine,
        string remoteRelativePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRequiredRemotePath(remoteRelativePath, nameof(remoteRelativePath));
        var separator = normalized.LastIndexOf('/');
        var parent = separator < 0 ? null : normalized[..separator];
        var name = separator < 0 ? normalized : normalized[(separator + 1)..];
        var files = await ListFilesAsync(machine, parent, progress, cancellationToken);
        var match = files.FirstOrDefault(file =>
            file.Path.Equals(name, StringComparison.OrdinalIgnoreCase));
        return match is null ? null : match with { Path = normalized };
    }

    public Task<GoogleDriveRemoteFile?> DiscoverManifestAsync(
        ReleaserMachineSettings machine,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var manifestPath = ResolveStableManifestRelativePath(machine);
        return DiscoverFileAsync(machine, manifestPath, progress, cancellationToken);
    }

    public async Task<SignedUpdateManifest?> ReadManifestAsync(
        ReleaserMachineSettings machine,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (await DiscoverManifestAsync(machine, progress, cancellationToken) is null)
        {
            return null;
        }

        var configuration = ResolveConfiguration(machine);
        var manifestPath = ResolveStableManifestRelativePath(machine);
        var target = CombineRemoteTarget(configuration, manifestPath);
        var result = await RunRequiredAsync(
            configuration,
            ["cat", target],
            $"rclone could not read the Google Drive manifest: {manifestPath}.",
            progress,
            cancellationToken,
            forwardToolOutput: false);
        try
        {
            return JsonSerializer.Deserialize<SignedUpdateManifest>(
                       result.StandardOutput,
                       ManifestJson.Options)
                   ?? throw new InvalidDataException("The Google Drive manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Google Drive manifest contains invalid JSON.", exception);
        }
    }

    public static string ResolveStableManifestRelativePath(ReleaserMachineSettings machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var configuration = ResolveConfiguration(machine);
        return string.IsNullOrWhiteSpace(machine.GoogleDriveManifestPath)
            ? CombineRemotePaths(configuration.ReleaseRelativePath, "manifest.json")
            : NormalizeRequiredRemotePath(
                machine.GoogleDriveManifestPath,
                nameof(machine.GoogleDriveManifestPath));
    }

    /// <summary>
    /// Verifies that every release-managed local source file exists remotely
    /// with the same size, then creates an exact per-file mirror override from
    /// its Google Drive ID. Extra remote files are ignored and will be removed by
    /// the next source sync.
    /// </summary>
    public async Task<IReadOnlyList<LooseFileMirrorOverride>> BuildLooseFileMirrorOverridesAsync(
        ReleaserMachineSettings machine,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var configuration = ResolveConfiguration(machine, requireSources: true);
        var gameRoot = RequireSourceDirectory(machine.GameSourceRoot, "корень игры");
        var mo2Root = RequireSourceDirectory(machine.Mo2SourceRoot, "корень MO2");
        EnsureDisjointManagedRoots(configuration);

        progress?.Report("Google Drive: чтение ID файлов игры…");
        var gameRemoteFiles = await ListFilesAsync(
            machine,
            configuration.GameRelativePath,
            progress,
            cancellationToken);
        progress?.Report("Google Drive: чтение ID файлов MO2…");
        var mo2RemoteFiles = await ListFilesAsync(
            machine,
            configuration.Mo2RelativePath,
            progress,
            cancellationToken);

        var game = BuildOverrides(
            GamePackageId,
            gameRoot,
            gameRemoteFiles,
            CommonExcludedRoots.Concat(GameExcludedRoots),
            configuration.MirrorPriority,
            progress,
            cancellationToken);
        var mo2 = BuildOverrides(
            Mo2PackageId,
            mo2Root,
            mo2RemoteFiles,
            CommonExcludedRoots.Concat(Mo2ExcludedRoots),
            configuration.MirrorPriority,
            progress,
            cancellationToken);
        return [.. game, .. mo2];
    }

    public static string CreateFileShareUrl(string fileId)
    {
        var normalized = fileId?.Trim() ?? string.Empty;
        if (!GoogleDriveIdPattern().IsMatch(normalized))
        {
            throw new ArgumentException("Некорректный ID файла Google Drive.", nameof(fileId));
        }

        return $"https://drive.google.com/file/d/{Uri.EscapeDataString(normalized)}/view?usp=sharing";
    }

    public static bool IsAccountHomeUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.TrimEnd('/').Equals("/drive/home", StringComparison.OrdinalIgnoreCase);

    private async Task SyncDirectoryCoreAsync(
        ResolvedConfiguration configuration,
        string sourceRoot,
        string target,
        IEnumerable<string> excludedRoots,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "sync",
            sourceRoot,
            target,
            "--create-empty-src-dirs",
            "--delete-after",
            "--track-renames",
            "--fast-list",
            "--drive-chunk-size", "64M",
            "--transfers", "4",
            "--checkers", "8",
            "--retries", "5",
            "--low-level-retries", "20",
            "--stats", "2s",
            "--stats-one-line",
        };
        foreach (var excluded in excludedRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            arguments.Add("--exclude");
            arguments.Add($"/{excluded}");
            arguments.Add("--exclude");
            arguments.Add($"/{excluded}/**");
        }

        await RunRequiredAsync(
            configuration,
            arguments,
            $"rclone не смог синхронизировать {sourceRoot} с {target}.",
            progress,
            cancellationToken);
    }

    private async Task<RcloneCommandResult> RunRequiredAsync(
        ResolvedConfiguration configuration,
        IReadOnlyList<string> arguments,
        string failureMessage,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        bool forwardToolOutput = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var commandArguments = new List<string>(arguments);
        if (configuration.ConfigPath is not null)
        {
            commandArguments.Add("--config");
            commandArguments.Add(configuration.ConfigPath);
        }

        var result = await _commandRunner.RunAsync(
            new RcloneCommand(
                configuration.RclonePath,
                Path.GetDirectoryName(configuration.RclonePath)!,
                commandArguments),
            forwardToolOutput && progress is not null
                ? new RedactingProgress(progress, configuration.ConfigPath)
                : null,
            cancellationToken);
        if (result.ExitCode == 0)
        {
            return result;
        }

        var details = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        details = RedactDiagnostic(details, configuration.ConfigPath);
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(details) ? failureMessage : $"{failureMessage} {details}");
    }

    private static List<LooseFileMirrorOverride> BuildOverrides(
        string packageId,
        string sourceRoot,
        IReadOnlyList<GoogleDriveRemoteFile> remoteFiles,
        IEnumerable<string> excludedRoots,
        int priority,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var excluded = excludedRoots.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remoteIndex = new Dictionary<string, GoogleDriveRemoteFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var remoteFile in remoteFiles)
        {
            if (!remoteIndex.TryAdd(remoteFile.Path, remoteFile))
            {
                throw new InvalidDataException(
                    $"Google Drive содержит пути, различающиеся только регистром: {remoteFile.Path}");
            }
        }

        var localFiles = Directory.EnumerateFiles(
                sourceRoot,
                "*",
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = false,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                })
            .Select(path => new LocalFile(
                PathSafety.NormalizeRelativePath(Path.GetRelativePath(sourceRoot, path).Replace('\\', '/')),
                path))
            .Where(item => !excluded.Contains(item.Path.Split('/', 2)[0]))
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ToArray();
        if (localFiles.Length == 0)
        {
            throw new InvalidDataException($"Пакет {packageId} не содержит файлов для публикации.");
        }
        if (localFiles.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != localFiles.Length)
        {
            throw new InvalidDataException(
                $"Пакет {packageId} содержит пути, различающиеся только регистром.");
        }

        var missing = new List<string>();
        var mismatched = new List<string>();
        var overrides = new List<LooseFileMirrorOverride>(localFiles.Length);
        for (var index = 0; index < localFiles.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var local = localFiles[index];
            if (!remoteIndex.TryGetValue(local.Path, out var remote))
            {
                missing.Add(local.Path);
                continue;
            }

            var localSize = new FileInfo(local.FullPath).Length;
            if (remote.Size != localSize)
            {
                mismatched.Add($"{local.Path} ({localSize} / {remote.Size})");
                continue;
            }

            overrides.Add(new LooseFileMirrorOverride(
                packageId,
                local.Path,
                [new MirrorManifest(Provider, remote.ShareUrl, priority)]));
            if ((index + 1) % 500 == 0 || index + 1 == localFiles.Length)
            {
                progress?.Report(
                    $"Google Drive: {packageId} — сопоставлено {index + 1:N0} из {localFiles.Length:N0} файлов…");
            }
        }

        if (missing.Count > 0 || mismatched.Count > 0)
        {
            var details = new List<string>();
            if (missing.Count > 0)
            {
                details.Add($"нет на диске: {FormatExamples(missing)}");
            }
            if (mismatched.Count > 0)
            {
                details.Add($"другой размер: {FormatExamples(mismatched)}");
            }
            throw new InvalidDataException(
                $"Google Drive не совпадает с локальным пакетом {packageId}; {string.Join("; ", details)}. Сначала выполните синхронизацию.");
        }

        return overrides;
    }

    private static GoogleDriveRemoteFile[] ParseRemoteFiles(string json)
    {
        RcloneListItem[] items;
        try
        {
            items = JsonSerializer.Deserialize<RcloneListItem[]>(json, JsonOptions) ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("rclone вернул некорректный JSON списка Google Drive.", exception);
        }

        var files = new List<GoogleDriveRemoteFile>(items.Length);
        foreach (var item in items.Where(item => !item.IsDir))
        {
            var rawPath = string.IsNullOrWhiteSpace(item.Path) ? item.Name : item.Path;
            var path = PathSafety.NormalizeRelativePath(rawPath);
            if (item.Size < 0)
            {
                throw new InvalidDataException($"Google Drive не вернул размер файла {path}.");
            }
            var id = item.Id?.Trim() ?? string.Empty;
            files.Add(new GoogleDriveRemoteFile(path, id, item.Size, CreateFileShareUrl(id)));
        }

        return files.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
    }

    private static ResolvedConfiguration ResolveConfiguration(
        ReleaserMachineSettings machine,
        bool requireSources = false)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var rclonePath = RequireExecutable(machine.GoogleDriveRclonePath);
        var configPath = string.IsNullOrWhiteSpace(machine.GoogleDriveRcloneConfigPath)
            ? null
            : RequireSourceFile(machine.GoogleDriveRcloneConfigPath, "конфигурацию rclone");
        var remoteName = NormalizeRemoteName(machine.GoogleDriveRemoteName);
        var projectPath = NormalizeRequiredRemotePath(machine.GoogleDriveProjectPath, nameof(machine.GoogleDriveProjectPath));

        var gamePath = ResolveSourceRemotePath(
            machine.GoogleDriveGamePath,
            machine.GameSourceRoot,
            "game",
            requireSources);
        var mo2Path = ResolveSourceRemotePath(
            machine.GoogleDriveMo2Path,
            machine.Mo2SourceRoot,
            "modpack",
            requireSources);
        var releasePath = NormalizeRequiredRemotePath(
            machine.GoogleDriveReleasePath,
            nameof(machine.GoogleDriveReleasePath));
        var priority = machine.GoogleDriveMirrorPriority;
        if (priority is < 0 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(machine),
                machine.GoogleDriveMirrorPriority,
                "Приоритет зеркала Google Drive должен быть от 0 до 10000.");
        }

        return new ResolvedConfiguration(
            rclonePath,
            configPath,
            remoteName,
            projectPath,
            gamePath,
            mo2Path,
            releasePath,
            priority);
    }

    private static string ResolveSourceRemotePath(
        string configuredPath,
        string sourceRoot,
        string fallback,
        bool requireSource)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return NormalizeRequiredRemotePath(configuredPath, nameof(configuredPath));
        }
        if (!string.IsNullOrWhiteSpace(sourceRoot))
        {
            var fullPath = Path.GetFullPath(sourceRoot.Trim());
            var name = new DirectoryInfo(Path.TrimEndingDirectorySeparator(fullPath)).Name;
            if (!string.IsNullOrWhiteSpace(name))
            {
                return NormalizeRequiredRemotePath(name, nameof(sourceRoot));
            }
        }
        if (requireSource)
        {
            throw new ArgumentException("Укажите исходную папку и путь Google Drive для пакета.");
        }
        return fallback;
    }

    private static string NormalizeRemoteName(string? value)
    {
        var remoteName = value?.Trim() ?? string.Empty;
        if (remoteName.Length is 0 or > 128
            || remoteName[0] == '-'
            || remoteName.Any(character =>
                !(char.IsLetterOrDigit(character) || character is ' ' or '_' or '-' or '.')))
        {
            throw new ArgumentException(
                "Некорректное имя rclone remote. Используйте буквы, цифры, пробел, точку, '_' или '-'.",
                nameof(value));
        }
        return remoteName;
    }

    private static string NormalizeReleaseVersion(string? value)
    {
        var version = value?.Trim() ?? string.Empty;
        if (!ReleaseVersionPattern().IsMatch(version))
        {
            throw new ArgumentException(
                "Версия для удаления должна быть одним безопасным именем папки без '/' и '..'.",
                nameof(value));
        }
        return version;
    }

    private static string NormalizeRequiredRemotePath(string? value, string parameterName)
    {
        var path = value?.Trim().Replace('\\', '/') ?? string.Empty;
        if (path.Length == 0
            || path.StartsWith('-')
            || path.Contains('"')
            || path.Contains('\''))
        {
            throw new ArgumentException("Укажите безопасный относительный путь Google Drive.", parameterName);
        }
        return PathSafety.NormalizeRelativePath(path);
    }

    private static string CombineRemotePaths(string? parent, string child)
    {
        var normalizedChild = NormalizeRequiredRemotePath(child, nameof(child));
        return string.IsNullOrWhiteSpace(parent)
            ? normalizedChild
            : NormalizeRequiredRemotePath(parent, nameof(parent)) + "/" + normalizedChild;
    }

    private static string CombineRemoteTarget(
        ResolvedConfiguration configuration,
        string relativePath) =>
        $"{configuration.RemoteName}:{configuration.ProjectPath}/{relativePath}";

    private static string RequireExecutable(string? path)
    {
        var executable = RequireFullPath(path, "исполняемый файл rclone");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("Не найден исполняемый файл rclone.", executable);
        }
        return executable;
    }

    private static string RequireSourceDirectory(string? path, string label)
    {
        var directory = RequireFullPath(path, label);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Не найден {label}: {directory}");
        }
        var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(directory) ?? string.Empty);
        if (Path.TrimEndingDirectorySeparator(directory).Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{label} не может быть корнем диска.");
        }
        return Path.TrimEndingDirectorySeparator(directory);
    }

    private static string RequireSourceFile(string? path, string label)
    {
        var file = RequireFullPath(path, label);
        return File.Exists(file)
            ? file
            : throw new FileNotFoundException($"Не найден {label}: {file}", file);
    }

    private static string RequireFullPath(string? path, string label)
    {
        var value = path?.Trim() ?? string.Empty;
        if (value.Length == 0 || !Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException($"Укажите полный путь: {label}.", nameof(path));
        }
        return Path.GetFullPath(value);
    }

    private static void EnsureDisjointManagedRoots(ResolvedConfiguration configuration)
    {
        var paths = new[]
        {
            configuration.GameRelativePath,
            configuration.Mo2RelativePath,
            configuration.ReleaseRelativePath,
        };
        for (var left = 0; left < paths.Length; left++)
        {
            for (var right = left + 1; right < paths.Length; right++)
            {
                if (IsSameOrDescendant(paths[left], paths[right])
                    || IsSameOrDescendant(paths[right], paths[left]))
                {
                    throw new InvalidOperationException(
                        "Папки игры, MO2 и релизов на Google Drive должны быть отдельными и не могут быть вложены друг в друга.");
                }
            }
        }
    }

    private static bool IsSameOrDescendant(string candidate, string parent) =>
        candidate.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);

    private static string ParseGoogleDrivePublicUrl(string output)
    {
        foreach (var token in output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = token.Trim().Trim('"', '\'', ',', ';');
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !(uri.Host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase)
                     || uri.Host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase))
                || IsAccountHomeUrl(uri.AbsoluteUri))
            {
                continue;
            }
            return uri.AbsoluteUri;
        }
        throw new InvalidDataException(
            "rclone не вернул публичную ссылку проекта Google Drive.");
    }

    private static string RedactDiagnostic(string value, string? configPath)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var redacted = value;
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            redacted = redacted.Replace(
                configPath,
                "<rclone-config>",
                StringComparison.OrdinalIgnoreCase);
        }
        redacted = BearerCredentialPattern().Replace(redacted, "Bearer <redacted>");
        return SensitiveAssignmentPattern().Replace(
            redacted,
            match => match.Groups[1].Value + match.Groups[2].Value + "<redacted>");
    }

    private static string FormatExamples(List<string> values)
    {
        const int limit = 5;
        var examples = string.Join(", ", values.Take(limit));
        return values.Count > limit ? $"{examples} и ещё {values.Count - limit:N0}" : examples;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{6,256}$", RegexOptions.CultureInvariant)]
    private static partial Regex GoogleDriveIdPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseVersionPattern();

    [GeneratedRegex("(?i)\\bBearer\\s+[A-Za-z0-9._~+/=-]+", RegexOptions.CultureInvariant)]
    private static partial Regex BearerCredentialPattern();

    [GeneratedRegex(
        "(?i)\\b(access[_-]?token|refresh[_-]?token|client[_-]?secret|token)([^A-Za-z0-9\\r\\n]{1,8})([^&\\s,;\\\"'}]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveAssignmentPattern();

    private sealed record LocalFile(string Path, string FullPath);

    private sealed record RcloneListItem(
        string Path,
        string Name,
        long Size,
        bool IsDir,
        string? Id);

    private sealed class RedactingProgress(
        IProgress<string> destination,
        string? configPath) : IProgress<string>
    {
        public void Report(string value) =>
            destination.Report(RedactDiagnostic(value, configPath));
    }

    private sealed record ResolvedConfiguration(
        string RclonePath,
        string? ConfigPath,
        string RemoteName,
        string ProjectPath,
        string GameRelativePath,
        string Mo2RelativePath,
        string ReleaseRelativePath,
        int MirrorPriority)
    {
        public string ProjectTarget => $"{RemoteName}:{ProjectPath}";

        public string GameTarget => $"{RemoteName}:{ProjectPath}/{GameRelativePath}";

        public string Mo2Target => $"{RemoteName}:{ProjectPath}/{Mo2RelativePath}";

        public string ReleaseTarget => $"{RemoteName}:{ProjectPath}/{ReleaseRelativePath}";
    }
}
