using System.Text;
using System.Runtime.ExceptionServices;
using SharpCompress.Readers;

namespace Anthology.Mo2.Core;

public sealed record Mo2ArchiveInstallResult(bool Success, string Message, string? ModName = null);

public sealed record Mo2ArchiveExtractionLimits(
    long MaxExpandedBytes,
    long MaxSingleEntryBytes,
    double MaxCompressionRatio,
    long MinimumRatioAllowanceBytes,
    long MinimumFreeSpaceReserveBytes)
{
    public static Mo2ArchiveExtractionLimits Default { get; } = new(
        MaxExpandedBytes: 256L * 1024 * 1024 * 1024,
        MaxSingleEntryBytes: 64L * 1024 * 1024 * 1024,
        MaxCompressionRatio: 250,
        MinimumRatioAllowanceBytes: 512L * 1024 * 1024,
        MinimumFreeSpaceReserveBytes: 512L * 1024 * 1024);
}

public static class Mo2ArchiveInstaller
{
    private const int MaxArchiveEntries = 1_000_000;
    private static readonly string[] SupportedExtensions = [".zip", ".7z", ".rar", ".001"];

    public static FomodArchiveInspection InspectFomod(
        string archivePath,
        CancellationToken cancellationToken = default) =>
        FomodArchiveReader.Inspect(archivePath, cancellationToken);

    public static Mo2ArchiveInstallResult Install(
        string root,
        string profileName,
        string archivePath,
        string? installName = null,
        bool replaceExisting = false,
        Mo2ArchiveExtractionLimits? extractionLimits = null,
        CancellationToken cancellationToken = default)
    {
        var target = PrepareTarget(root, archivePath, installName, replaceExisting, out var error);
        if (target is null)
        {
            return error!;
        }

        // Deny writers and deletion for the complete validation/extraction
        // transaction so the file scanned here is the one later installed.
        using var archiveLease = OpenArchiveStream(archivePath);
        var fileKeys = ReadFileKeys(archivePath, cancellationToken);
        if (fileKeys.Count == 0)
        {
            return new Mo2ArchiveInstallResult(false, "В архиве нет файлов.");
        }
        if (fileKeys.Any(FomodArchiveReader.IsModuleConfigPath))
        {
            return new Mo2ArchiveInstallResult(
                false,
                "Архив использует мастер FOMOD. Сначала выберите его компоненты.");
        }

        var prefix = FindWrapperPrefix(fileKeys);
        return CommitInstall(
            root,
            profileName,
            target,
            archivePath,
            extractionLimits,
            (staging, budget) => ExtractRegularArchive(
                archivePath,
                staging,
                prefix,
                budget,
                cancellationToken),
            cancellationToken);
    }

    public static Mo2ArchiveInstallResult InstallFomod(
        string root,
        string profileName,
        FomodPackage package,
        FomodInstallPlan plan,
        string? installName = null,
        bool replaceExisting = false,
        Mo2ArchiveExtractionLimits? extractionLimits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(plan);
        package.ThrowIfDisposed();
        if (plan.InspectionId != package.InspectionId)
        {
            return new Mo2ArchiveInstallResult(false, "План FOMOD относится к другому архиву. Повторите проверку компонентов.");
        }
        if (!package.IsBoundPlanUnchanged(plan))
        {
            return new Mo2ArchiveInstallResult(false, "План FOMOD был изменён после проверки. Повторите выбор компонентов.");
        }
        if (!plan.Success)
        {
            return new Mo2ArchiveInstallResult(false, string.Join(Environment.NewLine, plan.Errors));
        }

        var archivePath = package.ArchivePath;
        var target = PrepareTarget(root, archivePath, installName, replaceExisting, out var error);
        if (target is null)
        {
            return error!;
        }

        return CommitInstall(
            root,
            profileName,
            target,
            archivePath,
            extractionLimits,
            (staging, budget) => ExtractFomodPlan(
                archivePath,
                staging,
                plan,
                budget,
                cancellationToken),
            cancellationToken);
    }

