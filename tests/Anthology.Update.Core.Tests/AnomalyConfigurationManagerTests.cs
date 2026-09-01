using Anthology.Mo2.Core;

namespace Anthology.Update.Core.Tests;

public sealed class AnomalyConfigurationManagerTests
{
    [Fact]
    public void LoadReadsLiveUserLtxAndPrefersMo2OverwriteMcm()
    {
        using var environment = TestEnvironment.Create();
        environment.WriteGame("appdata/user.ltx", "r2_sun on\r\nsnd_volume_eff 0.75\r\ng_game_difficulty gd_veteran\r\n");
        environment.WriteGame("gamedata/configs/axr_options.ltx", "[mcm]\r\nbase/value = false\r\n");
        environment.WriteMo2("overwrite/gamedata/configs/axr_options.ltx", "[mcm]\r\naim_stamina/enabled = true\r\naim_stamina/drain = 0.5\r\n");

        var snapshot = AnomalyConfigurationManager.Load(environment.GameRoot, environment.Mo2Root);

        Assert.True(snapshot.AnomalyAvailable);
        Assert.True(snapshot.McmAvailable);
        Assert.Equal(3, snapshot.AnomalySettings.Count);
        Assert.Equal(2, snapshot.McmSettings.Count);
        Assert.Equal("Графика", snapshot.AnomalySettings[0].Category);
        Assert.Equal("aim_stamina", snapshot.McmSettings[0].Category);
        Assert.Contains(Path.Combine("overwrite", "gamedata", "configs", "axr_options.ltx"), snapshot.McmPath);
    }

    [Fact]
    public void SaveChangesOnlySelectedValuesAndCreatesBackups()
    {
        using var environment = TestEnvironment.Create();
        var originalUser = "; keep this comment\r\nr2_sun on\r\nsnd_volume_eff 0.75\r\n";
        var originalMcm = "[options]\r\nuntouched = 1\r\n\r\n[mcm]\r\naim_stamina/enabled = true\r\naim_stamina/drain = 0.5\r\n";
        environment.WriteGame("appdata/user.ltx", originalUser);
        environment.WriteMo2("overwrite/gamedata/configs/axr_options.ltx", originalMcm);
        var snapshot = AnomalyConfigurationManager.Load(environment.GameRoot, environment.Mo2Root);
        snapshot.AnomalySettings.Single(item => item.Key == "r2_sun").Value = "off";
        snapshot.McmSettings.Single(item => item.Key == "aim_stamina/drain").Value = "0.8";

        var result = AnomalyConfigurationManager.Save(snapshot);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, result.ChangedValues);
        Assert.Equal(2, result.BackupPaths?.Count);
        Assert.All(result.BackupPaths!, path => Assert.True(File.Exists(path)));
        Assert.Equal("; keep this comment\r\nr2_sun off\r\nsnd_volume_eff 0.75\r\n", File.ReadAllText(snapshot.UserLtxPath!));
        Assert.Equal("[options]\r\nuntouched = 1\r\n\r\n[mcm]\r\naim_stamina/enabled = true\r\naim_stamina/drain = 0.8\r\n", File.ReadAllText(snapshot.McmPath!));
        Assert.Equal(0, snapshot.DirtyCount);
    }

    [Fact]
    public void SaveCreatesMo2OverrideFromGameFallbackAndCanRestoreIt()
    {
        using var environment = TestEnvironment.Create();
        environment.WriteGame("appdata/user.ltx", "r2_sun on\r\n");
        environment.WriteGame("gamedata/configs/axr_options.ltx", "[mcm]\r\nmodule/enabled = true\r\n");
        Directory.CreateDirectory(environment.Mo2Root);
        var snapshot = AnomalyConfigurationManager.Load(environment.GameRoot, environment.Mo2Root);
        snapshot.McmSettings.Single().Value = "false";

        var saved = AnomalyConfigurationManager.Save(snapshot);

        Assert.True(saved.Success, saved.Message);
        Assert.True(File.Exists(snapshot.McmPath));
        Assert.Contains("module/enabled = false", File.ReadAllText(snapshot.McmPath!));
        Assert.Contains("module/enabled = true", File.ReadAllText(Path.Combine(environment.GameRoot, "gamedata", "configs", "axr_options.ltx")));

        var restored = AnomalyConfigurationManager.RestoreLatestBackup(snapshot.McmPath);

        Assert.True(restored.Success, restored.Message);
        Assert.Contains("module/enabled = true", File.ReadAllText(snapshot.McmPath!));
    }

    private sealed class TestEnvironment : IDisposable
    {
        private TestEnvironment(string root)
        {
            Root = root;
            GameRoot = Path.Combine(root, "game");
            Mo2Root = Path.Combine(root, "mo2");
            Directory.CreateDirectory(GameRoot);
        }

        public string Root { get; }

        public string GameRoot { get; }

        public string Mo2Root { get; }

        public static TestEnvironment Create() => new(Path.Combine(
            Path.GetTempPath(),
            "AnthologyConfigurationTests",
            Guid.NewGuid().ToString("N")));

        public void WriteGame(string relativePath, string contents) => Write(GameRoot, relativePath, contents);

        public void WriteMo2(string relativePath, string contents) => Write(Mo2Root, relativePath, contents);

        private static void Write(string root, string relativePath, string contents)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
