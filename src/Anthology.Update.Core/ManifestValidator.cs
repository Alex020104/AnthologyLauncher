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

        if (payload.SchemaVersion is not (1 or 2 or 3))
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

        if (payload.Packages.Count == 0
            && (payload.SchemaVersion < 2 || payload.Content is null || payload.Content.Items.Count == 0))
        {
            errors.Add("At least one package or one content item is required.");
        }

        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in payload.Packages)
        {
            ValidatePackage(package, packageIds, errors);
        }

        if (payload.Content is not null)
        {
            ValidateContent(payload.Content, errors);
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

        if (string.IsNullOrWhiteSpace(package.DisplayName))
        {
            errors.Add($"Package '{package.Id}' has no display name.");
        }

        if (string.IsNullOrWhiteSpace(package.Version))
        {
            errors.Add($"Package '{package.Id}' has no version.");
        }

        if (!string.Equals(package.ArchiveFormat, "zip", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Package '{package.Id}' uses unsupported archive format '{package.ArchiveFormat}'.");
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

            var localFile = string.Equals(mirror.Provider, "local-file", StringComparison.OrdinalIgnoreCase);
            var bundleFile = string.Equals(mirror.Provider, "bundle-file", StringComparison.OrdinalIgnoreCase);
            if (!Uri.TryCreate(mirror.Url, UriKind.Absolute, out var uri)
                || (localFile
                    ? !uri.IsFile
                    : bundleFile
                        ? !string.Equals(uri.Scheme, "bundle", StringComparison.OrdinalIgnoreCase)
                          || !string.IsNullOrEmpty(uri.Query)
                          || !string.IsNullOrEmpty(uri.Fragment)
                          || !string.IsNullOrEmpty(uri.UserInfo)
                        : uri.Scheme != Uri.UriSchemeHttps && !IsLocalDevelopmentUri(uri)))
            {
                errors.Add($"Package '{package.Id}' has unsafe mirror URL '{mirror.Url}'.");
            }
        }

        if (package.Files.Count == 0 && (package.DeletedFiles is null || package.DeletedFiles.Count == 0))
        {
            errors.Add($"Package '{package.Id}' has no files and no deletion paths.");
        }

        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in package.Files)
        {
            try
            {
                var normalized = PathSafety.NormalizeRelativePath(path);
                if (!filePaths.Add(normalized))
                {
                    errors.Add($"Package '{package.Id}' contains duplicate path '{path}'.");
                }
            }
            catch (ArgumentException exception)
            {
                errors.Add($"Package '{package.Id}' has unsafe path '{path}': {exception.Message}");
            }
        }

        foreach (var path in package.PreservedPaths ?? [])
        {
            try
            {
                PathSafety.NormalizeRelativePath(path);
            }
            catch (ArgumentException exception)
            {
                errors.Add($"Package '{package.Id}' has unsafe preserved path '{path}': {exception.Message}");
            }
        }


        var deletedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in package.DeletedFiles ?? [])
        {
            try
            {
                var normalized = PathSafety.NormalizeRelativePath(path);
                if (!deletedPaths.Add(normalized))
                {
                    errors.Add($"Package '{package.Id}' contains duplicate deletion path '{path}'.");
                }
                if (filePaths.Contains(normalized))
                {
                    errors.Add($"Package '{package.Id}' both installs and deletes '{path}'.");
                }
            }
            catch (ArgumentException exception)
            {
                errors.Add($"Package '{package.Id}' has unsafe deletion path '{path}': {exception.Message}");
            }
        }
    }

    private static void ValidateContent(ContentCatalog content, List<string> errors)
    {
        if (content.SchemaVersion is not (1 or 2 or 3 or 4))
        {
            errors.Add($"Unsupported content schema version: {content.SchemaVersion}.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in content.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || !PackageIdRegex().IsMatch(item.Id))
            {
                errors.Add($"Invalid content id: '{item.Id}'.");
            }
            else if (!ids.Add(item.Id))
            {
                errors.Add($"Duplicate content id: '{item.Id}'.");
            }

            if (string.IsNullOrWhiteSpace(item.Title))
            {
                errors.Add($"Content '{item.Id}' has no title.");
            }

            if (item.Translations is not null)
            {
                foreach (var (language, translation) in item.Translations)
                {
                    if (!AnthologyLanguages.IsSupported(language))
                    {
                        errors.Add($"Content '{item.Id}' has unsupported translation '{language}'.");
                    }
                    if (string.IsNullOrWhiteSpace(translation.Title))
                    {
                        errors.Add($"Content '{item.Id}' translation '{language}' has no title.");
                    }
                }
            }

            foreach (var image in item.Images)
            {
                ValidatePublicHttpsUrl(image, $"Content '{item.Id}' has unsafe image URL", errors);
            }

            foreach (var video in item.Videos)
            {
                ValidatePublicHttpsUrl(video.Url, $"Content '{item.Id}' has unsafe video URL", errors);
            }

            var blockIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var block in item.Blocks ?? [])
            {
                if (string.IsNullOrWhiteSpace(block.Id) || !PackageIdRegex().IsMatch(block.Id))
                {
                    errors.Add($"Content '{item.Id}' has invalid block id '{block.Id}'.");
                }
                else if (!blockIds.Add(block.Id))
                {
                    errors.Add($"Content '{item.Id}' has duplicate block id '{block.Id}'.");
                }

                if (block.Kind is ContentBlockKind.Section or ContentBlockKind.Link or ContentBlockKind.Article
                    && string.IsNullOrWhiteSpace(block.Title))
                {
                    errors.Add($"Content '{item.Id}' block '{block.Id}' has no title.");
                }

                if (block.Kind is ContentBlockKind.Image or ContentBlockKind.Link
                    || block.Kind == ContentBlockKind.Article && !string.IsNullOrWhiteSpace(block.Url))
                {
                    ValidatePublicHttpsUrl(block.Url ?? string.Empty, $"Content '{item.Id}' block '{block.Id}' has unsafe URL", errors);
                }

                if (block.Translations is null)
                {
                    continue;
                }

                foreach (var language in block.Translations.Keys)
                {
                    if (!AnthologyLanguages.IsSupported(language))
                    {
                        errors.Add($"Content '{item.Id}' block '{block.Id}' has unsupported translation '{language}'.");
                    }
                }
            }

            if (item.Download is not null)
            {
                if (string.IsNullOrWhiteSpace(item.Download.FileName)
                    || !string.Equals(Path.GetFileName(item.Download.FileName), item.Download.FileName, StringComparison.Ordinal))
                {
                    errors.Add($"Content '{item.Id}' has an unsafe download file name.");
                }

                if (item.Download.Size <= 0 || !Sha256Regex().IsMatch(item.Download.Sha256))
                {
                    errors.Add($"Content '{item.Id}' has invalid download metadata.");
                }

                if (!string.IsNullOrWhiteSpace(item.Download.InstallName)
                    && (!string.Equals(Path.GetFileName(item.Download.InstallName), item.Download.InstallName, StringComparison.Ordinal)
                        || item.Download.InstallName is "." or ".."))
                {
                    errors.Add($"Content '{item.Id}' has an unsafe MO2 install name.");
                }

                if (item.Download.Mirrors.Count == 0)
                {
                    errors.Add($"Content '{item.Id}' download has no mirrors.");
                }

                foreach (var mirror in item.Download.Mirrors)
                {
                    var localFile = string.Equals(mirror.Provider, "local-file", StringComparison.OrdinalIgnoreCase);
                    if (string.IsNullOrWhiteSpace(mirror.Provider)
                        || !Uri.TryCreate(mirror.Url, UriKind.Absolute, out var uri)
                        || (localFile
                            ? !uri.IsFile
                            : uri.Scheme != Uri.UriSchemeHttps && !IsLocalDevelopmentUri(uri)))
                    {
                        errors.Add($"Content '{item.Id}' has unsafe download URL '{mirror.Url}'.");
                    }
                }
            }
        }
    }

    private static void ValidatePublicHttpsUrl(string value, string prefix, List<string> errors)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            errors.Add($"{prefix} '{value}'.");
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
