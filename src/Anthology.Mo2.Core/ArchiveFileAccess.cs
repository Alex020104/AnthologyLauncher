using System.Diagnostics;
using System.Globalization;
using System.Text;
using SharpCompress.Readers;

namespace Anthology.Mo2.Core;

internal sealed record ArchiveFileEntry(string Path, string SourcePath, long Size);

internal static class ArchiveFileAccess
{
    internal const int MaxArchiveEntries = 1_000_000;
    internal const int MaxArchivePathCharacters = 32_767;
    internal const long MaxTotalArchivePathCharacters = 32L * 1024 * 1024;

    public static bool UsesNativeSevenZip(string archivePath) =>
        Path.GetExtension(archivePath).Equals(".7z", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<ArchiveFileEntry> ReadFileEntries(
        string archivePath,
        CancellationToken cancellationToken)
    {
        var entries = UsesNativeSevenZip(archivePath)
            ? NativeSevenZip.ReadFileEntries(archivePath, cancellationToken)
            : ReadWithSharpCompress(archivePath, cancellationToken);

        var duplicate = entries
            .GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Архив содержит повторяющиеся пути файлов: {duplicate.Key}");
        }
        return entries;
    }

    public static string NormalizeFilePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.Length > MaxArchivePathCharacters)
        {
            throw new InvalidDataException("Путь внутри архива превышает безопасную длину Windows.");
        }
        if (normalized.EndsWith('/')
            || normalized.Contains("//", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Архив содержит неоднозначный путь Windows: {path}");
        }
        return FomodPath.NormalizeRelativePath(normalized, allowEmpty: false);
    }

    public static FileStream OpenArchiveLease(string archivePath) => new(
        archivePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        1024 * 1024,
        FileOptions.SequentialScan);

    private static List<ArchiveFileEntry> ReadWithSharpCompress(
        string archivePath,
        CancellationToken cancellationToken)
    {
        var entries = new List<ArchiveFileEntry>();
        var entryCount = 0;
        long totalPathCharacters = 0;
        using var archiveStream = OpenArchiveLease(archivePath);
        using var reader = ReaderFactory.OpenReader(archiveStream);
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            entryCount++;
            EnsureEntryLimit(entryCount);
            if (reader.Entry.IsDirectory)
            {
                continue;
            }

            var sourcePath = reader.Entry.Key ?? string.Empty;
            var path = NormalizeFilePath(sourcePath);
            EnsurePathBudget(ref totalPathCharacters, path);
            entries.Add(new ArchiveFileEntry(path, sourcePath, reader.Entry.Size));
        }
        return entries;
    }

    internal static void EnsureEntryLimit(int entryCount)
    {
        if (entryCount > MaxArchiveEntries)
        {
            throw new InvalidDataException("Архив содержит слишком много элементов.");
        }
    }

    internal static void EnsurePathBudget(ref long totalPathCharacters, string path)
    {
        if (path.Length > MaxTotalArchivePathCharacters - totalPathCharacters)
        {
            throw new InvalidDataException("Суммарная длина путей внутри архива превышает безопасный предел.");
        }
        totalPathCharacters += path.Length;
    }
}

internal sealed class NativeSevenZipCache : IDisposable
{
    private const string CachePrefix = "anthology-mo2-7z-";
    private static int _staleSweepAttempted;
    private readonly string _archivePath;
    private readonly Dictionary<string, ArchiveFileEntry> _entries;
    private readonly Dictionary<string, string> _extractedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private bool _disposed;

    private NativeSevenZipCache(
        string archivePath,
        IReadOnlyList<ArchiveFileEntry> entries,
        string root)
    {
        _archivePath = archivePath;
        _entries = entries.ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        Root = root;
    }

    public string Root { get; }

