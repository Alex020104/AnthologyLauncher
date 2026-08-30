using Anthology.Mo2.Core;
using System.IO.Compression;

namespace Anthology.Update.Core.Tests;

public sealed class Mo2ProfileManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"anthology-mo2-{Guid.NewGuid():N}");

    [Fact]
    public void DetectReadsQtProfileAndConfiguredExecutables()
    {
        CreateInstance();
        var snapshot = Mo2ProfileManager.Detect(_root);

        Assert.True(snapshot.Available);
        Assert.Equal("Anthology Стандарт", snapshot.SelectedProfile);
        Assert.Single(snapshot.Profiles);
        Assert.Equal("Anomaly (DX11-AVX)", Assert.Single(snapshot.Executables).Title);
    }

    [Fact]
    public void ToggleAndMovePreserveCommentsAndCreateBackup()
    {
        CreateInstance();
        Mo2ProfileManager.SetEnabled(_root, "Anthology Стандарт", "Second", true);
        var updated = Mo2ProfileManager.Move(_root, "Anthology Стандарт", "First", -1);

        Assert.Equal(["First", "Second"], updated.Mods.Select(mod => mod.Name));
        Assert.All(updated.Mods, mod => Assert.True(mod.Enabled));
        var path = Path.Combine(_root, "profiles", "Anthology Стандарт", "modlist.txt");
        Assert.Contains("# generated", File.ReadAllLines(path));
        Assert.True(File.Exists(path + ".anthology-backup"));
    }

    [Fact]
    public void DragMovePlacesModAtDroppedPriorityAndPreservesComments()
    {
        CreateInstance();
        var path = Path.Combine(_root, "profiles", "Anthology Стандарт", "modlist.txt");
        File.WriteAllText(path, "# generated\n+Fourth\n+Third\n# keep me\n+Second\n+First\n");

        var updated = Mo2ProfileManager.MoveTo(_root, "Anthology Стандарт", "First", "Third");

        Assert.Equal(["Second", "Third", "First", "Fourth"], updated.Mods.Select(mod => mod.Name));
        Assert.Contains("# keep me", File.ReadAllLines(path));
        Assert.True(File.Exists(path + ".anthology-backup"));
    }

    [Fact]
    public void ReadProfileUsesMo2PriorityOrderAndNamesSeparators()
    {
        CreateInstance();
        var path = Path.Combine(_root, "profiles", "Anthology Стандарт", "modlist.txt");
        File.WriteAllText(path, "# generated\n+High\n+Child\n-Visual_separator\n+Low\n");

        var profile = Mo2ProfileManager.ReadProfile(_root, "Anthology Стандарт");

        Assert.Equal(["Low", "Visual_separator", "Child", "High"], profile.Mods.Select(mod => mod.Name));
        Assert.Equal([0, 1, 2, 3], profile.Mods.Select(mod => mod.Order));
        Assert.Equal("Visual", profile.Mods[1].DisplayName);
        Assert.True(profile.Mods[1].IsSeparator);
    }

    [Fact]
    public void ProfileTraversalIsRejected()
    {
        CreateInstance();
        Assert.Throws<DirectoryNotFoundException>(() => Mo2ProfileManager.ReadProfile(_root, ".."));
    }

    [Fact]
    public void SelectedProfileIsWrittenInQtFormatAndBackedUp()
    {
        CreateInstance();
        var secondProfile = Path.Combine(_root, "profiles", "Anthology Лёгкий");
        Directory.CreateDirectory(secondProfile);
        File.WriteAllText(Path.Combine(secondProfile, "modlist.txt"), "+First\n");

        Mo2ProfileManager.SetSelectedProfile(_root, "Anthology Лёгкий");

        var snapshot = Mo2ProfileManager.Detect(_root);
        Assert.Equal("Anthology Лёгкий", snapshot.SelectedProfile);
        Assert.True(File.Exists(Path.Combine(_root, "ModOrganizer.ini.anthology-backup")));
    }

    [Fact]
    public void ContentIndexFindsWinningConflictsAndBrowsesVirtualTree()
    {
        CreateInstance();
        var profilePath = Path.Combine(_root, "profiles", "Anthology Стандарт", "modlist.txt");
        File.WriteAllText(profilePath, "+High\n+Low\n");
        WriteModFile("Low", "gamedata/configs/shared.ltx", "low");
        WriteModFile("Low", "gamedata/scripts/only_low.script", "low");
        WriteModFile("High", "gamedata/configs/shared.ltx", "high");

        var instance = Mo2ProfileManager.Detect(_root);
        var profile = Mo2ProfileManager.ReadProfile(_root, "Anthology Стандарт");
        var index = Mo2ContentIndex.Build(instance, profile);

        Assert.Equal(2, index.Overview.UniqueFiles);
        Assert.Equal(1, index.Overview.ConflictFiles);
        Assert.Equal(1, index.Overview.Conflicts["High"].WinningConflicts);
        Assert.Equal(1, index.Overview.Conflicts["Low"].LosingConflicts);
        var conflict = Assert.Single(index.GetConflicts("Low"));
        Assert.Equal("High", conflict.Winner);
        Assert.Contains(index.Browse("gamedata"), item => item.IsDirectory && item.Name == "configs");
        var search = Assert.Single(index.Search("shared.ltx"));
        Assert.Equal("gamedata/configs/shared.ltx", search.RelativePath);
        Assert.Equal("High", search.Source);
        Assert.Equal(2, search.ProviderCount);
    }

    [Fact]
    public void ZipArchiveInstallsAtomicallyAndEnablesMod()
    {
        CreateInstance();
        var archivePath = Path.Combine(_root, "Test Addon.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("Test Addon/gamedata/configs/test.ltx");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("[test]");
        }

        var result = Mo2ArchiveInstaller.Install(_root, "Anthology Стандарт", archivePath);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(_root, "mods", "Test Addon", "gamedata", "configs", "test.ltx")));
        Assert.Contains("+Test Addon", File.ReadAllLines(Path.Combine(_root, "profiles", "Anthology Стандарт", "modlist.txt")));
    }

    [Fact]
    public void PublishedModUpdateReplacesExistingFolderAndEnablesIt()
    {
        CreateInstance();
        WriteModFile("anthology-weather", "gamedata/configs/weather.ltx", "old");
        var modList = Path.Combine(_root, "profiles", "Anthology Стандарт", "modlist.txt");
        File.WriteAllText(modList, "# generated\n-anthology-weather\n");
        var archivePath = Path.Combine(_root, "weather-2.0.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("gamedata/configs/weather.ltx");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("new");
        }

        var result = Mo2ArchiveInstaller.Install(
            _root,
            "Anthology Стандарт",
            archivePath,
            "anthology-weather",
            replaceExisting: true);

        Assert.True(result.Success);
        Assert.Equal("new", File.ReadAllText(Path.Combine(_root, "mods", "anthology-weather", "gamedata", "configs", "weather.ltx")));
        Assert.Contains("+anthology-weather", File.ReadAllLines(modList));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "mods"), ".__anthology_*"));
    }

    [Fact]
    public void ProfileCopyAndSeparatorUseNativeMo2Files()
    {
        CreateInstance();

        Mo2ProfileManager.CreateProfile(_root, "Новый профиль", "Anthology Стандарт");
        var separator = Mo2ProfileManager.AddSeparator(_root, "Новый профиль", "МОИ МОДЫ");

        Assert.True(File.Exists(Path.Combine(_root, "profiles", "Новый профиль", "modlist.txt")));
        Assert.True(Directory.Exists(Path.Combine(_root, "mods", "МОИ МОДЫ_separator")));
        Assert.Contains("+МОИ МОДЫ_separator", File.ReadAllLines(Path.Combine(_root, "profiles", "Новый профиль", "modlist.txt")));
        Assert.Equal("МОИ МОДЫ_separator", separator);
    }

    private void CreateInstance()
    {
        var profile = Path.Combine(_root, "profiles", "Anthology Стандарт");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(_root, "ModOrganizer.exe"), string.Empty);
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# generated\n+First\n-Second\n");
        File.WriteAllText(Path.Combine(_root, "ModOrganizer.ini"), """
            [General]
            selected_profile=@ByteArray(Anthology \xd0\xa1\xd1\x82\xd0\xb0\xd0\xbd\xd0\xb4\xd0\xb0\xd1\x80\xd1\x82)
            [customExecutables]
            size=1
            1\binary=C:/Games/Anomaly/bin/AnomalyDX11AVX.exe
            1\arguments=
            1\title=Anomaly (DX11-AVX)
            1\workingDirectory=C:/Games/Anomaly/bin
            """);
    }

    private void WriteModFile(string modName, string relativePath, string contents)
    {
        var path = Path.Combine(_root, "mods", modName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
