namespace Anthology.Mo2.Core;

/// <summary>
/// Performs the small, deterministic cleanup steps required immediately before
/// launching Anomaly.
/// </summary>
public static class AnomalyRuntimeMaintenance
{
    public static void ClearShaderCache(string gameRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);

        var fullGameRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
        var cacheRoot = Path.GetFullPath(Path.Combine(fullGameRoot, "appdata", "shaders_cache"));
        var expectedPrefix = fullGameRoot + Path.DirectorySeparatorChar;
        if (!cacheRoot.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Папка кэша шейдеров находится за пределами игры");
        }

        if (Directory.Exists(cacheRoot))
        {
            ClearReadOnlyAttributes(cacheRoot);
            Directory.Delete(cacheRoot, recursive: true);
        }

        Directory.CreateDirectory(cacheRoot);
    }

    private static void ClearReadOnlyAttributes(string cacheRoot)
    {
        foreach (var file in Directory.EnumerateFiles(
                     cacheRoot,
                     "*",
                     new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         AttributesToSkip = FileAttributes.ReparsePoint,
                         IgnoreInaccessible = false,
                     }))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }
}