    private static Mo2ArchiveInstallResult CommitInstall(
        string root,
        string profileName,
        InstallTarget target,
        string archivePath,
        Mo2ArchiveExtractionLimits? extractionLimits,
        Action<string, ExtractionBudget> extract,
        CancellationToken cancellationToken)
    {
        var staging = Path.Combine(target.ModsRoot, $".__anthology_install_{Guid.NewGuid():N}");
        var profileRoot = Mo2ProfileManager.ResolveProfileRoot(root, profileName);
        var modListPath = Path.Combine(profileRoot, "modlist.txt");
        var profileBackupPath = modListPath + ".anthology-backup";
        var extractionBudget = ExtractionBudget.Create(
            archivePath,
            target.ModsRoot,
            extractionLimits ?? Mo2ArchiveExtractionLimits.Default);
        string? backup = null;
        byte[]? modListSnapshot = null;
        byte[]? profileBackupSnapshot = null;
        var profileBackupExisted = false;
        var profileMutationStarted = false;
        var committed = false;
        try
        {
            Directory.CreateDirectory(staging);
            extract(staging, extractionBudget);
            File.WriteAllText(
                Path.Combine(staging, "meta.ini"),
                $"[General]{Environment.NewLine}gameName=STALKER Anomaly{Environment.NewLine}modid=0{Environment.NewLine}version=local{Environment.NewLine}",
                new UTF8Encoding(false));

            // This is the final cancellable boundary. Directory/profile changes
            // below form one rollback unit and must either finish or be reverted.
            cancellationToken.ThrowIfCancellationRequested();
            modListSnapshot = File.ReadAllBytes(modListPath);
            profileBackupExisted = File.Exists(profileBackupPath);
            if (profileBackupExisted)
            {
                profileBackupSnapshot = File.ReadAllBytes(profileBackupPath);
            }

            if (target.Replacing)
            {
                backup = Path.Combine(target.ModsRoot, $".__anthology_backup_{Guid.NewGuid():N}");
                Directory.Move(target.Destination, backup);
            }
            Directory.Move(staging, target.Destination);
            committed = true;
            profileMutationStarted = true;
            Mo2ProfileManager.AddOrEnableMod(root, profileName, target.ModName);
            string? cleanupWarning = null;
            if (backup is not null && Directory.Exists(backup))
            {
                try
                {
                    Directory.Delete(backup, true);
                }
                catch (Exception cleanupError) when (cleanupError is IOException
                                                       or UnauthorizedAccessException)
                {
                    // The new folder and profile are already committed. A failed
                    // best-effort cleanup must not trigger rollback after an old
                    // backup directory may have been partially deleted.
                    cleanupWarning = $" Старую резервную папку не удалось удалить: {cleanupError.Message}";
                }
            }
            var successMessage = target.Replacing
                ? $"Мод обновлён и включён: {target.ModName}"
                : $"Мод установлен и включён: {target.ModName}";
            return new Mo2ArchiveInstallResult(
                true,
                cleanupWarning is null ? successMessage : successMessage + "." + cleanupWarning,
                target.ModName);
        }
        catch (Exception exception)
        {
            var rollbackErrors = new List<Exception>();
            TryRollback(
                () =>
                {
                    if (Directory.Exists(staging))
                    {
                        Directory.Delete(staging, true);
                    }
                },
                rollbackErrors);
            TryRollback(
                () =>
                {
                    if (committed && Directory.Exists(target.Destination))
                    {
                        Directory.Delete(target.Destination, true);
                    }
                },
                rollbackErrors);
            TryRollback(
                () =>
                {
                    if (backup is not null
                        && Directory.Exists(backup)
                        && !Directory.Exists(target.Destination))
                    {
                        Directory.Move(backup, target.Destination);
                    }
                },
                rollbackErrors);
            TryRollback(
                () =>
                {
                    if (!profileMutationStarted || modListSnapshot is null)
                    {
                        return;
                    }
                    RestoreFileAtomic(modListPath, modListSnapshot);
                    if (profileBackupExisted)
                    {
                        RestoreFileAtomic(profileBackupPath, profileBackupSnapshot!);
                    }
                    else if (File.Exists(profileBackupPath))
                    {
                        File.Delete(profileBackupPath);
                    }
                },
                rollbackErrors);

            if (rollbackErrors.Count > 0)
            {
                throw new AggregateException(
                    "Установка не завершена, и автоматический откат выполнен не полностью.",
                    new[] { exception }.Concat(rollbackErrors));
            }
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }
    }

