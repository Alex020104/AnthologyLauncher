using System.Text.RegularExpressions;
using Anthology.Contracts;

namespace Anthology.Update.Core;

public static partial class PackageIntegrityCatalogValidator
{
    private static readonly HashSet<string> AllowedInstallRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "game", "modpack", "launcher", "database", "engine", "mods", "tools",
    };

    public static void ValidateAndThrow(SignedPackageIntegrityCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var errors = new List<string>();
        if (catalog.Payload.SchemaVersion != 1)
        {
            errors.Add($"Unsupported package integrity schema version: {catalog.Payload.SchemaVersion}.");
        }
        if (string.IsNullOrWhiteSpace(catalog.Payload.Channel))
        {
            errors.Add("Integrity catalog channel is required.");
        }
        if (string.IsNullOrWhiteSpace(catalog.Payload.ReleaseVersion))
        {
            errors.Add("Integrity catalog release version is required.");
        }
        if (!string.Equals(catalog.Signature.Algorithm, ManifestSecurity.Algorithm, StringComparison.Ordinal))
        {
            errors.Add($"Unsupported integrity signature algorithm: {catalog.Signature.Algorithm}.");
        }
        if (string.IsNullOrWhiteSpace(catalog.Signature.KeyId)
            || string.IsNullOrWhiteSpace(catalog.Signature.Value))
        {
            errors.Add("Integrity catalog signature is required.");
        }

        var artifactIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var managedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in catalog.Payload.Artifacts ?? [])
        {
            if (string.IsNullOrWhiteSpace(package.ArtifactId)
                || !PackageIdRegex().IsMatch(package.ArtifactId)
                || !artifactIds.Add(package.ArtifactId))
            {
                errors.Add($"Invalid or duplicate integrity artifact id: '{package.ArtifactId}'.");
            }
            if (string.IsNullOrWhiteSpace(package.PackageId)
                || !PackageIdRegex().IsMatch(package.PackageId)
                || string.IsNullOrWhiteSpace(package.PackageVersion)
                || string.IsNullOrWhiteSpace(package.RequiredPackageVersion))
            {
                errors.Add($"Integrity package '{package.PackageId}' has no version.");
            }
            if (package.Kind == PackageKind.Launcher)
            {
                errors.Add($"Integrity artifact '{package.ArtifactId}' must not manage launcher state.");
            }
            if (package.ArchiveSize <= 0
                || !Sha256Regex().IsMatch(package.ArchiveSha256)
                || !string.Equals(package.ArchiveFormat, "zip", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Integrity package '{package.PackageId}' has invalid archive metadata.");
            }
            if (!AllowedInstallRoots.Contains(package.InstallRoot) || package.Mirrors is not { Count: > 0 })
            {
                errors.Add($"Integrity package '{package.PackageId}' has no install root or mirrors.");
            }
            foreach (var mirror in package.Mirrors ?? [])
            {
                var localFile = string.Equals(mirror.Provider, "local-file", StringComparison.OrdinalIgnoreCase);
                var bundleFile = string.Equals(mirror.Provider, "bundle-file", StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(mirror.Provider)
                    || !Uri.TryCreate(mirror.Url, UriKind.Absolute, out var uri)
                    || (localFile
                        ? !uri.IsFile
                        : bundleFile
                            ? !string.Equals(uri.Scheme, "bundle", StringComparison.OrdinalIgnoreCase)
                            : !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                              && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add($"Integrity artifact '{package.ArtifactId}' has unsafe mirror URL '{mirror.Url}'.");
                }
            }

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in package.ArchiveFiles ?? [])
            {
                try
                {
                    var normalized = PathSafety.NormalizeRelativePath(file.Path);
                    if (!paths.Add(normalized))
                    {
                        errors.Add($"Integrity package '{package.PackageId}' contains duplicate path '{file.Path}'.");
                    }
                }
                catch (ArgumentException exception)
                {
                    errors.Add($"Integrity package '{package.PackageId}' has unsafe path '{file.Path}': {exception.Message}");
                }

                if (file.Size < 0 || !Sha256Regex().IsMatch(file.Sha256))
                {
                    errors.Add($"Integrity package '{package.PackageId}' has invalid metadata for '{file.Path}'.");
                }
            }
            if (paths.Count == 0 || package.ManagedFiles is not { Count: > 0 })
            {
                errors.Add($"Integrity artifact '{package.ArtifactId}' has no restorable files.");
            }
            foreach (var managedPath in package.ManagedFiles ?? [])
            {
                try
                {
                    var normalized = PathSafety.NormalizeRelativePath(managedPath);
                    if (!paths.Contains(normalized))
                    {
                        errors.Add($"Integrity artifact '{package.ArtifactId}' cannot restore '{managedPath}'.");
                    }
                    if (!managedTargets.Add(package.InstallRoot + "|" + normalized))
                    {
                        errors.Add($"Managed target '{package.InstallRoot}/{normalized}' has more than one origin artifact.");
                    }
                }
                catch (ArgumentException exception)
                {
                    errors.Add($"Integrity artifact '{package.ArtifactId}' has unsafe managed path '{managedPath}': {exception.Message}");
                }
            }

            PackageInstallScopePolicy.AppendIntegrityArtifactErrors(package, errors);
        }

        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }
    }

    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,79}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageIdRegex();
}
