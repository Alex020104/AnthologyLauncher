using Anthology.Mo2.Core;

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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
