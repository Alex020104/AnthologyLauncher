using System.Text.RegularExpressions;

namespace Anthology.Releaser.Core;

public static partial class ReleaseVersionRules
{
    public static void Validate(string version)
    {
        var match = VersionRegex().Match(version?.Trim() ?? string.Empty);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var revision) || revision < 131)
        {
            throw new ArgumentException("Версия должна иметь вид 2.1.131 или выше, например 2.1.132.", nameof(version));
        }
    }

    [GeneratedRegex("^2\\.1\\.([0-9]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();
}
