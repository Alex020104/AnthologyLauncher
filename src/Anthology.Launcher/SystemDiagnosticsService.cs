using System.Globalization;
using System.IO;
using System.Management;
using System.Reflection;

namespace Anthology.Launcher;

public sealed record SystemDiagnosticsSnapshot(
    string Cpu,
    IReadOnlyList<string> Gpus,
    ulong MemoryBytes,
    string OperatingSystem,
    string GameRoot,
    string GameDrive,
    string StorageModel,
    string StorageFormat,
    long StorageTotalBytes,
    long StorageFreeBytes,
    long PageFileBytes,
    DateTimeOffset CollectedAt)
{
    public static SystemDiagnosticsSnapshot Pending(string? gameRoot = null) => new(
        "Сбор данных…",
        ["Сбор данных…"],
        0,
        "Сбор данных…",
        string.IsNullOrWhiteSpace(gameRoot) ? "Корень игры пока не определён" : gameRoot,
        "—",
        "Сбор данных…",
        "—",
        0,
        0,
        0,
        DateTimeOffset.Now);

    public string GpuText => Gpus.Count == 0 ? "Не определено автоматически" : string.Join("; ", Gpus);

    public string MemoryText => FormatBytes(MemoryBytes);

    public string StorageText
    {
        get
        {
            var model = string.IsNullOrWhiteSpace(StorageModel) ? "модель не определена" : StorageModel;
            var format = string.IsNullOrWhiteSpace(StorageFormat) ? "файловая система не определена" : StorageFormat;
            return $"{GameDrive} · {model} · {format} · всего {FormatBytes((ulong)Math.Max(0, StorageTotalBytes))} · свободно {FormatBytes((ulong)Math.Max(0, StorageFreeBytes))}";
        }
    }

    public string ToReportText()
    {
        var launcherVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "не определена";
        return string.Join(Environment.NewLine,
        [
            $"CPU: {Cpu}",
            $"GPU: {GpuText}",
            $"RAM: {MemoryText}",
            $"Накопитель игры: {StorageText}",
            $"Корень игры: {GameRoot}",
            $"Файл подкачки: {FormatBytes((ulong)Math.Max(0, PageFileBytes))}",
            $"ОС: {OperatingSystem}",
            $"Лаунчер: {launcherVersion}",
            $"Диагностика собрана: {CollectedAt.LocalDateTime:dd.MM.yyyy HH:mm:ss}",
        ]);
    }

    private static string FormatBytes(ulong bytes)
    {
        if (bytes == 0)
        {
            return "не определено";
        }

        var value = (double)bytes;
        var units = new[] { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}

public sealed class SystemDiagnosticsService
{
    public static SystemDiagnosticsSnapshot Collect(string? configuredGameRoot)
    {
        var gameRoot = ResolveGameRoot(configuredGameRoot);
        var driveRoot = Path.GetPathRoot(gameRoot) ?? "не определён";
        var driveInfo = TryGetDriveInfo(driveRoot);

        return new SystemDiagnosticsSnapshot(
            QueryFirst("SELECT Name FROM Win32_Processor", "Name"),
            QueryMany("SELECT Name FROM Win32_VideoController", "Name"),
            QueryUInt64("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", "TotalPhysicalMemory"),
            QueryOperatingSystem(),
            gameRoot,
            driveRoot.TrimEnd(Path.DirectorySeparatorChar),
            QueryStorageModel(driveRoot),
            driveInfo?.DriveFormat ?? "не определено",
            driveInfo?.TotalSize ?? 0,
            driveInfo?.AvailableFreeSpace ?? 0,
            checked((long)Math.Min(long.MaxValue, QueryUInt64("SELECT AllocatedBaseSize FROM Win32_PageFileUsage", "AllocatedBaseSize", sumValues: true) * 1024UL * 1024UL)),
            DateTimeOffset.Now);
    }

    private static string ResolveGameRoot(string? configuredGameRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredGameRoot))
        {
            try
            {
                return Path.GetFullPath(configuredGameRoot);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Fall back to the launcher location and still provide useful hardware data.
            }
        }
        return Path.GetFullPath(AppContext.BaseDirectory);
    }

    private static DriveInfo? TryGetDriveInfo(string driveRoot)
    {
        try
        {
            var drive = new DriveInfo(driveRoot);
            return drive.IsReady ? drive : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string QueryFirst(string query, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    var value = Convert.ToString(item[property], CultureInfo.InvariantCulture)?.Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The report must remain available even when WMI is disabled by the system administrator.
        }
        return "Не определено автоматически";
    }

    private static string[] QueryMany(string query, string property)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    var value = Convert.ToString(item[property], CultureInfo.InvariantCulture)?.Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        values.Add(value);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or InvalidOperationException)
        {
            // See QueryFirst.
        }
        return values.ToArray();
    }

    private static ulong QueryUInt64(string query, string property, bool sumValues = false)
    {
        ulong result = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    if (item[property] is not null
                        && ulong.TryParse(Convert.ToString(item[property], CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    {
                        result += value;
                        if (!sumValues)
                        {
                            return result;
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or InvalidOperationException or OverflowException)
        {
            // See QueryFirst.
        }
        return result;
    }

    private static string QueryOperatingSystem()
    {
        var caption = QueryFirst("SELECT Caption FROM Win32_OperatingSystem", "Caption");
        var version = QueryFirst("SELECT Version FROM Win32_OperatingSystem", "Version");
        return $"{caption} · {version} · {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}";
    }

    private static string QueryStorageModel(string driveRoot)
    {
        var driveId = driveRoot.TrimEnd(Path.DirectorySeparatorChar).Replace("'", "''", StringComparison.Ordinal);
        if (driveId.Length != 2 || driveId[1] != ':')
        {
            return "Не определено автоматически";
        }

        try
        {
            using var logicalDisk = new ManagementObject($"Win32_LogicalDisk.DeviceID='{driveId}'");
            logicalDisk.Get();
            using var partitions = logicalDisk.GetRelated("Win32_DiskPartition");
            foreach (ManagementObject partition in partitions)
            {
                using (partition)
                using (var drives = partition.GetRelated("Win32_DiskDrive"))
                {
                    foreach (ManagementObject drive in drives)
                    {
                        using (drive)
                        {
                            var model = Convert.ToString(drive["Model"], CultureInfo.InvariantCulture)?.Trim();
                            var media = Convert.ToString(drive["MediaType"], CultureInfo.InvariantCulture)?.Trim();
                            if (!string.IsNullOrWhiteSpace(model))
                            {
                                return string.IsNullOrWhiteSpace(media) ? model : $"{model} ({media})";
                            }
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Drive letter, capacity and free space are still included in the report.
        }
        return "Не определено автоматически";
    }
}
