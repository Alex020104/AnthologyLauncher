using Anthology.Mo2.Core;

namespace Anthology.Update.Core.Tests;

public sealed class AnomalyRuntimeMaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"anthology-shader-cache-{Guid.NewGuid():N}");

    [Fact]
    public void ClearShaderCacheRemovesEveryCachedFileAndRecreatesTheDirectory()
    {
        var cache = Path.Combine(_root, "appdata", "shaders_cache");
        var nested = Path.Combine(cache, "r4", "temporary.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllText(nested, "cached shader");

        AnomalyRuntimeMaintenance.ClearShaderCache(_root);

        Assert.True(Directory.Exists(cache));
        Assert.Empty(Directory.EnumerateFileSystemEntries(cache));
    }

    [Fact]
    public void ClearShaderCacheAlsoRemovesReadOnlyCacheFiles()
    {
        var cache = Path.Combine(_root, "appdata", "shaders_cache");
        Directory.CreateDirectory(cache);
        var readOnly = Path.Combine(cache, "readonly.cache");
        File.WriteAllText(readOnly, "cached");
        File.SetAttributes(readOnly, File.GetAttributes(readOnly) | FileAttributes.ReadOnly);

        AnomalyRuntimeMaintenance.ClearShaderCache(_root);

        Assert.True(Directory.Exists(cache));
        Assert.Empty(Directory.EnumerateFileSystemEntries(cache));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
