using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Anthology.Mo2.Core;

namespace Anthology.Launcher;

public sealed record AnomalySaveCatalogItem(
    Mo2SaveEntry Save,
    string Title,
    string PlayerName,
    string CategoryKey,
    string CategoryLabel,
    string Description,
    string SizeLabel,
    string PartsLabel,
    string? PreviewDataUrl)
{
    public string SearchText => $"{Title} {PlayerName} {CategoryLabel} {Save.SaveName}";
}

public sealed class AnomalySaveCatalogService
{
    private readonly ConcurrentDictionary<string, string> _previewCache = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<AnomalySaveCatalogItem> Load(string? gameRoot)
    {
        return Mo2WorkspaceReader.ReadSaves(gameRoot)
            .Select(CreateCatalogItem)
            .ToArray();
    }

    private AnomalySaveCatalogItem CreateCatalogItem(Mo2SaveEntry save)
    {
        var (playerName, title) = SplitSaveName(save.SaveName);
        var (categoryKey, categoryLabel, description) = DescribeSave(title);
        return new AnomalySaveCatalogItem(
            save,
            title,
            playerName,
            categoryKey,
            categoryLabel,
            description,
            FormatSize(save.Size),
            save switch
            {
                { HasScop: true, HasScoc: true } => ".scop + .scoc",
                { HasScop: true } => ".scop",
                _ => ".scoc",
            },
            CreatePreviewDataUrl(save.PreviewPath));
    }

    private string? CreatePreviewDataUrl(string? previewPath)
    {
        if (string.IsNullOrWhiteSpace(previewPath) || !File.Exists(previewPath))
        {
            return null;
        }

        try
        {
            var file = new FileInfo(previewPath);
            var cacheKey = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            return _previewCache.GetOrAdd(cacheKey, _ =>
            {
                var decoded = DdsPreviewDecoder.DecodeDxt1(File.ReadAllBytes(file.FullName));
                var bitmap = BitmapSource.Create(
                    decoded.Width,
                    decoded.Height,
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null,
                    decoded.Bgra32,
                    decoded.Width * 4);
                bitmap.Freeze();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = new MemoryStream();
                encoder.Save(output);
                return $"data:image/png;base64,{Convert.ToBase64String(output.ToArray())}";
            });
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidDataException
                                           or UnauthorizedAccessException
                                           or NotSupportedException)
        {
            return null;
        }
    }

    private static (string PlayerName, string Title) SplitSaveName(string saveName)
    {
        var separator = saveName.IndexOf(" - ", StringComparison.Ordinal);
        if (separator <= 0 || separator + 3 >= saveName.Length)
        {
            return ("Профиль Anomaly", saveName);
        }

        return (saveName[..separator].Trim(), saveName[(separator + 3)..].Trim());
    }

    private static (string Key, string Label, string Description) DescribeSave(string title)
    {
        var normalized = title.ToLowerInvariant();
        if (normalized.Contains("quicksave", StringComparison.Ordinal))
        {
            return ("quick", "БЫСТРОЕ", "Быстрая точка сохранения");
        }

        if (normalized.Contains("autosave", StringComparison.Ordinal))
        {
            return ("auto", "АВТОМАТИЧЕСКОЕ", "Автоматическая точка восстановления");
        }

        if (normalized.Contains("tempsave", StringComparison.Ordinal))
        {
            return ("temporary", "ВРЕМЕННОЕ", "Временное системное сохранение");
        }

        if (normalized.Contains("fatal_ctd_save", StringComparison.Ordinal))
        {
            return ("emergency", "АВАРИЙНОЕ", "Сохранение, созданное перед аварийным завершением");
        }

        return ("manual", "РУЧНОЕ", "Ручное или сюжетное сохранение");
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / (1024d * 1024d):0.0} МБ";
        }

        return bytes >= 1024L ? $"{bytes / 1024d:0} КБ" : $"{bytes} Б";
    }
}