    private static InstallTarget? PrepareTarget(
        string root,
        string archivePath,
        string? installName,
        bool replaceExisting,
        out Mo2ArchiveInstallResult? error)
    {
        error = null;
        if (!File.Exists(archivePath))
        {
            error = new Mo2ArchiveInstallResult(false, $"Архив не найден: {archivePath}");
            return null;
        }
        if (!SupportedExtensions.Contains(Path.GetExtension(archivePath), StringComparer.OrdinalIgnoreCase))
        {
            error = new Mo2ArchiveInstallResult(false, "Поддерживаются архивы ZIP, 7z и RAR.");
            return null;
        }

        var fullRoot = Path.GetFullPath(root);
        var modsRoot = Path.GetFullPath(Path.Combine(fullRoot, "mods"));
        Directory.CreateDirectory(modsRoot);
        var modName = SanitizeModName(
            string.IsNullOrWhiteSpace(installName)
                ? Path.GetFileNameWithoutExtension(archivePath)
                : installName);
        var destination = Path.GetFullPath(Path.Combine(modsRoot, modName));
        if (!destination.StartsWith(modsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            error = new Mo2ArchiveInstallResult(false, "Некорректное имя мода.");
            return null;
        }

        var replacing = Directory.Exists(destination);
        if (replacing && !replaceExisting)
        {
            error = new Mo2ArchiveInstallResult(false, $"Мод уже существует: {modName}");
            return null;
        }
        return new InstallTarget(modsRoot, destination, modName, replacing);
    }

    private static void ExtractRegularArchive(
        string archivePath,
        string staging,
        string prefix,
        ExtractionBudget budget,
        CancellationToken cancellationToken)
    {
        var entryCount = 0;
        using var archiveStream = OpenArchiveStream(archivePath);
        using var reader = ReaderFactory.OpenReader(archiveStream);
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            entryCount++;
            EnsureArchiveEntryLimit(entryCount);
            if (reader.Entry.IsDirectory)
            {
                continue;
            }

            var relative = NormalizeRegularArchivePath(reader.Entry.Key ?? string.Empty);
            if (prefix.Length > 0 && relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                relative = relative[prefix.Length..];
            }
            if (relative.Length == 0)
            {
                continue;
            }

            var outputPath = GetStagingPath(staging, relative, reader.Entry.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var source = reader.OpenEntryStream();
            using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            CopyTo(source, output, reader.Entry.Size, reader.Entry.Key, budget, cancellationToken);
        }
    }

    private static void ExtractFomodPlan(
        string archivePath,
        string staging,
        FomodInstallPlan plan,
        ExtractionBudget budget,
        CancellationToken cancellationToken)
    {
        var targets = plan.Files
            .GroupBy(file => file.ArchivePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0)
        {
            return;
        }

        var entryCount = 0;
        using var archiveStream = OpenArchiveStream(archivePath);
        using var reader = ReaderFactory.OpenReader(archiveStream);
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            entryCount++;
            EnsureArchiveEntryLimit(entryCount);
            if (reader.Entry.IsDirectory)
            {
                continue;
            }

            var archiveEntry = Normalize(reader.Entry.Key ?? string.Empty);
            if (!targets.TryGetValue(archiveEntry, out var plannedFiles))
            {
                continue;
            }

            var firstPath = GetStagingPath(staging, plannedFiles[0].DestinationPath, reader.Entry.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
            using (var source = reader.OpenEntryStream())
            using (var output = new FileStream(firstPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                CopyTo(source, output, reader.Entry.Size, reader.Entry.Key, budget, cancellationToken);
            }

            foreach (var duplicate in plannedFiles.Skip(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var duplicatePath = GetStagingPath(staging, duplicate.DestinationPath, reader.Entry.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(duplicatePath)!);
                budget.AccountCopy(new FileInfo(firstPath).Length, duplicate.DestinationPath);
                File.Copy(firstPath, duplicatePath, overwrite: false);
            }
            extracted.Add(archiveEntry);
            if (extracted.Count == targets.Count)
            {
                break;
            }
        }

        var missing = targets.Keys.Where(key => !extracted.Contains(key)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException($"Архив изменился во время установки; отсутствует: {missing[0]}");
        }
    }

    private static string GetStagingPath(string staging, string relative, string? archiveEntry)
    {
        var fullStaging = Path.GetFullPath(staging);
        var target = Path.GetFullPath(Path.Combine(
            fullStaging,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(fullStaging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Архив содержит опасный путь: {archiveEntry ?? relative}");
        }
        return target;
    }

    private static void CopyTo(
        Stream source,
        Stream output,
        long declaredSize,
        string? entryName,
        ExtractionBudget budget,
        CancellationToken cancellationToken)
    {
        budget.ValidateDeclaredSize(declaredSize, entryName);
        long entryBytes = 0;
        var buffer = new byte[81920];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                return;
            }
            budget.Account(read, ref entryBytes, entryName);
            output.Write(buffer, 0, read);
        }
    }

    private static List<string> ReadFileKeys(string archivePath, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        var entryCount = 0;
        using var archiveStream = OpenArchiveStream(archivePath);
        using var reader = ReaderFactory.OpenReader(archiveStream);
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            entryCount++;
            EnsureArchiveEntryLimit(entryCount);
            if (!reader.Entry.IsDirectory)
            {
                var key = NormalizeRegularArchivePath(reader.Entry.Key ?? string.Empty);
                if (key.Length > 0)
                {
                    keys.Add(key);
                }
            }
        }
        return keys;
    }

    private static string FindWrapperPrefix(IEnumerable<string> paths)
    {
        var values = paths.Where(path => path.Length > 0).ToArray();
        var firstSegments = values
            .Select(path => path.Split('/', 2)[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (firstSegments.Length != 1 || firstSegments[0].Equals("gamedata", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var prefix = firstSegments[0] + "/";
        return values.Any(path => path.StartsWith(prefix + "gamedata/", StringComparison.OrdinalIgnoreCase))
            ? prefix
            : string.Empty;
    }

    private static string SanitizeModName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? $"Anthology Mod {DateTime.Now:yyyyMMdd-HHmmss}" : cleaned;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string NormalizeRegularArchivePath(string path)
    {
        var separatorsNormalized = path.Replace('\\', '/');
        if (separatorsNormalized.EndsWith('/')
            || separatorsNormalized.Contains("//", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Архив содержит неоднозначный путь Windows: {path}");
        }
        return FomodPath.NormalizeRelativePath(separatorsNormalized, allowEmpty: false);
    }

    private static void TryRollback(Action rollback, List<Exception> errors)
    {
        try
        {
            rollback();
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            errors.Add(exception);
        }
    }

    private static void RestoreFileAtomic(string path, byte[] bytes)
    {
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllBytes(temporary, bytes);
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

    private static void EnsureArchiveEntryLimit(int entryCount)
    {
        if (entryCount > MaxArchiveEntries)
        {
            throw new InvalidDataException("Архив содержит слишком много элементов.");
        }
    }

    private static FileStream OpenArchiveStream(string archivePath) => new(
        archivePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        1024 * 1024,
        FileOptions.SequentialScan);

    private sealed record InstallTarget(
        string ModsRoot,
        string Destination,
        string ModName,
        bool Replacing);

    private sealed class ExtractionBudget
    {
        private readonly Mo2ArchiveExtractionLimits _limits;
        private readonly long _allowedBytes;
        private long _expandedBytes;

        private ExtractionBudget(Mo2ArchiveExtractionLimits limits, long allowedBytes)
        {
            _limits = limits;
            _allowedBytes = allowedBytes;
        }

        public static ExtractionBudget Create(
            string archivePath,
            string destinationRoot,
            Mo2ArchiveExtractionLimits limits)
        {
            ValidateLimits(limits);
            var archiveBytes = Math.Max(1, new FileInfo(archivePath).Length);
            var ratioValue = archiveBytes * limits.MaxCompressionRatio;
            var ratioLimit = ratioValue >= long.MaxValue
                ? long.MaxValue
                : (long)Math.Ceiling(ratioValue);
            ratioLimit = Math.Max(ratioLimit, limits.MinimumRatioAllowanceBytes);

            var pathRoot = Path.GetPathRoot(Path.GetFullPath(destinationRoot));
            if (string.IsNullOrWhiteSpace(pathRoot))
            {
                throw new InvalidDataException("Не удалось определить диск для безопасной распаковки архива.");
            }
            var availableBytes = new DriveInfo(pathRoot).AvailableFreeSpace;
            if (availableBytes <= limits.MinimumFreeSpaceReserveBytes)
            {
                throw new IOException(
                    $"Недостаточно свободного места: после установки должно остаться не менее {limits.MinimumFreeSpaceReserveBytes} байт.");
            }

            var freeSpaceLimit = availableBytes - limits.MinimumFreeSpaceReserveBytes;
            var allowedBytes = Math.Min(limits.MaxExpandedBytes, Math.Min(ratioLimit, freeSpaceLimit));
            if (allowedBytes <= 0)
            {
                throw new InvalidDataException("Безопасный лимит распаковки архива равен нулю.");
            }
            return new ExtractionBudget(limits, allowedBytes);
        }

        public void ValidateDeclaredSize(long declaredSize, string? entryName)
        {
            if (declaredSize < 0)
            {
                return;
            }
            if (declaredSize > _limits.MaxSingleEntryBytes)
            {
                throw new InvalidDataException(
                    $"Файл архива превышает безопасный предел {_limits.MaxSingleEntryBytes} байт: {entryName}");
            }
            if (declaredSize > _allowedBytes - _expandedBytes)
            {
                throw new InvalidDataException(
                    $"Распакованные данные превысят безопасный предел {_allowedBytes} байт: {entryName}");
            }
        }

        public void Account(int bytes, ref long entryBytes, string? entryName)
        {
            entryBytes += bytes;
            if (entryBytes > _limits.MaxSingleEntryBytes)
            {
                throw new InvalidDataException(
                    $"Файл архива превышает безопасный предел {_limits.MaxSingleEntryBytes} байт: {entryName}");
            }
            AccountCopy(bytes, entryName);
        }

        public void AccountCopy(long bytes, string? entryName)
        {
            if (bytes < 0 || bytes > _allowedBytes - _expandedBytes)
            {
                throw new InvalidDataException(
                    $"Распакованные данные превышают безопасный предел {_allowedBytes} байт: {entryName}");
            }
            _expandedBytes += bytes;
        }

        private static void ValidateLimits(Mo2ArchiveExtractionLimits limits)
        {
            if (limits.MaxExpandedBytes <= 0
                || limits.MaxSingleEntryBytes <= 0
                || !double.IsFinite(limits.MaxCompressionRatio)
                || limits.MaxCompressionRatio <= 0
                || limits.MinimumRatioAllowanceBytes < 0
                || limits.MinimumFreeSpaceReserveBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(limits), "Лимиты распаковки архива некорректны.");
            }
        }
    }
}
