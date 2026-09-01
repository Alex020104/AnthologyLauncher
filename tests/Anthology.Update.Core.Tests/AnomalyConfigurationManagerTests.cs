using Anthology.Mo2.Core;

namespace Anthology.Update.Core.Tests;

public sealed class AnomalyConfigurationManagerTests
{
    [Fact]
    public void LoadReadsLiveUserLtxAndAlwaysUsesOriginalGameMcm()
    {
        using var environment = TestEnvironment.Create();
        environment.WriteGame("appdata/user.ltx", "r2_sun on\r\nsnd_volume_eff 0.75\r\ng_game_difficulty gd_veteran\r\n");
        environment.WriteGame("gamedata/configs/axr_options.ltx", "[mcm]\r\nbase/value = false\r\n");
        environment.WriteMo2("overwrite/gamedata/configs/axr_options.ltx", "[mcm]\r\naim_stamina/enabled = true\r\naim_stamina/drain = 0.5\r\n");

        var snapshot = AnomalyConfigurationManager.Load(environment.GameRoot, environment.Mo2Root);

        Assert.True(snapshot.AnomalyAvailable);
        Assert.True(snapshot.McmAvailable);
        Assert.Equal(3, snapshot.AnomalySettings.Count);
        Assert.Single(snapshot.McmSettings);
        Assert.Equal("Графика", snapshot.AnomalySettings[0].Category);
        Assert.Equal("base", snapshot.McmSettings[0].Category);
        Assert.Equal(Path.Combine(environment.GameRoot, "gamedata", "configs", "axr_options.ltx"), snapshot.McmPath);
    }

    [Fact]
    public void SaveChangesOnlySelectedValuesAndCreatesBackups()
    {
        using var environment = TestEnvironment.Create();
        var originalUser = "; keep this comment\r\nr2_sun on\r\nsnd_volume_eff 0.75\r\n";
        var originalMcm = "[options]\r\nuntouched = 1\r\n\r\n[mcm]\r\naim_stamina/enabled = true\r\naim_stamina/drain = 0.5\r\n";
        environment.WriteGame("appdata/user.ltx", originalUser);
        environment.WriteGame("gamedata/configs/axr_options.ltx", originalMcm);
        environment.WriteMo2("overwrite/gamedata/configs/axr_options.ltx", "[mcm]\r\naim_stamina/drain = 99\r\n");
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
        Assert.Contains("aim_stamina/drain = 99", File.ReadAllText(Path.Combine(environment.Mo2Root, "overwrite", "gamedata", "configs", "axr_options.ltx")));
        Assert.Equal(0, snapshot.DirtyCount);
    }

    [Fact]
    public void SaveAndRestoreOnlyTouchOriginalGameMcm()
    {
        using var environment = TestEnvironment.Create();
        environment.WriteGame("appdata/user.ltx", "r2_sun on\r\n");
        environment.WriteGame("gamedata/configs/axr_options.ltx", "[mcm]\r\nmodule/enabled = true\r\n");
        environment.WriteMo2("overwrite/gamedata/configs/axr_options.ltx", "[mcm]\r\nmodule/enabled = overwrite-value\r\n");
        var snapshot = AnomalyConfigurationManager.Load(environment.GameRoot, environment.Mo2Root);
        snapshot.McmSettings.Single().Value = "false";

        var saved = AnomalyConfigurationManager.Save(snapshot);

        Assert.True(saved.Success, saved.Message);
        Assert.True(File.Exists(snapshot.McmPath));
        Assert.Contains("module/enabled = false", File.ReadAllText(snapshot.McmPath!));
        Assert.Contains("module/enabled = false", File.ReadAllText(Path.Combine(environment.GameRoot, "gamedata", "configs", "axr_options.ltx")));
        Assert.Contains("module/enabled = overwrite-value", File.ReadAllText(Path.Combine(environment.Mo2Root, "overwrite", "gamedata", "configs", "axr_options.ltx")));

        var restored = AnomalyConfigurationManager.RestoreLatestBackup(snapshot.McmPath);

        Assert.True(restored.Success, restored.Message);
        Assert.Contains("module/enabled = true", File.ReadAllText(snapshot.McmPath!));
    }

    [Fact]
    public void LoadUsesInstalledMcmRussianLabelsWithoutChangingStoragePath()
    {
        using var environment = TestEnvironment.Create();
        environment.WriteGame("appdata/user.ltx", "r2_sun on\r\n");
        environment.WriteGame("gamedata/configs/axr_options.ltx", "[mcm]\r\nexo/drain = 1.0\r\n");
        environment.WriteMo2(
            "mods/Exo/gamedata/scripts/exo_mcm.script",
            "function on_mcm_load() op = { id='exo', sh=true, gr={ { id='title', type='slide', text='ui_mcm_exo_title', size={512,50} }, { id='drain', type='track', min=0.1, max=3, step=0.1 } } } return op end");
        environment.WriteMo2(
            "mods/Exo/gamedata/configs/text/rus/st_exo.xml",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><string_table><string id=\"ui_mcm_exo_title\"><text>Экзоскелеты</text></string><string id=\"ui_mcm_exo_drain\"><text>Расход энергии</text></string><string id=\"ui_mcm_exo_drain_desc\"><text>Расход заряда при движении.</text></string></string_table>");

        var snapshot = AnomalyConfigurationManager.Load(environment.GameRoot, environment.Mo2Root);
        var entry = Assert.Single(snapshot.McmSettings);

        Assert.Equal(Path.Combine(environment.GameRoot, "gamedata", "configs", "axr_options.ltx"), entry.TargetPath);
        Assert.Equal("Экзоскелеты", entry.CategoryDisplayName);
        Assert.Equal("Расход энергии", entry.DisplayName);
        Assert.Equal("Расход заряда при движении.", entry.Description);
        Assert.Equal("track", entry.ControlType);
        Assert.Equal(0.1, entry.Minimum);
        Assert.Equal(3, entry.Maximum);
        Assert.Equal(0.1, entry.Step);
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
