using Anthology.Contracts;

namespace Anthology.Releaser.Core;

/// <summary>
/// Imports the old launcher copy as ordinary editable drafts exactly once.
/// After workspace schema 3 is saved, deleted entries are never recreated.
/// </summary>
public static class EditorialContentSeed
{
    public static bool AddMissing(List<ContentDraft> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var changed = false;
        if (!content.Any(item => item.Kind == ContentKind.News))
        {
            content.AddRange(CreateNews());
            changed = true;
        }
        if (!content.Any(item => item.Kind == ContentKind.Information))
        {
            content.AddRange(CreateInformation());
            changed = true;
        }
        return changed;
    }

    public static IReadOnlyList<ContentDraft> CreateNews() =>
    [
        new()
        {
            Id = "next-architecture",
            Kind = ContentKind.News,
            Section = "Разработка",
            Title = "Anthology Next: новая платформа",
            Summary = "Лаунчер, апдейтер, релизер и сообщество развиваются как независимые модули.",
            Body = "Здесь можно полностью переписать новость, добавить изображения, ссылки, видео и текстовые блоки, а затем опубликовать её отдельно от обновления игры.",
        },
        new()
        {
            Id = "mirror-protocol",
            Kind = ContentKind.News,
            Section = "Обновления",
            Title = "Обновления без привязки к GitHub",
            Summary = "Пакет может иметь несколько зеркал: GitHub, Яндекс.Диск, Google Drive или обычный HTTPS/CDN.",
            Body = "Источники и текст этой новости теперь полностью управляются в Releaser Next. Запись можно изменить, переместить, снять с публикации или удалить.",
        },
    ];

