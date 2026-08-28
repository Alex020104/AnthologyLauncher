namespace Anthology.Contracts;

public static class DemoContent
{
    public static CommunityFeed CreateFeed()
    {
        var now = DateTimeOffset.UtcNow;
        return new CommunityFeed(
            News:
            [
                new("next-architecture", "Anthology Next: новая платформа", "Лаунчер, апдейтер, релизер и сообщество теперь развиваются как независимые модули.", now.AddHours(-4), "Разработка"),
                new("mirror-protocol", "Обновления без привязки к GitHub", "Пакет может иметь несколько зеркал: GitHub, Яндекс.Диск, Google Drive или обычный HTTPS/CDN.", now.AddDays(-1), "Обновления"),
            ],
            Videos:
            [
                new("dev-diary-01", "Дневник разработки: Anthology Next", "youtube", string.Empty, null, TimeSpan.FromMinutes(8)),
            ],
            Mods:
            [
                new("rak", "R.A.K Weapon Pack", "Anthology Team", "Оружейный модуль и адаптации для Anthology.", "OBT", ["оружие", "3DSS", "PiP"]),
                new("performance", "Anthology Performance", "Anthology Team", "Набор исправлений производительности и стабильности.", "Next", ["движок", "FPS", "стабильность"]),
            ],
            Polls:
            [
                new("priority-01", "Какой раздел развивать следующим?", [
                    new("updater", "Обновления и восстановление", 42),
                    new("library", "Библиотека модов", 31),
                    new("community", "Чат и обратная связь", 27),
                ], now.AddDays(7)),
            ],
            Channels:
            [
                new("general", "Общий", "Обсуждение сборки и новостей"),
                new("support", "Помощь", "Вопросы по установке и запуску"),
                new("feedback", "Отзывы", "Предложения и конструктивная обратная связь"),
                new("developer", "Разработчику", "Прямые обращения к команде", true),
            ]);
    }
}
