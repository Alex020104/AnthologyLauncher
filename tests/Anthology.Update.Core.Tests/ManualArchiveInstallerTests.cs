using System.IO.Compression;
using System.Diagnostics;
using System.Text;
using Anthology.Mo2.Core;

namespace Anthology.Update.Core.Tests;

public sealed class ManualArchiveInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"anthology-manual-archive-{Guid.NewGuid():N}");
    private readonly List<Mo2ManualArchivePackage> _packages = [];

    [Fact]
    public void InspectManualArchiveBuildsRecursiveCatalogAndSuggestsWrapper()
    {
        var archivePath = CreateArchive("wrapped.zip", new Dictionary<string, byte[]>
        {
            ["Wrapper/gamedata/configs/base.ltx"] = Encoding.UTF8.GetBytes("base"),
            ["Wrapper/gamedata/textures/icon.dds"] = new byte[7],
            ["Wrapper/readme.txt"] = Encoding.UTF8.GetBytes("readme")
        });

        var package = Inspect(archivePath);

        Assert.Equal("Wrapper", package.SuggestedRoot);
        Assert.Equal(3, package.FileCount);
        Assert.Equal(17, package.ExpandedBytes);
        AssertDirectory(package, "", 3, 17);
        AssertDirectory(package, "Wrapper", 3, 17);
        AssertDirectory(package, "Wrapper/gamedata", 2, 11);
        AssertDirectory(package, "Wrapper/gamedata/configs", 1, 4);
        AssertDirectory(package, "Wrapper/gamedata/textures", 1, 7);
    }

    [Fact]
    public void InstallManualArchiveUsesOnlySelectedSourceRoot()
    {
        CreateMo2Instance();
        var archivePath = CreateArchive("choices.zip", new Dictionary<string, byte[]>
        {
            ["Container/Choice A/gamedata/choice.txt"] = Encoding.UTF8.GetBytes("A"),
            ["Container/Choice A/docs/a.txt"] = Encoding.UTF8.GetBytes("docs A"),
            ["Container/Choice B/gamedata/choice.txt"] = Encoding.UTF8.GetBytes("B"),
            ["Container/Choice B/docs/b.txt"] = Encoding.UTF8.GetBytes("docs B")
        });
        var package = Inspect(archivePath);

        var result = Mo2ArchiveInstaller.Install(
            _root,
            "Profile",
            package,
            "Container/Choice B",
            installName: "Selected Choice");

        Assert.True(result.Success, result.Message);
        var installed = Path.Combine(_root, "mods", "Selected Choice");
        Assert.Equal("B", File.ReadAllText(Path.Combine(installed, "gamedata", "choice.txt")));
        Assert.Equal("docs B", File.ReadAllText(Path.Combine(installed, "docs", "b.txt")));
        Assert.False(File.Exists(Path.Combine(installed, "docs", "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(installed, "Container")));
        AssertDirectory(package, "Container/Choice B", 2, 7);
    }

    [Fact]
    public void InstallManualArchiveRejectsDirectoryOutsideReviewedCatalog()
    {
        CreateMo2Instance();
        var package = Inspect(CreateArchive("invalid-root.zip", new Dictionary<string, byte[]>
        {
            ["Option/gamedata/file.txt"] = Encoding.UTF8.GetBytes("payload")
        }));

        Assert.Throws<ArgumentException>(() => Mo2ArchiveInstaller.Install(
            _root,
            "Profile",
            package,
            "Missing"));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "mods")));
    }

    [Fact]
    public void NativeSevenZipManualInstallPreservesCyrillicAndSelectedRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CreateMo2Instance();
        var inputRoot = Path.Combine(_root, "native-input");
        var selected = Path.Combine(inputRoot, "Обёртка", "Вариант Б", "gamedata");
        Directory.CreateDirectory(selected);
        File.WriteAllText(Path.Combine(selected, "проверка.txt"), "данные", Encoding.UTF8);
        var archivePath = Path.Combine(_root, "кириллица.7z");
        CreateNativeSevenZip(inputRoot, archivePath, "Обёртка");

        var package = Inspect(archivePath);
        var result = Mo2ArchiveInstaller.Install(
            _root,
            "Profile",
            package,
            "Обёртка/Вариант Б",
            installName: "Native 7z");

        Assert.True(result.Success, result.Message);
        Assert.Equal(
            "данные",
            File.ReadAllText(Path.Combine(_root, "mods", "Native 7z", "gamedata", "проверка.txt"), Encoding.UTF8));
        Assert.Empty(Directory.EnumerateDirectories(
            Path.Combine(_root, "mods", "Native 7z"),
            ".__anthology_7z_*"));
    }

    [Fact]
    public void NativeSevenZipFomodReadsCyrillicArtworkAndEmptyMo2Pattern()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CreateMo2Instance();
        var inputRoot = Path.Combine(_root, "native-fomod-input");
        var fomodRoot = Path.Combine(inputRoot, "FOMOD");
        Directory.CreateDirectory(fomodRoot);
        var payloadRoot = Path.Combine(inputRoot, "Данные");
        Directory.CreateDirectory(payloadRoot);
        File.WriteAllText(Path.Combine(payloadRoot, "АК-107.ltx"), "installed", Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(fomodRoot, "ModuleConfig.xml"),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <config>
              <moduleName>Нативный FOMOD</moduleName>
              <moduleImage path="FOMOD\АК-107.png" />
              <requiredInstallFiles><file source="Данные\АК-107.ltx" destination="gamedata\configs\АК-107.ltx" /></requiredInstallFiles>
              <conditionalFileInstalls><patterns><pattern /></patterns></conditionalFileInstalls>
            </config>
            """,
            Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(fomodRoot, "АК-107.png"), [1, 2, 3, 4]);
        var archivePath = Path.Combine(_root, "нативный-fomod.7z");
        CreateNativeSevenZip(inputRoot, archivePath, "FOMOD", "Данные");

        var inspection = Mo2ArchiveInstaller.InspectFomod(archivePath);
        Assert.True(inspection.Success, inspection.Message);
        using var package = Assert.IsType<FomodPackage>(inspection.Package);
        Assert.Equal("Нативный FOMOD", package.Module.Name);
        Assert.Empty(package.Module.ConditionalInstalls);
        Assert.Equal([1, 2, 3, 4], FomodArchiveReader.ReadAsset(package, "FOMOD/АК-107.png"));

        var plan = FomodEngine.BuildPlan(package);
        var result = Mo2ArchiveInstaller.InstallFomod(
            _root,
            "Profile",
            package,
            plan,
            installName: "Нативный FOMOD");

        Assert.True(result.Success, result.Message);
        Assert.Equal(
            "installed",
            File.ReadAllText(
                Path.Combine(_root, "mods", "Нативный FOMOD", "gamedata", "configs", "АК-107.ltx"),
                Encoding.UTF8));
    }

    private Mo2ManualArchivePackage Inspect(string archivePath)
    {
        var package = Mo2ArchiveInstaller.InspectManualArchive(archivePath);
        _packages.Add(package);
        return package;
    }

    private static void AssertDirectory(
        Mo2ManualArchivePackage package,
        string path,
        int fileCount,
        long expandedBytes)
    {
        var directory = Assert.Single(package.Directories, item => item.Path == path);
        Assert.Equal(fileCount, directory.FileCount);
        Assert.Equal(expandedBytes, directory.ExpandedBytes);
    }

    private string CreateArchive(string fileName, IReadOnlyDictionary<string, byte[]> entries)
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, fileName);
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (var pair in entries)
        {
            var entry = archive.CreateEntry(pair.Key);
            using var output = entry.Open();
            output.Write(pair.Value);
        }
        return archivePath;
    }

    private void CreateMo2Instance()
    {
        var profile = Path.Combine(_root, "profiles", "Profile");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        File.WriteAllText(Path.Combine(_root, "ModOrganizer.exe"), string.Empty);
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# generated\n");
        File.WriteAllText(Path.Combine(_root, "ModOrganizer.ini"), "[General]\n");
    }

    private static void CreateNativeSevenZip(
        string sourceRoot,
        string archivePath,
        params string[] entryNames)
    {
        var tarPath = Path.Combine(Environment.SystemDirectory, "tar.exe");
        Assert.True(File.Exists(tarPath), $"Системный распаковщик Windows не найден: {tarPath}");
        var startInfo = new ProcessStartInfo
        {
            FileName = tarPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-a", "-cf", archivePath, "-C", sourceRoot }.Concat(entryNames))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        Assert.Equal(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, File.ReadAllBytes(archivePath)[..6]);
    }

    public void Dispose()
    {
        foreach (var package in _packages)
        {
            package.Dispose();
        }
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