    public static IReadOnlyList<ContentDraft> CreateInformation() =>
    [
        new()
        {
            Id = "requirements",
            Kind = ContentKind.Information,
            Section = "general",
            Title = "Системные требования",
            Summary = "Минимальные и рекомендуемые параметры для оригинала и модпака.",
            Body = "Требования зависят от выбранного варианта игры, рендера, разрешения и дополнительных проектов.",
            Blocks =
            [
                TextBlock("requirements-original", "Оригинал", "Должен запускаться на любой 64-битной системе начиная с DirectX 8. DirectX 10 в оригинальной конфигурации по умолчанию не рекомендуется."),
                TextBlock("requirements-minimum", "Оригинал + модпак — минимум", "• GTX 1660 6 ГБ или RX 580 8 ГБ;\n• любой четырёхъядерный процессор;\n• 16 ГБ оперативной памяти и файл подкачки 40–50 ГБ;\n• SSD для приемлемого времени загрузки."),
                TextBlock("requirements-recommended", "Рекомендуется", "• RTX 3060 8 ГБ или RX 6600 XT 8 ГБ;\n• современный шестиядерный процессор;\n• 32 ГБ оперативной памяти;\n• NVMe/M.2 SSD.\n\nОсновное тестирование ведётся в разрешениях 1920×1080 и 2560×1440."),
            ],
        },
        new()
        {
            Id = "original",
            Kind = ContentKind.Information,
            Section = "general",
            Title = "Информация об Оригинале",
            Summary = "Основа Anthology и доступные сюжетные линии без опционального модпака.",
            Body = "Оригинальная версия основана на S.T.A.L.K.E.R. Anomaly 1.5.3 и сюжетной платформе A.N.T.H.O.L.O.G.Y под руководством Максима Ратного.",
            Blocks =
            [
                TextBlock("original-stories", "Сюжетная основа", "В состав входят адаптации сюжетов «Тень Чернобыля» и «Зов Припяти»; «Чистое небо» находится в активной разработке. Также подключены самостоятельные сюжетные модификации."),
                TextBlock("original-freeplay", "Свободная игра", "Для свободной игры остаются сюжетные линии Anomaly/CoC: «Легенда Зоны», «Смертный Грех» и «Послесвечение»."),
            ],
        },
        new()
        {
            Id = "modpack",
            Kind = ContentKind.Information,
            Section = "general",
            Title = "Информация о Модпаке",
            Summary = "Профили Standard и Hard, совместимость и принцип модульности.",
            Body = "Модпак поддерживает DirectX 11 и является опциональным дополнением к оригиналу. Он адаптирует моды под Anthology, но сам по себе не заменяет сюжетную основу.",
            Blocks =
            [
                TextBlock("modpack-standard", "Standard", "Классическое прохождение с механиками Anomaly и дополнительными возможностями для обычного игрока."),
                TextBlock("modpack-hard", "Hard", "Более сложный профиль с другим балансом и спорными механиками. Он рассчитан на игроков, которым нужен заметно более жёсткий старт и развитие."),
                TextBlock("modpack-mo2", "Модульность и MO2", "Оружейный пакет R.A.K и его модули остаются модульными. Встроенный раздел MO2 управляет профилями, дополнениями и порядком их загрузки."),
            ],
        },
        new()
        {
            Id = "stories",
            Kind = ContentKind.Information,
            Section = "stories",
            Title = "Сюжеты",
            Summary = "Отдельные карточки всех оригинальных, модифицированных и freeplay-историй.",
            Body = "Каждая карточка ниже является самостоятельным редактируемым подразделом. Её можно перемещать, переписывать или удалять в релизере.",
            Blocks =
            [
                Story("story-soc", "Тень Чернобыля", "ОРИГИНАЛЬНЫЙ СЮЖЕТ · ДОСТУПЕН", "Адаптация классической кампании для платформы Anthology. Кампания переносит историю «Тени Чернобыля» в общую техническую основу Anomaly 1.5.3 и Anthology."),
                Story("story-cop", "Зов Припяти", "ОРИГИНАЛЬНЫЙ СЮЖЕТ · ДОСТУПЕН", "Сюжетная линия «Зова Припяти» внутри общей сборки. Здесь можно разместить окончательный синопсис, рекомендуемый профиль и замечания по совместимости сохранений."),
                Story("story-cs", "Чистое небо", "ОРИГИНАЛЬНЫЙ СЮЖЕТ · В РАЗРАБОТКЕ", "Подготавливаемая адаптация кампании «Чистого неба». Карточку можно обновлять вместе со статусом разработки."),
                Story("story-path-fog", "Путь во Мгле", "СЮЖЕТНАЯ МОДИФИКАЦИЯ · ДОСТУПЕН", "Самостоятельная история, включённая в Anthology, со своей последовательностью заданий."),
                Story("story-spatial", "Пространственная Аномалия", "СЮЖЕТНАЯ МОДИФИКАЦИЯ · ДОСТУПЕН", "Самостоятельный сюжетный модуль в составе платформы Anthology."),
                Story("story-forgotten", "Забытый отряд", "СЮЖЕТНАЯ МОДИФИКАЦИЯ · ДОСТУПЕН", "Отдельная кампания из состава Anthology."),
                Story("story-death-web", "Смерти вопреки. В паутине лжи", "СЮЖЕТНАЯ МОДИФИКАЦИЯ · ДОСТУПЕН", "Сюжетная модификация, адаптированная для общей платформы."),
                Story("story-valley", "Долина Шорохов", "СЮЖЕТНАЯ МОДИФИКАЦИЯ · ДОСТУПЕН", "Самостоятельный сюжетный модуль."),
                Story("story-attribute", "Атрибут", "СЮЖЕТНАЯ МОДИФИКАЦИЯ · ДОСТУПЕН", "Отдельная сюжетная линия Anthology."),
                Story("story-legend", "Легенда Зоны", "ANOMALY / FREEPLAY · ДОСТУПЕН", "Основная freeplay-цепочка Anomaly/CoC для прохождения в открытой Зоне."),
                Story("story-sin", "Смертный Грех", "ANOMALY / FREEPLAY · ДОСТУПЕН", "Продолжение стандартного сюжетного цикла Anomaly."),
                Story("story-afterglow", "Послесвечение", "ANOMALY / FREEPLAY · ДОСТУПЕН", "Заключительная ветка стандартного сюжетного цикла."),
            ],
        },
    ];

    private static ContentBlockDraft TextBlock(string id, string title, string body) => new()
    {
        Id = id,
        Kind = ContentBlockKind.Section,
        Title = title,
        Body = body,
    };

    private static ContentBlockDraft Story(string id, string title, string status, string body) => new()
    {
        Id = id,
        Kind = ContentBlockKind.Article,
        Title = title,
        Body = $"{status}\n\n{body}",
    };
}
