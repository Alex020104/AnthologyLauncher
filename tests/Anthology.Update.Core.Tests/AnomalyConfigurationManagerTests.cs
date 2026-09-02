using Anthology.Mo2.Core;

namespace Anthology.Update.Core.Tests;

public sealed class AnomalyConfigurationManagerTests
{
    [Fact]
    public void LoadReadsOriginalAnomalyAndMcmSectionsFromGameAxrOptions()
    {
        using var environment = TestEnvironment.Create();
        environment.WriteGame("appdata/user.ltx", "r2_sun legacy-value\r\n");
        environment.WriteGame(
            "gamedata/configs/axr_options.ltx",
            "[options]\r\nvideo/basic/r2_sun = on\r\nsound/general/snd_volume_eff = 0.75\r\ngameplay/general/g_game_difficulty = gd_veteran\r\n\r\n[mcm]\r\nbase/value = false\r\n");
        environment.WriteMo2("overwrite/gamedata/configs/axr_options.ltx", "[mcm]\r\naim_stamina/enabled = true\r\naim_stamina/drain = 0.5\r\n");

        var snapshot = AnomalyConfigurationManager.Load(environment.GameRoot, environment.Mo2Root);

        Assert.True(snapshot.AnomalyAvailable);
        Assert.True(snapshot.McmAvailable);
        Assert.Equal(3, snapshot.AnomalySettings.Count);
        Assert.Single(snapshot.McmSettings);
        Assert.Equal("video", snapshot.AnomalySettings[0].Category);
        Assert.Equal("video/basic", snapshot.AnomalySettings[0].MenuPath);
        Assert.Equal("base", snapshot.McmSettings[0].Category);
        Assert.Equal(snapshot.AnomalyPath, snapshot.McmPath);
        Assert.Equal(Path.Combine(environment.GameRoot, "gamedata", "configs", "axr_options.ltx"), snapshot.McmPath);
    }

