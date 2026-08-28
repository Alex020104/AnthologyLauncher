using System.Text.RegularExpressions;
using Anthology.Contracts;

namespace Anthology.Update.Core;

public static partial class ManifestValidator
{
    private static readonly HashSet<string> AllowedInstallRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "game",
        "modpack",
        "launcher",
        "database",
        "engine",
        "mods",
        "tools",
    };

    public static IReadOnlyList<string> Validate(SignedUpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var errors = new List<string>();
        var payload = manifest.Payload;

        if (payload.SchemaVersion != 1)
        {
            errors.Add($"Unsupported schema version: {payload.SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(payload.Channel))
        {
            errors.Add("Channel is required.");
        }

        if (string.IsNullOrWhiteSpace(payload.Version))
        {
            errors.Add("Version is required.");
        }

        if (payload.Packages.Count == 0)
        {
            errors.Add("At least one package is required.");
        }

        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in payload.Packages)
        {
            ValidatePackage(package, packageIds, errors);
        }

        if (string.IsNullOrWhiteSpace(manifest.Signature.KeyId))
        {
            errors.Add("Signature key id is required.");
        }

        if (!string.Equals(manifest.Signature.Algorithm, ManifestSecurity.Algorithm, StringComparison.Ordinal))
        {
            errors.Add($"Unsupported signature algorithm: {manifest.Signature.Algorithm}.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Signature.Value))
        {
            errors.Add("Manifest signature is required.");
        }

        return errors;
    }

    public static void ValidateAndThrow(SignedUpdateManifest manifest)
    {
        var errors = Validate(manifest);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }
    }

    private static void ValidatePackage(
        PackageManifest package,
        HashSet<string> packageIds,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(package.Id) || !PackageIdRegex().IsMatch(package.Id))
        {
            errors.Add($"Invalid package id: '{package.Id}'.");
        }
        else if (!packageIds.Add(package.Id))
        {
            errors.Add($"Duplicate package id: '{package.Id}'.");
        }

        if (!AllowedInstallRoots.Contains(package.InstallRoot))
        {
            errors.Add($"Package '{package.Id}' uses unknown install root '{package.InstallRoot}'.");
        }

        if (package.Size <= 0)
        {
            errors.Add($"Package '{package.Id}' has invalid size.");
        }

        if (!Sha256Regex().IsMatch(package.Sha256))
        {
            errors.Add($"Package '{package.Id}' has invalid SHA-256.");
        }

        if (package.Mirrors.Count == 0)
        {
            errors.Add($"Package '{package.Id}' has no mirrors.");
        }

        foreach (var mirror in package.Mirrors)
        {
            if (string.IsNullOrWhiteSpace(mirror.Provider))
            {
                errors.Add($"Package '{package.Id}' has a mirror without provider.");
            }

            if (!Uri.TryCreate(mirror.Url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && !IsLocalDevelopmentUri(uri)))
            {
                errors.Add($"Package '{package.Id}' has unsafe mirror URL '{mirror.Url}'.");
            }
        }

        foreach (var path in package.Files)
        {
            try
            {
                _ = PathSafety.NormalizeRelativePath(path);
            }
            catch (ArgumentException exception)
            {
                errors.Add($"Package '{package.Id}' has unsafe path '{path}': {exception.Message}");
            }
        }
    }

    private static bool IsLocalDevelopmentUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp
        && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,79}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageIdRegex();

    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}
