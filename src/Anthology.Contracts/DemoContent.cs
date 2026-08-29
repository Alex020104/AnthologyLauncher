namespace Anthology.Contracts;

public static class DemoContent
{
    public static CommunityFeed CreateFeed()
    {
        var now = DateTimeOffset.UtcNow;
        return new CommunityFeed(
            News: [],
            Videos: [],
            Mods:
            [
                new("rak", "R.A.K Weapon Pack", "Anthology Team", "Оружейный модуль и адаптации для Anthology.", "OBT", ["оружие", "3DSS", "PiP"], Section: "dev"),
                new("performance", "Anthology Performance", "Anthology Team", "Набор исправлений производительности и стабильности.", "Next", ["движок", "FPS", "стабильность"], Section: "modmakers"),
                new("classic-balance", "Classic Balance", "Anthology Community", "Альтернативное решение спорных механик без изменения основной сборки.", "1.0", ["баланс", "опционально"], Section: "solutions"),
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
