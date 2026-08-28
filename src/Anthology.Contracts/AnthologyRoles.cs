namespace Anthology.Contracts;

public static class AnthologyRoles
{
    private static readonly HashSet<string> Developers = new(StringComparer.OrdinalIgnoreCase)
    {
        "alex020104",
        "srallnk",
        "шура",
        "ratniy",
    };

    private static readonly HashSet<string> Moderators = new(StringComparer.OrdinalIgnoreCase)
    {
        "hydra_donnatus",
    };

    public static string Resolve(string? displayName)
    {
        var normalized = Normalize(displayName);
        if (Developers.Contains(normalized)) return "admin";
        if (Moderators.Contains(normalized)) return "mod";
        return "user";
    }

    public static bool IsDeveloper(string? displayName) => Resolve(displayName) == "admin";

    public static string Label(string role) => role switch
    {
        "admin" => "разработчик",
        "mod" => "модератор",
        "ai" => "AI Helper",
        _ => "пользователь",
    };

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
}
