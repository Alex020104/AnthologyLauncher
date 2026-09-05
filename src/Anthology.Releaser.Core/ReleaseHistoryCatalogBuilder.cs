using System.Security.Cryptography;
using System.Text.Json;
using Anthology.Contracts;
using Anthology.Update.Core;

namespace Anthology.Releaser.Core;

/// <summary>
/// Produces the signed history document published next to manifest.json. The
/// first run bootstraps from every still-present, valid version manifest so a
/// fresh launcher immediately receives the existing release archive.
/// </summary>
public static class ReleaseHistoryCatalogBuilder
{
    public const string FileName = "history.json";
    private const int MaximumManifestBytes = 128 * 1024 * 1024;
    private const int MaximumScannedVersionDirectories = 500;

    public static async Task<SignedReleaseHistory> BuildAsync(
        string outputRoot,
        SignedUpdateManifest currentManifest,
        ECDsa signingKey,
        string keyId,
        CancellationToken cancellationToken = default) =>
        await BuildAsync(
            [outputRoot],
            currentManifest,
            signingKey,
            keyId,
            cancellationToken);

    public static async Task<SignedReleaseHistory> BuildAsync(
        IEnumerable<string> trustedPublicationRoots,
        SignedUpdateManifest currentManifest,
        ECDsa signingKey,
        string keyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trustedPublicationRoots);
        ArgumentNullException.ThrowIfNull(currentManifest);
        ArgumentNullException.ThrowIfNull(signingKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);

