using Anthology.Contracts;

namespace Anthology.Update.Core;

public static class ReleaseHistoryValidator
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumEntries = 100;

    private const int MaximumVersionLength = 128;
    private const int MaximumTitleLength = 512;
    private const int MaximumSummaryLength = 8 * 1024;
    private const int MaximumBodyLength = 256 * 1024;
    private const int MaximumWarningsLength = 64 * 1024;
    private const int MaximumTranslations = 16;

    public static void ValidateAndThrow(ReleaseHistoryCatalog catalog, string expectedChannel)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedChannel);

        if (catalog.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported release-history schema {catalog.SchemaVersion}. Expected {CurrentSchemaVersion}.");
        }
        if (!string.Equals(catalog.Channel?.Trim(), expectedChannel.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Release-history channel '{catalog.Channel}' does not match '{expectedChannel.Trim()}'.");
        }
        if (catalog.UpdatedAt == default)
        {
            throw new InvalidDataException("Release history has no update timestamp.");
        }
        if (catalog.Entries is null || catalog.Entries.Count == 0)
        {
            throw new InvalidDataException("Release history contains no releases.");
        }
        if (catalog.Entries.Count > MaximumEntries)
        {
            throw new InvalidDataException(
                $"Release history contains more than {MaximumEntries} releases.");
        }

        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? newestPublishedAt = null;
        foreach (var entry in catalog.Entries)
        {
            if (entry is null)
            {
                throw new InvalidDataException("Release history contains an empty release entry.");
            }

            var version = entry.Version?.Trim();
            if (string.IsNullOrWhiteSpace(version) || version.Length > MaximumVersionLength)
            {
                throw new InvalidDataException("Release history contains an invalid version.");
            }
            if (!versions.Add(version))
            {
                throw new InvalidDataException($"Release history contains duplicate version '{version}'.");
            }
            if (entry.PublishedAt == default)
            {
                throw new InvalidDataException($"Release '{version}' has no publication timestamp.");
            }
            if (entry.Changelog is null)
            {
                throw new InvalidDataException($"Release '{version}' has no changelog.");
            }

            ValidateChangelog(entry.Changelog, version);
            newestPublishedAt = newestPublishedAt is null || entry.PublishedAt > newestPublishedAt
                ? entry.PublishedAt
                : newestPublishedAt;
        }

        if (newestPublishedAt > catalog.UpdatedAt)
        {
            throw new InvalidDataException("Release history update timestamp predates its newest release.");
        }
    }

    private static void ValidateChangelog(ReleaseChangelog changelog, string version)
    {
        ValidateText(changelog.Title, MaximumTitleLength, version, "title");
        ValidateText(changelog.Summary, MaximumSummaryLength, version, "summary");
        ValidateText(changelog.Body, MaximumBodyLength, version, "body");
        ValidateText(changelog.Warnings, MaximumWarningsLength, version, "warnings");

        if (changelog.Translations is null)
        {
            return;
        }
        if (changelog.Translations.Count > MaximumTranslations)
        {
            throw new InvalidDataException(
                $"Release '{version}' contains too many changelog translations.");
        }

        foreach (var (language, translation) in changelog.Translations)
        {
            if (string.IsNullOrWhiteSpace(language) || language.Length > 32 || translation is null)
            {
                throw new InvalidDataException($"Release '{version}' contains an invalid translation.");
            }

            ValidateText(translation.Title, MaximumTitleLength, version, $"{language} title");
            ValidateText(translation.Summary, MaximumSummaryLength, version, $"{language} summary");
            ValidateText(translation.Body, MaximumBodyLength, version, $"{language} body");
            ValidateText(translation.Warnings, MaximumWarningsLength, version, $"{language} warnings");
        }
    }

    private static void ValidateText(string? value, int maximumLength, string version, string field)
    {
        if (value is null || value.Length > maximumLength)
        {
            throw new InvalidDataException(
                $"Release '{version}' has an invalid changelog {field}.");
        }
    }
}
