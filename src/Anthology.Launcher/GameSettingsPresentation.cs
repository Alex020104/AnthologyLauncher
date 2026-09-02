using System.Globalization;
using System.Text.RegularExpressions;
using Anthology.Mo2.Core;

namespace Anthology.Launcher;

public enum GameSettingControl
{
    Toggle,
    Slider,
    Select,
    Number,
    Text,
}

public sealed record GameSettingChoice(string Value, string Label);

public sealed record GameSettingView(
    string Title,
    string Description,
    string CategoryTitle,
    GameSettingControl Control,
    bool IsFriendly,
    double? Minimum = null,
    double? Maximum = null,
    double? Step = null,
    IReadOnlyList<GameSettingChoice>? Choices = null);

public static partial class GameSettingsPresentation
{
    private static readonly IReadOnlyList<GameSettingChoice> DifficultyChoices =
    [
        new("gd_novice", "Новичок"),
        new("gd_stalker", "Сталкер"),
        new("gd_veteran", "Ветеран"),
        new("gd_master", "Мастер"),
    ];

    private static readonly IReadOnlyList<GameSettingChoice> QualityChoices =
    [
        new("st_opt_off", "Выключено"),
        new("st_opt_low", "Низкое"),
        new("st_opt_medium", "Среднее"),
        new("st_opt_high", "Высокое"),
        new("st_opt_ultra", "Максимальное"),
        new("st_opt_extreme", "Экстремальное"),
    ];

    private static readonly Dictionary<string, GameSettingView> Anomaly = new(StringComparer.OrdinalIgnoreCase)
    {
        ["g_game_difficulty"] = Select("Сложность игры", "Влияет на получаемый и наносимый урон.", "Игра", DifficultyChoices),
        ["g_autopickup"] = Toggle("Автоматический подбор", "Подбирать доступные предметы без отдельного подтверждения.", "Игра"),
        ["g_dynamic_music"] = Toggle("Динамическая музыка", "Менять музыку в зависимости от происходящего.", "Игра"),
        ["g_important_save"] = Toggle("Важные сохранения", "Помечать ключевые автоматические сохранения.", "Игра"),
        ["g_crouch_toggle"] = Toggle("Приседание переключателем", "Одно нажатие включает или выключает приседание.", "Игра"),
        ["g_lookout_toggle"] = Toggle("Выглядывание переключателем", "Не требуется удерживать клавишу выглядывания.", "Игра"),
        ["g_freelook_toggle"] = Toggle("Свободный обзор переключателем", "Не требуется удерживать клавишу свободного обзора.", "Игра"),
        ["g_sleep_time"] = Slider("Длительность сна", "Количество часов для стандартного действия сна.", "Игра", 1, 24, 1),
        ["g_hit_pwr_modif"] = Slider("Множитель урона игрока", "Множитель силы попаданий игрока. Стандартное значение — 1.", "Игра", 0.1, 3, 0.1),
        ["vid_mode"] = Text("Разрешение экрана", "Формат: ширина x высота, например 1920x1080.", "Видео"),
        ["renderer"] = Select("Рендер", "Графический API, который использует игра.", "Видео",
        [
            new("renderer_r1", "DirectX 8"), new("renderer_r2a", "DirectX 9"),
            new("renderer_r2.5", "DirectX 9 Enhanced"), new("renderer_r3", "DirectX 10"),
            new("renderer_r4", "DirectX 11"),
        ]),
        ["rs_v_sync"] = Toggle("Вертикальная синхронизация", "Убирает разрывы кадра, но может увеличить задержку управления.", "Видео"),
        ["rs_refresh_60hz"] = Toggle("Принудительно 60 Гц", "Использовать частоту 60 Гц. Обычно оставляйте выключенным.", "Видео"),
        ["rs_vis_distance"] = Slider("Дальность видимости", "Общая дальность отрисовки мира.", "Видео", 0.4, 1.5, 0.05),
        ["r2_sun"] = Toggle("Солнце и динамические тени", "Включает освещение и тени от солнца.", "Видео"),
        ["r2_sun_quality"] = Select("Качество солнечных теней", "Качество теней от солнца.", "Видео", QualityChoices),
        ["r2_ssao"] = Select("Затенение SSAO", "Контактные тени в углах и местах соприкосновения объектов.", "Видео", QualityChoices),
        ["r2_volumetric_lights"] = Toggle("Объёмный свет", "Световые лучи и объёмное освещение.", "Видео"),
        ["r2_steep_parallax"] = Toggle("Глубокий параллакс", "Добавляет глубину совместимым поверхностям.", "Видео"),
        ["r2_detail_bump"] = Toggle("Детальный рельеф", "Дополнительный рельеф мелких текстур.", "Видео"),
        ["r2_dof_enable"] = Toggle("Глубина резкости", "Размытие объектов вне фокуса.", "Видео"),
        ["r3_dynamic_wet_surfaces"] = Toggle("Мокрые поверхности", "Динамический эффект влажных поверхностей.", "Видео"),
        ["r4_enable_tessellation"] = Toggle("Тесселяция", "Повышает детализацию геометрии на совместимом рендере.", "Видео"),
        ["r__tf_aniso"] = Slider("Анизотропная фильтрация", "Чёткость текстур под углом.", "Видео", 1, 16, 1),
        ["texture_lod"] = Slider("Качество текстур", "Уровень детализации текстур. Меньшее значение обычно даёт более чёткую картинку.", "Видео", 0, 4, 1),
        ["fov"] = Slider("Угол обзора", "Горизонтальный угол обзора камеры.", "Видео", 55, 120, 1),
        ["hud_fov"] = Slider("Положение оружия", "Угол обзора модели оружия в руках.", "Видео", 0.45, 1, 0.01),
        ["snd_volume_eff"] = Slider("Громкость эффектов", "Громкость звуков игры.", "Звук", 0, 1, 0.05),
        ["snd_volume_music"] = Slider("Громкость музыки", "Громкость фоновой музыки.", "Звук", 0, 1, 0.05),
        ["snd_efx"] = Toggle("Звуковые эффекты EFX", "Эхо и окружение OpenAL.", "Звук"),
        ["snd_acceleration"] = Toggle("Аппаратное ускорение звука", "Использовать аппаратное ускорение OpenAL, если оно доступно.", "Звук"),
        ["snd_targets"] = Slider("Количество источников звука", "Сколько звуков может воспроизводиться одновременно.", "Звук", 32, 512, 32),
        ["snd_cache_size"] = Slider("Кэш звука", "Размер кэша звуковых данных.", "Звук", 32, 512, 32),
        ["mouse_sens"] = Slider("Чувствительность мыши", "Основная чувствительность камеры.", "Управление", 0.01, 0.6, 0.005),
        ["mouse_invert"] = Toggle("Инверсия мыши", "Инвертировать вертикальное движение камеры.", "Управление"),
        ["cam_inert"] = Slider("Инерция камеры", "Плавность движения камеры. Ноль отключает инерцию.", "Управление", 0, 1, 0.01),
        ["hud_draw"] = Toggle("Показывать интерфейс", "Главный игровой HUD.", "Интерфейс"),
        ["hud_crosshair"] = Toggle("Показывать прицел", "Стандартный прицел в центре экрана.", "Интерфейс"),
        ["hud_crosshair_dist"] = Toggle("Расстояние до цели", "Показывать расстояние рядом с прицелом.", "Интерфейс"),
        ["hud_weapon"] = Toggle("Показывать оружие", "Модель оружия от первого лица.", "Интерфейс"),
        ["g_3d_pda"] = Toggle("Трёхмерный КПК", "Показывать КПК в руках персонажа.", "Интерфейс"),
        ["discord_status"] = Toggle("Статус Discord", "Показывать текущую активность игры в Discord.", "Интерфейс"),
    };

