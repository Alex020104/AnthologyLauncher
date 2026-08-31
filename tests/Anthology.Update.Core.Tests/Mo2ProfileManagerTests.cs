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
    public void ReconcileProfileRemovesMissingModsAndAddsNewFoldersDisabled()
    {
        CreateInstance();
        var path = Path.Combine(_root, "profiles", "Anthology Стандарт", "modlist.txt");
        File.WriteAllText(path, "# generated\n+Missing enabled\n*Unmanaged game data\n+Installed\n-Existing disabled\n-.git\n");
        Directory.CreateDirectory(Path.Combine(_root, "mods", "Installed"));
        Directory.CreateDirectory(Path.Combine(_root, "mods", "New external mod"));
        Directory.CreateDirectory(Path.Combine(_root, "mods", ".git"));

        var result = Mo2ProfileManager.ReconcileProfile(_root, "Anthology Стандарт");

        Assert.True(result.Changed);
        Assert.Equal(["Missing enabled", "Existing disabled", ".git"], result.RemovedMissingMods);
        Assert.Equal(["New external mod"], result.AddedDisabledMods);
        Assert.True(File.Exists(result.RecoveryPath));
        var lines = File.ReadAllLines(path);
        Assert.Contains("-New external mod", lines);
        Assert.Contains("*Unmanaged game data", lines);
        Assert.Contains("+Installed", lines);
        Assert.DoesNotContain("+Missing enabled", lines);
        Assert.DoesNotContain("-Existing disabled", lines);
        Assert.DoesNotContain("-.git", lines);
        Assert.Equal(["Installed", "Unmanaged game data", "New external mod"], result.Profile.Mods.Select(mod => mod.Name));
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
    public void RebaseGamePathsUpdatesPortableAnomalyExecutablesAndCreatesBackup()
    {
        CreateInstance();
        var gameRoot = Path.Combine(_root, "A.N.T.H.O.L.O.G.Y");
        var gameBin = Path.Combine(gameRoot, "bin");
        Directory.CreateDirectory(gameBin);
        File.WriteAllText(Path.Combine(gameBin, "AnomalyDX11AVX.exe"), string.Empty);

        Mo2ProfileManager.RebaseGamePaths(_root, gameRoot);

        var snapshot = Mo2ProfileManager.Detect(_root);
        Assert.Equal(Path.GetFullPath(gameRoot), snapshot.GamePath);
        var executable = Assert.Single(snapshot.Executables);
        Assert.Equal(Path.Combine(gameBin, "AnomalyDX11AVX.exe"), executable.Binary);
        Assert.Equal(gameBin, executable.WorkingDirectory);
        Assert.True(File.Exists(Path.Combine(_root, "ModOrganizer.ini.anthology-backup")));
    }

    [Fact]
    public void MissingPortableConfigurationIsCreatedForDetectedGameAndProfile()
    {
        var profileName = "Anthology Portable";
        var profile = Path.Combine(_root, "profiles", profileName);
        var gameRoot = Path.Combine(_root, "game");
        var gameBin = Path.Combine(gameRoot, "bin");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(gameBin);
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "+First\n");
        File.WriteAllText(Path.Combine(_root, "ModOrganizer.exe"), string.Empty);
        File.WriteAllText(Path.Combine(gameBin, "AnomalyDX11AVX.exe"), string.Empty);
        File.WriteAllText(Path.Combine(gameBin, "AnomalyDX9.exe"), string.Empty);

        var created = Mo2ProfileManager.EnsurePortableConfiguration(_root, gameRoot, profileName);

        Assert.True(created);
        var snapshot = Mo2ProfileManager.Detect(_root);
        Assert.True(snapshot.Available);
        Assert.Equal(profileName, snapshot.SelectedProfile);
        Assert.Equal(Path.GetFullPath(gameRoot), snapshot.GamePath);
        Assert.Collection(
            snapshot.Executables,
            executable =>
            {
                Assert.Equal("Anomaly (DX11-AVX)", executable.Title);
                Assert.Equal(Path.Combine(gameBin, "AnomalyDX11AVX.exe"), executable.Binary);
                Assert.Equal(gameBin, executable.WorkingDirectory);
            },
            executable => Assert.Equal("Anomaly (DX9)", executable.Title));
    }

    [Fact]
    public void ExistingPortableConfigurationIsPreservedAndRebased()
    {
        CreateInstance();
        var gameRoot = Path.Combine(_root, "relocated-game");
        var gameBin = Path.Combine(gameRoot, "bin");
        Directory.CreateDirectory(gameBin);
        File.WriteAllText(Path.Combine(gameBin, "AnomalyDX11AVX.exe"), string.Empty);
        var iniPath = Path.Combine(_root, "ModOrganizer.ini");
        var iniLines = File.ReadAllLines(iniPath).ToList();
        iniLines.Insert(1, "gameName=STALKER Anomaly");
        File.WriteAllLines(iniPath, iniLines);
        Mo2ProfileManager.RebaseGamePaths(_root, gameRoot);

        var created = Mo2ProfileManager.EnsurePortableConfiguration(
            _root,
            gameRoot,
            "Anthology Стандарт");

        Assert.False(created);
        var snapshot = Mo2ProfileManager.Detect(_root);
        Assert.Equal(Path.GetFullPath(gameRoot), snapshot.GamePath);
        Assert.Equal("Anthology Стандарт", snapshot.SelectedProfile);
        Assert.Equal(Path.Combine(gameBin, "AnomalyDX11AVX.exe"), Assert.Single(snapshot.Executables).Binary);
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
        Assert.Equal(["Low", "High"], search.Providers);
        Assert.Equal(2, index.Search(string.Empty).Count);
    }

    [Fact]
    public void ReadSavesReturnsOnlyAnomalySavesWithNewestFirst()
    {
        var saves = Path.Combine(_root, "game", "appdata", "savedgames");
        Directory.CreateDirectory(saves);
        var older = Path.Combine(saves, "older.scop");
        var newestPersistent = Path.Combine(saves, "newest.scop");
        var newest = Path.Combine(saves, "newest.scoc");
        File.WriteAllText(older, "older");
        File.WriteAllText(newestPersistent, "persistent");
        File.WriteAllText(newest, "newest");
        File.WriteAllText(Path.Combine(saves, "newest.dds"), "preview");
        File.WriteAllText(Path.Combine(saves, "notes.txt"), "not a save");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(newestPersistent, DateTime.UtcNow.AddMinutes(-1));
        File.SetLastWriteTimeUtc(newest, DateTime.UtcNow.AddMinutes(-1));

        var result = Mo2WorkspaceReader.ReadSaves(Path.Combine(_root, "game"));

        Assert.Equal(["newest.scop", "older.scop"], result.Select(save => save.Name));
        Assert.True(result[0].HasScop);
        Assert.True(result[0].HasScoc);
        Assert.EndsWith("newest.dds", result[0].PreviewPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new FileInfo(newestPersistent).Length + new FileInfo(newest).Length, result[0].Size);
    }

    [Fact]
    public void DdsPreviewDecoderDecodesDxt1Pixels()
    {
        var data = new byte[136];
        "DDS "u8.CopyTo(data);
        BitConverter.GetBytes(124).CopyTo(data, 4);
        BitConverter.GetBytes(4).CopyTo(data, 12);
        BitConverter.GetBytes(4).CopyTo(data, 16);
        "DXT1"u8.CopyTo(data.AsSpan(84));
        data[128] = 0x00;
        data[129] = 0xf8;
        data[130] = 0xe0;
        data[131] = 0x07;

        var decoded = DdsPreviewDecoder.DecodeDxt1(data);

        Assert.Equal(4, decoded.Width);
        Assert.Equal(4, decoded.Height);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, decoded.Bgra32[..4]);
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