    public static NativeSevenZipCache Create(
        string archivePath,
        IReadOnlyList<ArchiveFileEntry> entries)
    {
        SweepStaleCaches();
        var root = Path.Combine(
            Path.GetTempPath(),
            $"{CachePrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new NativeSevenZipCache(archivePath, entries, root);
    }

    public void EnsureExtracted(
        IEnumerable<string> archivePaths,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            var pending = archivePaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => !_extractedPaths.ContainsKey(path))
                .Select(path => _entries.TryGetValue(path, out var entry)
                    ? entry
                    : throw new InvalidDataException($"В архиве отсутствует файл: {path}"))
                .ToArray();
            if (pending.Length == 0)
            {
                return;
            }

            var batchRoot = Path.Combine(Root, $"batch-{Guid.NewGuid():N}");
            try
            {
                NativeSevenZip.ExtractFiles(_archivePath, pending, batchRoot, cancellationToken);
                foreach (var entry in pending)
                {
                    _extractedPaths[entry.Path] = NativeSevenZip.GetExtractedPath(batchRoot, entry.Path);
                }
            }
            catch
            {
                NativeSevenZip.DeleteDirectoryBestEffort(batchRoot);
                throw;
            }
        }
    }

    public string GetPath(string archivePath)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return _extractedPaths.TryGetValue(archivePath, out var path)
                ? path
                : throw new InvalidOperationException($"Файл архива ещё не распакован: {archivePath}");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _extractedPaths.Clear();
        }
        NativeSevenZip.DeleteDirectoryBestEffort(Root);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void SweepStaleCaches()
    {
        if (Interlocked.Exchange(ref _staleSweepAttempted, 1) != 0)
        {
            return;
        }

        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        try
        {
            foreach (var candidate in Directory.EnumerateDirectories(temporaryRoot, $"{CachePrefix}*"))
            {
                var fullPath = Path.GetFullPath(candidate);
                var name = Path.GetFileName(fullPath);
                if (!fullPath.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                    || !name.StartsWith(CachePrefix, StringComparison.Ordinal)
                    || !Guid.TryParseExact(name[CachePrefix.Length..], "N", out _)
                    || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0
                    || Directory.GetLastWriteTimeUtc(fullPath) > DateTime.UtcNow.AddDays(-1))
                {
                    continue;
                }
                NativeSevenZip.DeleteDirectoryBestEffort(fullPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Очистка следов аварийно завершившегося прошлого запуска не должна
            // мешать открыть текущий мастер установки.
        }
    }
}

internal static class NativeSevenZip
{
    private const int MaxListingCharacters = 64 * 1024 * 1024;
    private const int MaxDiagnosticCharacters = 1024 * 1024;
    private static readonly Encoding OutputEncoding = CreateOutputEncoding();

    public static IReadOnlyList<ArchiveFileEntry> ReadFileEntries(
        string archivePath,
        CancellationToken cancellationToken)
    {
        var namesOutput = RunTar(
            ["-tf", Path.GetFullPath(archivePath)],
            MaxListingCharacters,
            cancellationToken);
        var verboseOutput = RunTar(
            ["-tvf", Path.GetFullPath(archivePath)],
            MaxListingCharacters,
            cancellationToken);
        var names = SplitOutputLines(namesOutput);
        var verboseLines = SplitOutputLines(verboseOutput);
        if (names.Length != verboseLines.Length)
        {
            throw new InvalidDataException(
                $"Системный распаковщик вернул несовпадающие списки архива ({names.Length} и {verboseLines.Length} элементов).");
        }
        ArchiveFileAccess.EnsureEntryLimit(names.Length);

        var result = new List<ArchiveFileEntry>(names.Length);
        long totalPathCharacters = 0;
        for (var index = 0; index < names.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = names[index];
            if (sourcePath.Length == 0 || sourcePath.Contains('\0'))
            {
                throw new InvalidDataException("Системный распаковщик вернул некорректный пустой путь.");
            }
            if (sourcePath.Length > ArchiveFileAccess.MaxArchivePathCharacters)
            {
                throw new InvalidDataException("Путь внутри архива превышает безопасную длину Windows.");
            }

            var verbose = verboseLines[index];
            var type = verbose.FirstOrDefault(character => !char.IsWhiteSpace(character));
            if (type == 'd')
            {
                continue;
            }
            if (type != '-')
            {
                throw new InvalidDataException(
                    $"Архив 7z содержит неподдерживаемую ссылку или специальный элемент: {sourcePath}");
            }
            if (!verbose.EndsWith(sourcePath, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Не удалось прочитать свойства элемента архива: {sourcePath}");
            }

            var metadata = verbose[..^sourcePath.Length].TrimEnd();
            var fields = metadata.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5
                || !long.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                || size < 0)
            {
                throw new InvalidDataException($"Не удалось определить распакованный размер: {sourcePath}");
            }

            // Validate before this path can ever be handed back to tar for
            // extraction. Post-extraction checks are too late for rooted paths
            // or traversal components.
            var path = ArchiveFileAccess.NormalizeFilePath(sourcePath);
            ArchiveFileAccess.EnsurePathBudget(ref totalPathCharacters, path);
            result.Add(new ArchiveFileEntry(path, sourcePath, size));
        }
        return result;
    }

    public static void ExtractFiles(
        string archivePath,
        IReadOnlyCollection<ArchiveFileEntry> entries,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            Directory.CreateDirectory(destinationRoot);
            return;
        }

        var fullDestination = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(fullDestination);
        if (Directory.EnumerateFileSystemEntries(fullDestination).Any())
        {
            throw new InvalidOperationException("Для системной распаковки требуется пустая временная папка.");
        }

        var selectionPath = Path.Combine(
            Path.GetDirectoryName(fullDestination)!,
            $".__anthology_tar_selection_{Guid.NewGuid():N}.bin");
        try
        {
            WriteSelectionFile(selectionPath, entries);
            RunTar(
                [
                    "-xf", Path.GetFullPath(archivePath),
                    "-C", fullDestination,
                    "-m",
                    "-k",
                    "--safe-writes",
                    "--no-same-owner",
                    "--no-same-permissions",
                    "--no-xattrs",
                    "--no-acls",
                    "--no-fflags",
                    "--no-recursion",
                    "--null",
                    "-T", selectionPath
                ],
                MaxDiagnosticCharacters,
                cancellationToken);
            ValidateExtractedTree(fullDestination, entries, cancellationToken);
        }
        finally
        {
            if (File.Exists(selectionPath))
            {
                File.Delete(selectionPath);
            }
        }
    }

    public static string GetExtractedPath(string extractionRoot, string archivePath)
    {
        var fullRoot = Path.GetFullPath(extractionRoot);
        var path = Path.GetFullPath(Path.Combine(
            fullRoot,
            archivePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Путь архива выходит за пределы папки распаковки: {archivePath}");
        }
        return path;
    }

    public static void DeleteDirectoryBestEffort(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Antivirus software can briefly retain a just-closed image.
                // Retry before falling back to the next-launch stale-cache sweep.
                if (attempt < 2)
                {
                    Thread.Sleep(40 * (attempt + 1));
                }
            }
        }
    }

    private static void WriteSelectionFile(
        string selectionPath,
        IEnumerable<ArchiveFileEntry> entries)
    {
        using var output = new FileStream(selectionPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        foreach (var entry in entries)
        {
            var bytes = OutputEncoding.GetBytes(entry.SourcePath);
            output.Write(bytes);
            output.WriteByte(0);
        }
    }

    private static void ValidateExtractedTree(
        string extractionRoot,
        IReadOnlyCollection<ArchiveFileEntry> entries,
        CancellationToken cancellationToken)
    {
        var expectedFiles = entries.ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        var expectedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { string.Empty };
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var separator = entry.Path.LastIndexOf('/');
            while (separator > 0)
            {
                expectedDirectories.Add(entry.Path[..separator]);
                separator = entry.Path.LastIndexOf('/', separator - 1);
            }
        }

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(Path.GetFullPath(extractionRoot));
        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pendingDirectories.Pop();
            foreach (var item in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"При распаковке обнаружена небезопасная ссылка: {item}");
                }

                var relative = Path.GetRelativePath(extractionRoot, item).Replace('\\', '/');
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!expectedDirectories.Contains(relative))
                    {
                        throw new InvalidDataException($"Системный распаковщик создал неожиданную папку: {relative}");
                    }
                    pendingDirectories.Push(item);
                    continue;
                }

                var normalized = ArchiveFileAccess.NormalizeFilePath(relative);
                if (!expectedFiles.TryGetValue(normalized, out var entry))
                {
                    throw new InvalidDataException($"Системный распаковщик создал неожиданный файл: {relative}");
                }
                var actualSize = new FileInfo(item).Length;
                if (actualSize != entry.Size)
                {
                    throw new InvalidDataException(
                        $"Размер распакованного файла отличается от проверенного архива: {entry.Path}");
                }
                found.Add(normalized);
            }
        }

        var missing = expectedFiles.Keys.FirstOrDefault(path => !found.Contains(path));
        if (missing is not null)
        {
            throw new InvalidDataException($"Системный распаковщик не извлёк запрошенный файл: {missing}");
        }
    }

    private static string RunTar(
        IReadOnlyList<string> arguments,
        int maxStandardOutputCharacters,
        CancellationToken cancellationToken) =>
        RunTarAsync(arguments, maxStandardOutputCharacters, cancellationToken).GetAwaiter().GetResult();

    private static async Task<string> RunTarAsync(
        IReadOnlyList<string> arguments,
        int maxStandardOutputCharacters,
        CancellationToken cancellationToken)
    {
        var tarPath = ResolveTarPath();
        var startInfo = new ProcessStartInfo
        {
            FileName = tarPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = OutputEncoding,
            StandardErrorEncoding = OutputEncoding
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Не удалось запустить системный распаковщик Windows.");
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException("Не удалось запустить системный распаковщик Windows.", exception);
        }

        var stdoutTask = ReadBoundedTextAsync(
            process.StandardOutput,
            maxStandardOutputCharacters,
            "вывод списка архива");
        var stderrTask = ReadBoundedTextAsync(
            process.StandardError,
            MaxDiagnosticCharacters,
            "диагностика распаковщика");
        var exitTask = process.WaitForExitAsync(cancellationToken);
        try
        {
            // Waiting only for process exit can deadlock when a reader faults and
            // stops draining its redirected pipe. Observe each task as soon as it
            // completes so any reader failure immediately tears down tar.
            var pending = new HashSet<Task> { exitTask, stdoutTask, stderrTask };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(completed);
                await completed.ConfigureAwait(false);
            }
        }
        catch
        {
            TryKillProcess(process);
            await CompleteFailedProcessAsync(process, exitTask, stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidDataException(
                $"Системный распаковщик Windows не смог обработать архив 7z (код {process.ExitCode}): {detail.Trim()}");
        }
        return stdout;
    }

    private static async Task<string> ReadBoundedTextAsync(
        StreamReader reader,
        int maxCharacters,
        string description)
    {
        var result = new StringBuilder(Math.Min(maxCharacters, 8192));
        var buffer = new char[8192];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
            if (read == 0)
            {
                return result.ToString();
            }
            if (read > maxCharacters - result.Length)
            {
                throw new InvalidDataException(
                    $"Объём данных «{description}» превысил безопасный предел {maxCharacters} символов.");
            }
            result.Append(buffer, 0, read);
        }
    }

    private static async Task CompleteFailedProcessAsync(
        Process process,
        params Task[] startedTasks)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cleanup must preserve the original reader/process/cancellation
            // failure. The already-started tasks are still observed below.
        }

        foreach (var startedTask in startedTasks)
        {
            try
            {
                await startedTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Preserve the original failure while observing every task so
                // no redirected-stream exception is left unobserved.
            }
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (IsSafeProcessCleanupException(exception))
        {
            // The process exited concurrently, cannot be accessed, or the
            // platform does not support killing the complete process tree.
        }
    }

    private static bool IsSafeProcessCleanupException(Exception exception) =>
        exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException;

    private static string ResolveTarPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Для архивов 7z требуется системный распаковщик Windows.");
        }
        var tarPath = Path.Combine(Environment.SystemDirectory, "tar.exe");
        if (!File.Exists(tarPath))
        {
            throw new FileNotFoundException("Системный распаковщик Windows (tar.exe) не найден.", tarPath);
        }
        return tarPath;
    }

    private static string[] SplitOutputLines(string value) => value
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private static Encoding CreateOutputEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            // Windows bsdtar writes redirected archive names through the
            // process ANSI code page (1251 on a Russian installation), not the
            // console OEM page. Using OEM here turns Cyrillic paths into mojibake
            // and later makes an otherwise valid FOMOD image impossible to find.
            return Encoding.GetEncoding(
                CultureInfo.CurrentCulture.TextInfo.ANSICodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }
}