    private static readonly Dictionary<string, string> McmModules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["3d_scopes"] = "3D-прицелы",
        ["SortingPlus"] = "Сортировка инвентаря",
        ["aim_stamina"] = "Выносливость при прицеливании",
        ["alarm_wakeup"] = "Будильник",
        ["ammomaker"] = "Изготовление патронов",
        ["anthology_hud_selector"] = "Интерфейс Anthology",
        ["barter"] = "Бартер",
        ["beef_nvg"] = "Приборы ночного видения",
        ["blood_pool"] = "Лужи крови",
        ["body_health_system"] = "Система здоровья частей тела",
        ["exo"] = "Экзоскелеты",
        ["magazines"] = "Магазины оружия",
        ["pda_inter"] = "Интерфейс КПК",
        ["spatial_audio"] = "Пространственный звук",
        ["tactic_compass"] = "Тактический компас",
        ["toxic_air"] = "Токсичный воздух",
        ["zhopa2"] = "Z.H.O.P.A. ALIFE 2.0",
    };

    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enable"] = "включить", ["enabled"] = "включено", ["disable"] = "отключить", ["disabled"] = "отключено",
        ["show"] = "показывать", ["hide"] = "скрывать", ["debug"] = "отладка", ["mode"] = "режим",
        ["volume"] = "громкость", ["brightness"] = "яркость", ["distance"] = "дальность", ["range"] = "диапазон",
        ["speed"] = "скорость", ["duration"] = "длительность", ["chance"] = "вероятность", ["factor"] = "множитель",
        ["power"] = "сила", ["drain"] = "расход", ["recover"] = "восстановление", ["weight"] = "вес",
        ["key"] = "клавиша", ["keybind"] = "назначение клавиши", ["position"] = "положение", ["pos"] = "положение",
        ["size"] = "размер", ["offset"] = "смещение", ["color"] = "цвет", ["blur"] = "размытие",
        ["zoom"] = "увеличение", ["opacity"] = "прозрачность", ["scale"] = "масштаб", ["quality"] = "качество",
        ["sound"] = "звук", ["music"] = "музыка", ["hud"] = "интерфейс", ["weapon"] = "оружие",
        ["player"] = "игрок", ["actor"] = "персонаж", ["npc"] = "NPC", ["max"] = "максимум", ["min"] = "минимум",
    };

    public static GameSettingView For(AnomalyConfigurationEntry entry)
    {
        if (entry.Kind == AnomalyConfigurationKind.Anomaly)
        {
            var leafKey = entry.Key.Split('/').Last();
            Anomaly.TryGetValue(leafKey, out var known);
            return new GameSettingView(
                entry.DisplayName ?? known?.Title ?? Humanize(leafKey),
                entry.Description ?? known?.Description ?? "Параметр оригинального меню Anomaly.",
                entry.CategoryDisplayName ?? known?.CategoryTitle ?? Humanize(entry.Category),
                ControlFromMetadata(entry, known) ?? known?.Control ?? InferControl(entry),
                true,
                entry.Minimum ?? known?.Minimum,
                entry.Maximum ?? known?.Maximum,
                entry.Step ?? known?.Step,
                known?.Choices);
        }

        var slash = entry.Key.IndexOf('/');
        var option = slash >= 0 ? entry.Key[(slash + 1)..] : entry.Key;
        var module = slash >= 0 ? entry.Key[..slash] : entry.Category;
        var moduleTitle = entry.CategoryDisplayName
                          ?? (McmModules.TryGetValue(module, out var knownTitle) ? knownTitle : Humanize(module));
        var optionTitle = entry.DisplayName ?? Humanize(option.Replace('/', ' '));
        var control = ControlFromMetadata(entry) ?? InferControl(entry);
        return new GameSettingView(
            optionTitle,
            entry.Description ?? $"Настройка модуля «{moduleTitle}». MCM применит её при следующем запуске игры.",
            moduleTitle,
            control,
            true,
            entry.Minimum,
            entry.Maximum,
            entry.Step ?? InferStep(entry.Value));
    }

    private static GameSettingControl? ControlFromMetadata(
        AnomalyConfigurationEntry entry,
        GameSettingView? known = null)
    {
        var type = entry.ControlType?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        if (type == "check" || type.EndsWith(".check", StringComparison.Ordinal))
        {
            return GameSettingControl.Toggle;
        }

        if (type == "track" || type.EndsWith(".track", StringComparison.Ordinal))
        {
            return entry.Minimum.HasValue && entry.Maximum.HasValue
                ? GameSettingControl.Slider
                : GameSettingControl.Number;
        }

        if (type is "list" or "radio" or "radio_h" or "radio_v"
            || type.Contains("radio", StringComparison.Ordinal))
        {
            // Keep authored choices where the launcher knows them. Dynamic MCM
            // list contents are not safe to evaluate outside the game, so they
            // remain directly editable instead of presenting an empty select.
            return known?.Choices is { Count: > 0 }
                ? GameSettingControl.Select
                : GameSettingControl.Text;
        }

        return type is "input" or "key_bind" or "keybind"
            ? GameSettingControl.Text
            : null;
    }

    public static string CategoryTitle(AnomalyConfigurationKind kind, string category) =>
        kind == AnomalyConfigurationKind.Mcm && McmModules.TryGetValue(category, out var title)
            ? title
            : kind == AnomalyConfigurationKind.Mcm ? Humanize(category) : category;

    private static GameSettingControl InferControl(AnomalyConfigurationEntry entry)
    {
        if (entry.IsBoolean)
        {
            return GameSettingControl.Toggle;
        }

        return double.TryParse(entry.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            ? GameSettingControl.Number
            : GameSettingControl.Text;
    }

    private static double InferStep(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return 1;
        }

        return value.Contains('.') ? Math.Abs(number) < 1 ? 0.01 : 0.1 : 1;
    }

    public static string Humanize(string value)
    {
        var text = CamelCaseBoundary().Replace(value.Replace('_', ' ').Replace('-', ' '), "$1 $2");
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => Words.TryGetValue(part, out var translated) ? translated : part)
            .ToArray();
        if (parts.Length == 0)
        {
            return value;
        }

        var result = string.Join(' ', parts);
        return char.ToUpperInvariant(result[0]) + result[1..];
    }

    private static GameSettingView Toggle(string title, string description, string category) =>
        new(title, description, category, GameSettingControl.Toggle, true);

    private static GameSettingView Slider(string title, string description, string category, double min, double max, double step) =>
        new(title, description, category, GameSettingControl.Slider, true, min, max, step);

    private static GameSettingView Select(string title, string description, string category, IReadOnlyList<GameSettingChoice> choices) =>
        new(title, description, category, GameSettingControl.Select, true, Choices: choices);

    private static GameSettingView Text(string title, string description, string category) =>
        new(title, description, category, GameSettingControl.Text, true);

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex CamelCaseBoundary();
}