        var roots = trustedPublicationRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roots.Length == 0)
        {
            throw new ArgumentException("At least one trusted publication root is required.", nameof(trustedPublicationRoots));
        }
        var normalizedKeyId = keyId.Trim();
        ValidateTrustedManifest(currentManifest, signingKey, normalizedKeyId, currentManifest.Payload.Channel);

        var entries = new List<ReleaseHistoryEntry>();
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existingHistory = await TryLoadTrustedHistoryAsync(
                Path.Combine(root, FileName),
                signingKey,
                normalizedKeyId,
                currentManifest.Payload.Channel,
                cancellationToken);
            if (existingHistory is not null)
            {
                entries.AddRange(existingHistory.Payload.Entries);
            }

            if (!Directory.Exists(root))
            {
                continue;
            }

            var scanned = 0;
            foreach (var directory in Directory.EnumerateDirectories(root)
                         .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++scanned > MaximumScannedVersionDirectories)
                {
                    break;
                }
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(directory);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var historicalManifest = await TryLoadTrustedManifestAsync(
                    Path.Combine(directory, "manifest.json"),
                    signingKey,
                    normalizedKeyId,
                    currentManifest.Payload.Channel,
                    cancellationToken);
                if (historicalManifest is not null)
                {
                    entries.Add(CreateEntry(historicalManifest.Payload));
                }
            }
        }

        entries.Add(CreateEntry(currentManifest.Payload));
        var normalizedEntries = Normalize(entries);
        var updatedAt = normalizedEntries.Max(entry => entry.PublishedAt);
        var payload = new ReleaseHistoryCatalog(
            ReleaseHistoryValidator.CurrentSchemaVersion,
            currentManifest.Payload.Channel.Trim().ToLowerInvariant(),
            updatedAt,
            normalizedEntries);
        ReleaseHistoryValidator.ValidateAndThrow(payload, currentManifest.Payload.Channel);
        return ManifestSecurity.Sign(payload, signingKey, normalizedKeyId);
    }

    public static async Task<string> BuildAndWriteVersionAsync(
        string outputRoot,
        SignedUpdateManifest currentManifest,
        ECDsa signingKey,
        string keyId,
        CancellationToken cancellationToken = default)
    {
        var signed = await BuildAsync(
            outputRoot,
            currentManifest,
            signingKey,
            keyId,
            cancellationToken);
        var destination = Path.Combine(
            Path.GetFullPath(outputRoot),
            currentManifest.Payload.Version.Trim(),
            FileName);
        await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(destination, signed, cancellationToken);
        return destination;
    }

    private static ReleaseHistoryEntry[] Normalize(IEnumerable<ReleaseHistoryEntry> entries) => entries
        .Where(entry => entry is not null
                        && !string.IsNullOrWhiteSpace(entry.Version)
                        && entry.PublishedAt != default
                        && entry.Changelog is not null)
        .GroupBy(entry => entry.Version.Trim(), StringComparer.OrdinalIgnoreCase)
        .Select(group => group
            .OrderByDescending(entry => entry.PublishedAt)
            .ThenByDescending(entry => ChangelogInformationScore(entry.Changelog))
            .First() with { Version = group.Key })
        .OrderByDescending(entry => entry.PublishedAt)
        .ThenByDescending(entry => entry.Version, StringComparer.OrdinalIgnoreCase)
        .Take(ReleaseHistoryValidator.MaximumEntries)
        .ToArray();

    private static int ChangelogInformationScore(ReleaseChangelog changelog) =>
        (changelog.Title?.Length ?? 0)
        + (changelog.Summary?.Length ?? 0)
        + (changelog.Body?.Length ?? 0)
        + (changelog.Warnings?.Length ?? 0)
        + (changelog.Translations?.Values.Sum(value =>
            (value.Title?.Length ?? 0)
            + (value.Summary?.Length ?? 0)
            + (value.Body?.Length ?? 0)
            + (value.Warnings?.Length ?? 0)) ?? 0);

    private static ReleaseHistoryEntry CreateEntry(UpdateManifest manifest)
    {
        var version = manifest.Version.Trim();
        return new ReleaseHistoryEntry(
            version,
            manifest.PublishedAt,
            manifest.Content?.Changelog ?? new ReleaseChangelog(
                $"Обновление {version}",
                "Описание этой версии ещё не опубликовано.",
                string.Empty,
                string.Empty));
    }

    private static async Task<SignedReleaseHistory?> TryLoadTrustedHistoryAsync(
        string path,
        ECDsa signingKey,
        string keyId,
        string channel,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024)
            {
                return null;
            }
            await using var stream = OpenRead(path);
            var history = await JsonSerializer.DeserializeAsync<SignedReleaseHistory>(
                stream,
                ManifestJson.Options,
                cancellationToken);
            if (history is null
                || !string.Equals(history.Signature.KeyId, keyId, StringComparison.Ordinal)
                || !ManifestSecurity.Verify(history, signingKey))
            {
                return null;
            }

            ReleaseHistoryValidator.ValidateAndThrow(history.Payload, channel);
            return history;
        }
        catch (Exception exception) when (exception is IOException
                                           or JsonException
                                           or InvalidDataException
                                           or CryptographicException
                                           or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<SignedUpdateManifest?> TryLoadTrustedManifestAsync(
        string path,
        ECDsa signingKey,
        string keyId,
        string channel,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumManifestBytes)
            {
                return null;
            }
            await using var stream = OpenRead(path);
            var manifest = await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(
                stream,
                ManifestJson.Options,
                cancellationToken);
            if (manifest is null)
            {
                return null;
            }
            ValidateTrustedManifest(manifest, signingKey, keyId, channel);
            return manifest;
        }
        catch (Exception exception) when (exception is IOException
                                           or JsonException
                                           or InvalidDataException
                                           or CryptographicException
                                           or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void ValidateTrustedManifest(
        SignedUpdateManifest manifest,
        ECDsa signingKey,
        string keyId,
        string channel)
    {
        if (manifest.Payload is null
            || manifest.Signature is null
            || !string.Equals(manifest.Signature.KeyId, keyId, StringComparison.Ordinal)
            || !string.Equals(manifest.Payload.Channel?.Trim(), channel.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(manifest.Payload.Version)
            || manifest.Payload.PublishedAt == default
            || !ManifestSecurity.Verify(manifest, signingKey))
        {
            throw new CryptographicException("Release manifest is not trusted for the history catalog.");
        }
    }

    private static FileStream OpenRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
}
