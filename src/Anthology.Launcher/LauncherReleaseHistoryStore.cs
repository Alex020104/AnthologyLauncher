using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Anthology.Contracts;
using Anthology.Update.Core;

namespace Anthology.Launcher;

public sealed record LauncherReleaseHistoryEntry(
    string Version,
    DateTimeOffset PublishedAt,
    ReleaseChangelog Changelog);

/// <summary>
/// Keeps a verified copy of the public signed release catalog and a compatibility
/// journal for manifests published before history.json existed. A network or
/// validation failure never replaces the last verified on-disk data.
/// </summary>
public sealed class LauncherReleaseHistoryStore(
    LauncherSettingsStore settingsStore,
    HttpClient httpClient) : IDisposable
{
    private static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(12);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.Combine(settingsStore.DataRoot, "release-history.json");
    private readonly string _signedPath = Path.Combine(settingsStore.DataRoot, "release-history.signed.json");
    private readonly ReleaseHistoryClient _client = new(httpClient);

    public async Task<IReadOnlyList<LauncherReleaseHistoryEntry>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var signed = await TryLoadVerifiedAsync(_signedPath, cancellationToken);
            if (signed is not null)
            {
                return Normalize(Map(signed.Payload.Entries)
                    .Concat(await ReadUnsafeAsync(cancellationToken)));
            }
            return await ReadUnsafeAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LauncherReleaseHistoryEntry>> RefreshFromServerAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.Current;
        var source = ReleaseHistorySourceResolver.Resolve(
            settings.ReleaseHistorySource,
            settings.ManifestSource);
        if (string.IsNullOrWhiteSpace(source)
            || string.IsNullOrWhiteSpace(settings.PublicKeyPath)
            || !File.Exists(settings.PublicKeyPath))
        {
            return await LoadAsync(cancellationToken);
        }

        SignedReleaseHistory? verified = null;
        var downloaded = false;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(RemoteTimeout);
            try
            {
                ProductionTrustAnchor.ValidatePublicKey(settings.PublicKeyPath);
                verified = await _client.LoadVerifiedAsync(
                    source,
                    settings.PublicKeyPath,
                    settings.UpdateChannel,
                    ProductionTrustAnchor.KeyId,
                    timeout.Token);
                ProductionTrustAnchor.ValidateReleaseHistory(verified);
                downloaded = true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A slow mirror falls back to the last locally verified catalog.
            }
            catch (Exception exception) when (exception is IOException
                                               or HttpRequestException
                                               or JsonException
                                               or InvalidDataException
                                               or CryptographicException
                                               or ArgumentException
                                               or NotSupportedException
                                               or UnauthorizedAccessException)
            {
                // Invalid, unavailable, or untrusted remote data never replaces cache.
            }
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var cached = await TryLoadVerifiedAsync(_signedPath, cancellationToken);
            if (downloaded && cached is not null && IsOlderThan(verified!, cached))
            {
                // A valid old document can still be replayed by a stale mirror.
                // Preserve the newest catalog that this installation has seen.
                verified = cached;
                downloaded = false;
            }
            verified ??= cached;
            if (verified is null)
            {
                return await ReadUnsafeAsync(cancellationToken);
            }

            if (downloaded)
            {
                await WriteSignedUnsafeAsync(verified, cancellationToken);
            }

            var entries = Normalize(Map(verified.Payload.Entries)
                .Concat(await ReadUnsafeAsync(cancellationToken)));
            await WriteUnsafeAsync(entries, cancellationToken);
            return entries;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LauncherReleaseHistoryEntry>> RememberAsync(
        string version,
        DateTimeOffset publishedAt,
        ReleaseChangelog? changelog,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var normalizedVersion = version.Trim();
        var normalizedChangelog = changelog ?? new ReleaseChangelog(
            $"Обновление {normalizedVersion}",
            "Описание этой версии ещё не опубликовано.",
            string.Empty,
            string.Empty);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = (await ReadUnsafeAsync(cancellationToken)).ToList();
            var matching = existing.FirstOrDefault(entry =>
                string.Equals(entry.Version, normalizedVersion, StringComparison.OrdinalIgnoreCase));
            if (matching is not null
                && matching.PublishedAt.Equals(publishedAt)
                && ChangelogsEqual(matching.Changelog, normalizedChangelog))
            {
                return existing;
            }

            existing.RemoveAll(entry =>
                string.Equals(entry.Version, normalizedVersion, StringComparison.OrdinalIgnoreCase));
            existing.Add(new LauncherReleaseHistoryEntry(
                normalizedVersion,
                publishedAt,
                normalizedChangelog));
            var normalized = Normalize(existing);
            await WriteUnsafeAsync(normalized, cancellationToken);
            return normalized;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<IReadOnlyList<LauncherReleaseHistoryEntry>> ReadUnsafeAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                32 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<ReleaseHistoryDocument>(
                stream,
                ManifestJson.Options,
                cancellationToken);
            return Normalize(document?.Entries ?? []);
        }
        catch (Exception exception) when (exception is IOException
                                           or JsonException
                                           or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private async Task WriteUnsafeAsync(
        IReadOnlyList<LauncherReleaseHistoryEntry> entries,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             32 * 1024,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new ReleaseHistoryDocument(1, entries),
                    ManifestJson.Options,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporary, _path, true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A later successful write replaces a stale temporary file.
            }
        }
    }

    private static LauncherReleaseHistoryEntry[] Normalize(
        IEnumerable<LauncherReleaseHistoryEntry> entries) => entries
        .Where(entry => !string.IsNullOrWhiteSpace(entry.Version) && entry.Changelog is not null)
        .GroupBy(entry => entry.Version.Trim(), StringComparer.OrdinalIgnoreCase)
        .Select(group => group.OrderByDescending(entry => entry.PublishedAt).First())
        .OrderByDescending(entry => entry.PublishedAt)
        .ThenByDescending(entry => entry.Version, StringComparer.OrdinalIgnoreCase)
        .Take(ReleaseHistoryValidator.MaximumEntries)
        .ToArray();

    private async Task<SignedReleaseHistory?> TryLoadVerifiedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var settings = settingsStore.Current;
        if (!File.Exists(path)
            || string.IsNullOrWhiteSpace(settings.PublicKeyPath)
            || !File.Exists(settings.PublicKeyPath))
        {
            return null;
        }

        try
        {
            ProductionTrustAnchor.ValidatePublicKey(settings.PublicKeyPath);
            var history = await _client.LoadVerifiedAsync(
                path,
                settings.PublicKeyPath,
                settings.UpdateChannel,
                ProductionTrustAnchor.KeyId,
                cancellationToken);
            ProductionTrustAnchor.ValidateReleaseHistory(history);
            return history;
        }
        catch (Exception exception) when (exception is IOException
                                           or JsonException
                                           or InvalidDataException
                                           or CryptographicException
                                           or ArgumentException
                                           or NotSupportedException
                                           or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task WriteSignedUnsafeAsync(
        SignedReleaseHistory history,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_signedPath)!);
        var temporary = _signedPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             32 * 1024,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, history, ManifestJson.Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, _signedPath, true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A later verified refresh replaces a stale temporary file.
            }
        }
    }

    private static LauncherReleaseHistoryEntry[] Map(IEnumerable<ReleaseHistoryEntry> entries) =>
        Normalize(entries.Select(entry => new LauncherReleaseHistoryEntry(
            entry.Version,
            entry.PublishedAt,
            entry.Changelog)));

    private static bool ChangelogsEqual(ReleaseChangelog left, ReleaseChangelog right) =>
        string.Equals(left.Title, right.Title, StringComparison.Ordinal)
        && string.Equals(left.Summary, right.Summary, StringComparison.Ordinal)
        && string.Equals(left.Body, right.Body, StringComparison.Ordinal)
        && string.Equals(left.Warnings, right.Warnings, StringComparison.Ordinal);

    private static bool IsOlderThan(SignedReleaseHistory candidate, SignedReleaseHistory cached)
    {
        if (candidate.Payload.UpdatedAt < cached.Payload.UpdatedAt)
        {
            return true;
        }
        if (candidate.Payload.UpdatedAt > cached.Payload.UpdatedAt)
        {
            return false;
        }

        var candidateVersions = candidate.Payload.Entries
            .Select(entry => entry.Version.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return cached.Payload.Entries.Any(entry => !candidateVersions.Contains(entry.Version.Trim()));
    }

    private sealed record ReleaseHistoryDocument(
        int SchemaVersion,
        IReadOnlyList<LauncherReleaseHistoryEntry> Entries);
}