    [Fact]
    public void SaveChangesOnlySelectedValuesAndCreatesBackups()
    {
        using var environment = TestEnvironment.Create();
        var originalUser = "; keep this legacy file\r\nr2_sun legacy\r\n";
        var originalMcm = "[options]\r\nvideo/basic/r2_sun = on\r\nuntouched = 1\r\n\r\n[mcm]\r\naim_stamina/enabled = true\r\naim_stamina/drain = 0.5\r\n";
        environment.WriteGame("appdata/user.ltx", originalUser);
        environment.WriteGame("gamedata/configs/axr_options.ltx", originalMcm);
        environment.WriteMo2("overwrite/gamedata/configs/axr_options.ltx", "[mcm]\r\naim_stamina/drain = 99\r\n");
        var snapshot = AnomalyConfigurationManager.Load(environment.GameRoot, environment.Mo2Root);
        snapshot.AnomalySettings.Single(item => item.Key == "video/basic/r2_sun").Value = "off";
        snapshot.McmSettings.Single(item => item.Key == "aim_stamina/drain").Value = "0.8";

        var result = AnomalyConfigurationManager.Save(snapshot);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, result.ChangedValues);
        Assert.Single(result.BackupPaths!);
        Assert.All(result.BackupPaths!, path => Assert.True(File.Exists(path)));
        Assert.Equal(originalUser, File.ReadAllText(Path.Combine(environment.GameRoot, "appdata", "user.ltx")));
        Assert.Equal("[options]\r\nvideo/basic/r2_sun = off\r\nuntouched = 1\r\n\r\n[mcm]\r\naim_stamina/enabled = true\r\naim_stamina/drain = 0.8\r\n", File.ReadAllText(snapshot.AnomalyPath!));
        Assert.Contains("aim_stamina/drain = 99", File.ReadAllText(Path.Combine(environment.Mo2Root, "overwrite", "gamedata", "configs", "axr_options.ltx")));
        Assert.Equal(0, snapshot.DirtyCount);
    }

    [Fact]
    public void LoadAndSaveUseOriginalUserLtxWithoutDuplicatingAxrOptions()
    {
        using var environment = TestEnvironment.Create();
        environment.WriteGame(
            "appdata/user.ltx",
            "r2_sun legacy-value\r\nvid_mode 1920x1080\r\nbind jump kSPACE\r\n");
        environment.WriteGame(
            "gamedata/configs/axr_options.ltx",
            "[options]\r\nvideo/basic/r2_sun = on\r\n");

        var snapshot = AnomalyConfigurationManager.Load(environment.GameRoot, environment.Mo2Root);

        Assert.Equal(3, snapshot.AnomalySettings.Count);
        Assert.Single(snapshot.AnomalySettings, item => item.Key.EndsWith("r2_sun", StringComparison.OrdinalIgnoreCase));
        var videoMode = Assert.Single(snapshot.AnomalySettings, item => item.Key == "vid_mode");
        var jump = Assert.Single(snapshot.AnomalySettings, item => item.Key == "bind/jump");
        Assert.Equal("video/basic", videoMode.MenuPath);
        Assert.Equal("control/keybind", jump.MenuPath);
        Assert.Equal(AnomalyConfigurationStorageFormat.ConsoleCommand, videoMode.StorageFormat);

        videoMode.Value = "2560x1440";
        jump.Value = "kJ";
        snapshot.AnomalySettings.Single(item => item.Key.EndsWith("r2_sun", StringComparison.OrdinalIgnoreCase)).Value = "off";
        var result = AnomalyConfigurationManager.Save(snapshot);

        Assert.True(result.Success, result.Message);
        Assert.Equal(3, result.ChangedValues);
        Assert.Equal(2, result.BackupPaths!.Count);
        Assert.Equal(
            "r2_sun legacy-value\r\nvid_mode 2560x1440\r\nbind jump kJ\r\n",
            File.ReadAllText(Path.Combine(environment.GameRoot, "appdata", "user.ltx")));
        Assert.Contains("video/basic/r2_sun = off", File.ReadAllText(snapshot.AnomalyPath!));
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
            "function on_mcm_load() op = { id='exo', sh=true, gr={ { id='title', type='slide', text='ui_mcm_exo_title', size={512,50} }, { id='drain', type='track', min=0.1, max=3, step=0.1, def=1.5 } } } return op end");
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
        Assert.Equal("1.5", entry.DefaultValue);
    }

    [Fact]
    public void LoadUsesExactAnomalyMenuDefinitionFromUiOptionsScript()
    {
        using var environment = TestEnvironment.Create();
        environment.WriteGame("appdata/user.ltx", "vid_mode 1920x1080\r\nrs_c_gamma 1.0\r\n");
        environment.WriteGame("gamedata/configs/axr_options.ltx", "[options]\r\n");
        environment.WriteGame(
            "gamedata/scripts/ui_options.script",
            """
            options = {}
            function init_opt_base()
            options = {
                { id="video", gr={
                    { id="basic", sh=true, gr={
                        { id="slide", type="slide", text="ui_mm_title_video_basic" },
                        { id="resolution", type="list", cmd="vid_mode" },
                        { id="gamma", type="track", cmd="rs_c_gamma", min=0.5, max=1.5, step=0.1 }
                    },},
                },},
            }
            end
            """);
        environment.WriteGame(
            "gamedata/configs/text/rus/ui_options.xml",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><string_table>"
            + "<string id=\"ui_mm_menu_video\"><text>Видео</text></string>"
            + "<string id=\"ui_mm_title_video_basic\"><text>Основные настройки видео</text></string>"
            + "<string id=\"ui_mm_video_basic_resolution\"><text>Разрешение экрана</text></string>"
            + "<string id=\"ui_mm_video_basic_gamma\"><text>Гамма</text></string>"
            + "</string_table>");

        var snapshot = AnomalyConfigurationManager.Load(environment.GameRoot, environment.Mo2Root);
        var resolution = Assert.Single(snapshot.AnomalySettings, item => item.Key == "vid_mode");
        var gamma = Assert.Single(snapshot.AnomalySettings, item => item.Key == "rs_c_gamma");

        Assert.Equal("video/basic", resolution.MenuPath);
        Assert.Equal("Разрешение экрана", resolution.DisplayName);
        Assert.Equal("Основные настройки видео", resolution.MenuDisplayName);
        Assert.Equal("list", resolution.ControlType);
        Assert.Equal("video/basic", gamma.MenuPath);
        Assert.Equal("Гамма", gamma.DisplayName);
        Assert.Equal("track", gamma.ControlType);
        Assert.Equal(0.5, gamma.Minimum);
        Assert.Equal(1.5, gamma.Maximum);
        Assert.Equal(0.1, gamma.Step);
    }

    [Fact]
    public void LoadUsesMcmHintBeforeGeneratedOrTextLabels()
    {
        using var environment = TestEnvironment.Create();
        environment.WriteGame("appdata/user.ltx", "r2_sun on\r\n");
        environment.WriteGame(
            "gamedata/configs/axr_options.ltx",
            "[mcm]\r\ntest_module/raw_option = true\r\n");
        environment.WriteMo2(
            "mods/Test/gamedata/scripts/test_module_mcm.script",
            "local op={id='test_module',gr={{id='raw_option',type='check',text='wrong_label',hint='friendly_option'}}} return op");
        environment.WriteMo2(
            "mods/Test/gamedata/configs/text/rus/test.xml",
            "<string_table>"
            + "<string id=\"wrong_label\"><text>Неверная подпись</text></string>"
            + "<string id=\"friendly_option\"><text>Неверная подпись без префикса</text></string>"
            + "<string id=\"friendly_option_desc\"><text>Неверное описание без префикса</text></string>"
            + "<string id=\"ui_mcm_friendly_option\"><text>Понятная подпись</text></string>"
            + "<string id=\"ui_mcm_friendly_option_desc\"><text>Понятное описание параметра.</text></string>"
            + "</string_table>");

        var entry = Assert.Single(AnomalyConfigurationManager.Load(environment.GameRoot, environment.Mo2Root).McmSettings);

        Assert.Equal("Понятная подпись", entry.DisplayName);
        Assert.Equal("Понятное описание параметра.", entry.Description);
        Assert.Equal("check", entry.ControlType);
    }

    [Fact]
    public void LoadAcceptsStringTableWithCommentBeforeXmlDeclaration()
    {
        using var environment = TestEnvironment.Create();
        environment.WriteGame("appdata/user.ltx", "r2_sun on\r\n");
        environment.WriteGame(
            "gamedata/configs/axr_options.ltx",
            "[mcm]\r\ncommented/enabled = true\r\n");
        environment.WriteMo2(
            "mods/Commented/gamedata/scripts/commented_mcm.script",
            "local op={id='commented',gr={{id='enabled',type='check',hint='commented_enabled'}}} return op");
        environment.WriteMo2(
            "mods/Commented/gamedata/configs/text/rus/commented.xml",
            "<!-- Addon localization -->\r\n<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
            + "<string_table><string id=\"ui_mcm_commented_enabled\"><text>Включить дополнение</text></string></string_table>");

        var entry = Assert.Single(AnomalyConfigurationManager.Load(environment.GameRoot, environment.Mo2Root).McmSettings);

        Assert.Equal("Включить дополнение", entry.DisplayName);
    }

    [Fact]
    public void LoadDecodesQtByteArrayProfileAndUsesMo2PriorityOrder()
    {
        using var environment = TestEnvironment.Create();
        environment.WriteGame("appdata/user.ltx", "r2_sun on\r\n");
        environment.WriteGame(
            "gamedata/configs/axr_options.ltx",
            "[mcm]\r\npriority/value = true\r\n");
        environment.WriteMo2(
            "ModOrganizer.ini",
            "selected_profile=@ByteArray(\\xd0\\x9f\\xd1\\x80\\xd0\\xbe\\xd1\\x84\\xd0\\xb8\\xd0\\xbb\\xd1\\x8c)\r\n");
        environment.WriteMo2("profiles/Профиль/modlist.txt", "+02 High\r\n+01 Low\r\n-99 Disabled\r\n");
        foreach (var (mod, label) in new[]
                 {
                     ("01 Low", "Низкий приоритет"),
                     ("02 High", "Высокий приоритет"),
                     ("99 Disabled", "Отключённый мод"),
                 })
        {
            environment.WriteMo2(
                $"mods/{mod}/gamedata/scripts/priority_mcm.script",
                "local op={id='priority',gr={{id='value',type='check',hint='priority_value'}}} return op");
            environment.WriteMo2(
                $"mods/{mod}/gamedata/configs/text/rus/priority.xml",
                $"<string_table><string id=\"ui_mcm_priority_value\"><text>{label}</text></string></string_table>");
        }

        var entry = Assert.Single(AnomalyConfigurationManager.Load(environment.GameRoot, environment.Mo2Root).McmSettings);

        Assert.Equal("Высокий приоритет", entry.DisplayName);
    }

    [Fact]
    public void LoadUsesReturnedModuleAndRootIdAsNestedMcmPath()
    {
        using var environment = TestEnvironment.Create();
        environment.WriteGame("appdata/user.ltx", "r2_sun on\r\n");
        environment.WriteGame(
            "gamedata/configs/axr_options.ltx",
            "[mcm]\r\nssfx_module/ao/intensity = 0.5\r\n");
        environment.WriteMo2(
            "mods/SSFX/gamedata/scripts/ssfx_ao_mcm.script",
            "local op={id='ao',gr={{id='title',type='slide',text='ssfx_ao_title'},{id='intensity',type='track',hint='ssfx_ao_intensity',min=0,max=1,step=0.1}}} return op, 'ssfx_module'");
        environment.WriteMo2(
            "mods/SSFX/gamedata/configs/text/rus/ssfx.xml",
            "<string_table>"
            + "<string id=\"ssfx_ao_title\"><text>Затенение окружения</text></string>"
            + "<string id=\"ui_mcm_ssfx_ao_intensity\"><text>Интенсивность эффекта</text></string>"
            + "</string_table>");

        var entry = Assert.Single(AnomalyConfigurationManager.Load(environment.GameRoot, environment.Mo2Root).McmSettings);

        Assert.Equal("ssfx_module", entry.Category);
        Assert.Equal("ssfx_module/ao", entry.MenuPath);
        Assert.Equal("Затенение окружения", entry.MenuDisplayName);
        Assert.Equal("Интенсивность эффекта", entry.DisplayName);
    }

    [Fact]
    public void LoadResolvesVariableModuleAndStringFormatHint()
    {
        using var environment = TestEnvironment.Create();
        environment.WriteGame("appdata/user.ltx", "r2_sun on\r\n");
        environment.WriteGame(
            "gamedata/configs/axr_options.ltx",
            "[mcm]\r\ndii/scale = 1.1\r\n");
        environment.WriteMo2(
            "mods/DII/gamedata/scripts/icon_overlayer_mcm.script",
            "local mcm_id='dii'\r\nlocal op={id=mcm_id,gr={{id='scale',type='track',hint=string_format('%s_scale',mcm_id),min=0.5,max=2,step=0.1}}} return op");
        environment.WriteMo2(
            "mods/DII/gamedata/configs/text/rus/dii.xml",
            "<string_table><string id=\"ui_mcm_dii_scale\"><text>Масштаб значков</text></string></string_table>");

        var entry = Assert.Single(AnomalyConfigurationManager.Load(environment.GameRoot, environment.Mo2Root).McmSettings);

        Assert.Equal("dii", entry.Category);
        Assert.Equal("Масштаб значков", entry.DisplayName);
        Assert.Equal(0.5, entry.Minimum);
        Assert.Equal(2, entry.Maximum);
    }

    [Fact]
    public void LoadReadsDynamicMcmSchemaDefinitionsAndPanelPaths()
    {
        using var environment = TestEnvironment.Create();
        environment.WriteGame("appdata/user.ltx", "r2_sun on\r\n");
        environment.WriteGame(
            "gamedata/configs/axr_options.ltx",
            "[mcm]\r\nzhopa2/tasks/explore_enabled = true\r\nzhopa2/tasks/task_weight = 40\r\n");
        environment.WriteMo2(
            "mods/Zhopa/gamedata/scripts/zhopa2_mcm_schema.script",
            """
            local OPTION_DEFS = {
                explore_enabled = { type = "check", val = 1, def = true },
                task_weight = { type = "track", val = 2, min = 0, max = 100, step = 1, def = 40 },
            }
            local PANELS = {
                { id = "tasks", text = "ui_mcm_zhopa2_panel_tasks", ["groups"] = {
                    { id = "roam", text = "ui_mcm_zhopa2_group_roam", options = { "explore_enabled", "task_weight" } },
                },},
            }
            """);
        environment.WriteMo2(
            "mods/Zhopa/gamedata/configs/text/rus/zhopa2.xml",
            "<string_table>"
            + "<string id=\"ui_mcm_zhopa2_title\"><text>Z.H.O.P.A. ALIFE 2</text></string>"
            + "<string id=\"ui_mcm_zhopa2_panel_tasks\"><text>Задачи сталкеров</text></string>"
            + "<string id=\"ui_mcm_zhopa2_tasks_explore_enabled\"><text>Разрешить исследование</text></string>"
            + "<string id=\"ui_mcm_zhopa2_tasks_task_weight\"><text>Вес задачи</text></string>"
            + "</string_table>");

        var settings = AnomalyConfigurationManager.Load(environment.GameRoot, environment.Mo2Root).McmSettings;
        var enabled = Assert.Single(settings, item => item.Key == "zhopa2/tasks/explore_enabled");
        var weight = Assert.Single(settings, item => item.Key == "zhopa2/tasks/task_weight");

        Assert.Equal("Z.H.O.P.A. ALIFE 2", enabled.CategoryDisplayName);
        Assert.Equal("Задачи сталкеров", enabled.MenuDisplayName);
        Assert.Equal("Разрешить исследование", enabled.DisplayName);
        Assert.Equal("check", enabled.ControlType);
        Assert.Equal("track", weight.ControlType);
        Assert.Equal(0, weight.Minimum);
        Assert.Equal(100, weight.Maximum);
        Assert.Equal(1, weight.Step);
        Assert.Equal("40", weight.DefaultValue);
    }

    [Fact]
    public void LoadDoesNotBorrowAmbiguousLeafMetadataFromAnotherMcmSubmenu()
    {
        using var environment = TestEnvironment.Create();
        environment.WriteGame("appdata/user.ltx", "r2_sun on\r\n");
        environment.WriteGame(
            "gamedata/configs/axr_options.ltx",
            "[mcm]\r\nambiguous/unknown/enabled = true\r\n");
        environment.WriteMo2(
            "mods/Ambiguous/gamedata/scripts/ambiguous_mcm.script",
            """
            local op={id='ambiguous',gr={
              {id='first',gr={{id='enabled',type='track',hint='first_enabled',min=0,max=10}}},
              {id='second',gr={{id='enabled',type='list',hint='second_enabled'}}}
            }} return op
            """);
        environment.WriteMo2(
            "mods/Ambiguous/gamedata/configs/text/rus/ambiguous.xml",
            "<string_table>"
            + "<string id=\"ui_mcm_first_enabled\"><text>Чужой первый параметр</text></string>"
            + "<string id=\"ui_mcm_second_enabled\"><text>Чужой второй параметр</text></string>"
            + "</string_table>");

        var entry = Assert.Single(AnomalyConfigurationManager.Load(environment.GameRoot, environment.Mo2Root).McmSettings);

        Assert.Equal("Включено", entry.DisplayName);
        Assert.Null(entry.ControlType);
        Assert.Null(entry.Minimum);
        Assert.Null(entry.Maximum);
    }

    [Fact]
    public void LoadUsesUniqueLeafMetadataWhenOnlyOneSubmenuDefinesIt()
    {
        using var environment = TestEnvironment.Create();
        environment.WriteGame("appdata/user.ltx", "r2_sun on\r\n");
        environment.WriteGame(
            "gamedata/configs/axr_options.ltx",
            "[mcm]\r\nunique/renamed/strength = 3\r\n");
        environment.WriteMo2(
            "mods/Unique/gamedata/scripts/unique_mcm.script",
            "local op={id='unique',gr={{id='actual',gr={{id='strength',type='track',hint='unique_strength',min=1,max=5,step=1}}}}} return op");
        environment.WriteMo2(
            "mods/Unique/gamedata/configs/text/rus/unique.xml",
            "<string_table><string id=\"ui_mcm_unique_strength\"><text>Сила эффекта</text></string></string_table>");

        var entry = Assert.Single(AnomalyConfigurationManager.Load(environment.GameRoot, environment.Mo2Root).McmSettings);

        Assert.Equal("Сила эффекта", entry.DisplayName);
        Assert.Equal("track", entry.ControlType);
        Assert.Equal(1, entry.Minimum);
        Assert.Equal(5, entry.Maximum);
    }

    [Fact]
    public void LoadRecoversCompleteStringsFromMalformedXrayLocalizationAndUsesDirectLevelId()
    {
        using var environment = TestEnvironment.Create();
        environment.WriteGame("appdata/user.ltx", "r2_sun on\r\n");
        environment.WriteGame(
            "gamedata/configs/axr_options.ltx",
            "[options]\r\nalife/warfare/army/lvl_l99_testzone_priority = 1\r\n");
        environment.WriteGame(
            "gamedata/configs/text/rus/st_levels.xml",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <string_table>
              <! -- malformed decorative pseudo-comment -- >
              <string id="unrelated"><text>Соседняя строка</text></string>
              <string id="l99_testzone"><text>Испытательный полигон</text></string>
            </string_table>
            """);

        var entry = Assert.Single(
            AnomalyConfigurationManager.Load(environment.GameRoot, environment.Mo2Root).AnomalySettings,
            item => item.Key.EndsWith("lvl_l99_testzone_priority", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("Приоритет локации «Испытательный полигон»", entry.DisplayName);
    }

    [Fact]
    public void LoadDoesNotScanDisabledModsWhenSelectedProfileIsBroken()
    {
        using var environment = TestEnvironment.Create();
        environment.WriteGame("appdata/user.ltx", "r2_sun on\r\n");
        environment.WriteGame("gamedata/configs/axr_options.ltx", "[mcm]\r\nbroken/value = true\r\n");
        environment.WriteMo2("ModOrganizer.ini", "selected_profile=Missing profile\r\n");
        environment.WriteMo2(
            "mods/Disabled/gamedata/scripts/broken_mcm.script",
            "local op={id='broken',gr={{id='value',type='check',hint='disabled_value'}}} return op");
        environment.WriteMo2(
            "mods/Disabled/gamedata/configs/text/rus/disabled.xml",
            "<string_table><string id=\"ui_mcm_disabled_value\"><text>Текст отключённого мода</text></string></string_table>");

        var entry = Assert.Single(AnomalyConfigurationManager.Load(environment.GameRoot, environment.Mo2Root).McmSettings);

        Assert.NotEqual("Текст отключённого мода", entry.DisplayName);
        Assert.Null(entry.ControlType);
    }

    [Fact]
    public void LoadSafelyMatchesNormalizedProfileAndModNamesAndRejectsTraversal()
    {
        using var environment = TestEnvironment.Create();
        environment.WriteGame("appdata/user.ltx", "r2_sun on\r\n");
        environment.WriteGame("gamedata/configs/axr_options.ltx", "[mcm]\r\nsafe/value = true\r\n");
        environment.WriteMo2("ModOrganizer.ini", "selected_profile=Test Profile\r\n");
        environment.WriteMo2("profiles/Test   Profile/modlist.txt", "+Actual Mod-Name\r\n+../Outside\r\n");
        environment.WriteMo2(
            "mods/Actual   Mod—Name/gamedata/scripts/safe_mcm.script",
            "local op={id='safe',gr={{id='value',type='check',hint='safe_value'}}} return op");
        environment.WriteMo2(
            "mods/Actual   Mod—Name/gamedata/configs/text/rus/safe.xml",
            "<string_table><string id=\"ui_mcm_safe_value\"><text>Безопасно найденный мод</text></string></string_table>");
        environment.WriteMo2(
            "Outside/gamedata/scripts/safe_mcm.script",
            "local op={id='safe',gr={{id='value',type='track',hint='outside_value',min=0,max=99}}} return op");
        environment.WriteMo2(
            "Outside/gamedata/configs/text/rus/outside.xml",
            "<string_table><string id=\"ui_mcm_outside_value\"><text>Выход за каталог</text></string></string_table>");

        var entry = Assert.Single(AnomalyConfigurationManager.Load(environment.GameRoot, environment.Mo2Root).McmSettings);

        Assert.Equal("Безопасно найденный мод", entry.DisplayName);
        Assert.Equal("check", entry.ControlType);
    }

    [Fact]
    public void LoadUsesExactMetadataMenuPathForUserLtxCommand()
    {
        using var environment = TestEnvironment.Create();
        environment.WriteGame("appdata/user.ltx", "custom_console 1\r\n");
        environment.WriteGame("gamedata/configs/axr_options.ltx", "[options]\r\n");
        environment.WriteGame(
            "gamedata/scripts/ui_options.script",
            """
            options={{id="video",gr={{id="advanced",gr={
              {id="custom",type="track",cmd="custom_console",text="custom_console_title",min=0,max=2,step=1}
            }}}}}
            """);
        environment.WriteGame(
            "gamedata/configs/text/rus/custom.xml",
            "<string_table><string id=\"custom_console_title\"><text>Особый параметр</text></string></string_table>");

        var entry = Assert.Single(AnomalyConfigurationManager.Load(environment.GameRoot, environment.Mo2Root).AnomalySettings);

        Assert.Equal("video", entry.Category);
        Assert.Equal("video/advanced", entry.MenuPath);
        Assert.Equal("track", entry.ControlType);
    }

    [Fact]
    public void LoadUsesExactMetadataMenuPathForAxrOptionCommand()
    {
        using var environment = TestEnvironment.Create();
        environment.WriteGame("appdata/user.ltx", string.Empty);
        environment.WriteGame(
            "gamedata/configs/axr_options.ltx",
            "[options]\r\nlegacy/custom_console = 1\r\n");
        environment.WriteGame(
            "gamedata/scripts/ui_options.script",
            "options={{id='video',gr={{id='advanced',gr={{id='custom',type='track',cmd='custom_console',min=0,max=2,step=1}}}}}}");

        var entry = Assert.Single(AnomalyConfigurationManager.Load(environment.GameRoot, environment.Mo2Root).AnomalySettings);

        Assert.Equal("video", entry.Category);
        Assert.Equal("video/advanced", entry.MenuPath);
        Assert.Equal("track", entry.ControlType);
    }

    [Fact]
    public void LoadPreservesAuthoredEnglishLocalizationForKnownAnomalyCommand()
    {
        using var environment = TestEnvironment.Create();
        environment.WriteGame("appdata/user.ltx", "r2_steep_parallax on\r\n");
        environment.WriteGame("gamedata/configs/axr_options.ltx", "[options]\r\n");
        environment.WriteGame(
            "gamedata/scripts/ui_options.script",
            "options={{id='video',gr={{id='advanced',gr={{id='parallax',type='check',cmd='r2_steep_parallax',text='authored_parallax'}}}}}}");
        environment.WriteGame(
            "gamedata/configs/text/rus/authored.xml",
            "<string_table><string id=\"authored_parallax\"><text>Authored English Label</text></string></string_table>");

        var entry = Assert.Single(AnomalyConfigurationManager.Load(environment.GameRoot, environment.Mo2Root).AnomalySettings);

        Assert.Equal("Authored English Label", entry.DisplayName);
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
