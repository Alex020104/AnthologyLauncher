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

        if (payload.SchemaVersion is not (1 or 2 or 3 or 4 or 5))
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

        if (payload.Packages.Any(package => package.LooseFiles is not null)
            && string.IsNullOrWhiteSpace(payload.MinimumLauncherVersion))
        {
            errors.Add("Loose-file delivery requires an explicit minimum launcher version.");
        }

        if (payload.Packages.Count == 0
            && (payload.SchemaVersion < 2 || payload.Content is null || payload.Content.Items.Count == 0))
        {
            errors.Add("At least one package or one content item is required.");
        }

        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in payload.Packages)
        {
            ValidatePackage(package, payload.SchemaVersion, packageIds, errors);
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
        int schemaVersion,
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

        if (string.IsNullOrWhiteSpace(package.DisplayName))
        {
            errors.Add($"Package '{package.Id}' has no display name.");
        }

        if (string.IsNullOrWhiteSpace(package.Version))
        {
            errors.Add($"Package '{package.Id}' has no version.");
        }

        if (!Sha256Regex().IsMatch(package.Sha256))
        {
            errors.Add($"Package '{package.Id}' has invalid SHA-256.");
        }

        var filePaths = package.LooseFiles is null
            ? ValidateArchivePackage(package, errors)
            : ValidateLoosePackage(package, schemaVersion, errors);
        if (filePaths.Count == 0
            && (package.DeletedFiles is null || package.DeletedFiles.Count == 0)
            && (package.DeletedDirectories is null || package.DeletedDirectories.Count == 0))
        {
            errors.Add($"Package '{package.Id}' has no files and no deletion paths.");
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

        var deletedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (schemaVersion < 4 && package.DeletedDirectories is { Count: > 0 })
        {
            errors.Add($"Package '{package.Id}' requires schema version 4 for directory deletion paths.");
        }
        foreach (var path in package.DeletedDirectories ?? [])
        {
            try
            {
                var normalized = PathSafety.NormalizeRelativePath(path);
                if (!deletedDirectories.Add(normalized))
                {
                    errors.Add($"Package '{package.Id}' contains duplicate directory deletion path '{path}'.");
                }
            }
            catch (ArgumentException exception)
            {
                errors.Add($"Package '{package.Id}' has unsafe directory deletion path '{path}': {exception.Message}");
            }
        }

        foreach (var error in PackageInstallScopePolicy.Validate(package))
        {
            errors.Add(error);
        }
    }

    private static HashSet<string> ValidateArchivePackage(
        PackageManifest package,
        List<string> errors)
    {
        if (package.Size <= 0)
        {
            errors.Add($"Package '{package.Id}' has invalid size.");
        }
        if (!string.Equals(package.ArchiveFormat, "zip", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Package '{package.Id}' uses unsupported archive format '{package.ArchiveFormat}'.");
        }
        if (package.Mirrors.Count == 0)
        {
            errors.Add($"Package '{package.Id}' has no mirrors.");
        }
        foreach (var mirror in package.Mirrors)
        {
            ValidateMirror(package.Id, mirror, false, errors);
        }

        return ValidatePaths(package.Id, package.Files, errors);
    }

    private static HashSet<string> ValidateLoosePackage(
        PackageManifest package,
        int schemaVersion,
        List<string> errors)
    {
        if (schemaVersion < 5)
        {
            errors.Add($"Package '{package.Id}' requires schema version 5 for loose-file delivery.");
        }
        if (!string.Equals(package.ArchiveFormat, "loose", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Loose package '{package.Id}' must use archive format 'loose'.");
        }
        if (package.LooseFiles!.Count > 500_000)
        {
            errors.Add($"Loose package '{package.Id}' exceeds the 500000-file safety limit.");
        }
        if (package.Mirrors.Count > 16)
        {
            errors.Add($"Loose package '{package.Id}' has too many mirror templates.");
        }
        foreach (var mirror in package.Mirrors)
        {
            ValidateMirror(package.Id, mirror, true, errors);
        }

        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalSize = 0;
        var canComputePackageHash = true;
        foreach (var file in package.LooseFiles)
        {
            string? normalized = null;
            try
            {
                normalized = PathSafety.NormalizeRelativePath(file.Path);
                if (normalized.Length > 1024)
                {
                    errors.Add($"Loose package '{package.Id}' path is too long: '{file.Path}'.");
                    canComputePackageHash = false;
                }
                if (!filePaths.Add(normalized))
                {
                    errors.Add($"Package '{package.Id}' contains duplicate path '{file.Path}'.");
                    canComputePackageHash = false;
                }
            }
            catch (ArgumentException exception)
            {
                errors.Add($"Package '{package.Id}' has unsafe path '{file.Path}': {exception.Message}");
                canComputePackageHash = false;
            }

            if (file.Size < 0)
            {
                errors.Add($"Loose package '{package.Id}' file '{file.Path}' has invalid size.");
                canComputePackageHash = false;
            }
            else
            {
                try
                {
                    totalSize = checked(totalSize + file.Size);
                }
                catch (OverflowException)
                {
                    errors.Add($"Loose package '{package.Id}' total size overflows Int64.");
                    canComputePackageHash = false;
                }
            }
            if (!Sha256Regex().IsMatch(file.Sha256))
            {
                errors.Add($"Loose package '{package.Id}' file '{file.Path}' has invalid SHA-256.");
                canComputePackageHash = false;
            }
            if (file.Mirrors is { Count: > 16 })
            {
                errors.Add($"Loose package '{package.Id}' file '{file.Path}' has too many mirrors.");
            }
            foreach (var mirror in file.Mirrors ?? [])
            {
                ValidateMirror(package.Id, mirror, false, errors, normalized);
            }
            if (package.Mirrors.Count == 0 && file.Mirrors is not { Count: > 0 })
            {
                errors.Add($"Loose package '{package.Id}' file '{file.Path}' has no mirror.");
            }
        }

        if (package.Files.Count > 0)
        {
            var legacyPaths = ValidatePaths(package.Id, package.Files, errors);
            if (!legacyPaths.SetEquals(filePaths))
            {
                errors.Add($"Loose package '{package.Id}' legacy file list does not match loose-file metadata.");
            }
        }
        if (package.Size != totalSize)
        {
            errors.Add($"Loose package '{package.Id}' total size does not match its file table.");
        }
        if (canComputePackageHash)
        {
            try
            {
                var expectedHash = LoosePackageHash.ComputeSha256(package.LooseFiles);
                if (!string.Equals(expectedHash, package.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Loose package '{package.Id}' table SHA-256 does not match its file metadata.");
                }
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                errors.Add($"Loose package '{package.Id}' table hash cannot be computed: {exception.Message}");
            }
        }

        return filePaths;
    }

    private static HashSet<string> ValidatePaths(
        string packageId,
        IEnumerable<string> paths,
        List<string> errors)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            try
            {
                var normalized = PathSafety.NormalizeRelativePath(path);
                if (!result.Add(normalized))
                {
                    errors.Add($"Package '{packageId}' contains duplicate path '{path}'.");
                }
            }
            catch (ArgumentException exception)
            {
                errors.Add($"Package '{packageId}' has unsafe path '{path}': {exception.Message}");
            }
        }
        return result;
    }

    private static void ValidateMirror(
        string packageId,
        MirrorManifest mirror,
        bool requirePathTemplate,
        List<string> errors,
        string? filePath = null)
    {
        var label = filePath is null
            ? $"Package '{packageId}'"
            : $"Loose package '{packageId}' file '{filePath}'";
        if (string.IsNullOrWhiteSpace(mirror.Provider))
        {
            errors.Add($"{label} has a mirror without provider.");
        }

        if (string.IsNullOrWhiteSpace(mirror.Url))
        {
            errors.Add($"{label} has a mirror without URL.");
            return;
        }

        var containsTemplate = mirror.Url.Contains("{path}", StringComparison.OrdinalIgnoreCase);
        if (containsTemplate != requirePathTemplate)
        {
            errors.Add(requirePathTemplate
                ? $"{label} mirror '{mirror.Provider}' has no '{{path}}' placeholder."
                : $"{label} exact mirror '{mirror.Provider}' must not contain '{{path}}'.");
            return;
        }
        var resolvedUrl = requirePathTemplate
            ? mirror.Url.Replace("{path}", "probe/file.bin", StringComparison.OrdinalIgnoreCase)
            : mirror.Url;
        var localFile = string.Equals(mirror.Provider, "local-file", StringComparison.OrdinalIgnoreCase);
        var bundleFile = string.Equals(mirror.Provider, "bundle-file", StringComparison.OrdinalIgnoreCase);
        if (!Uri.TryCreate(resolvedUrl, UriKind.Absolute, out var uri)
            || (localFile
                ? !uri.IsFile
                : bundleFile
                    ? !string.Equals(uri.Scheme, "bundle", StringComparison.OrdinalIgnoreCase)
                      || !string.IsNullOrEmpty(uri.Query)
                      || !string.IsNullOrEmpty(uri.Fragment)
                      || !string.IsNullOrEmpty(uri.UserInfo)
                    : !IsWebUri(uri)))
        {
            errors.Add($"{label} has unsafe mirror URL '{mirror.Url}'.");
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
                ValidatePublicWebUrl(image, $"Content '{item.Id}' has unsafe image URL", errors);
            }

            foreach (var video in item.Videos)
            {
                ValidatePublicWebUrl(video.Url, $"Content '{item.Id}' has unsafe video URL", errors);
            }

            ValidateSocialLinks(item.AuthorLinks, $"Content '{item.Id}' author", errors);

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
                    ValidatePublicWebUrl(block.Url ?? string.Empty, $"Content '{item.Id}' block '{block.Id}' has unsafe URL", errors);
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
                            : !IsWebUri(uri)))
                    {
                        errors.Add($"Content '{item.Id}' has unsafe download URL '{mirror.Url}'.");
                    }
                }
            }
        }

        ValidateSocialLinks(content.SocialLinks, "Social", errors);

        var personIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var person in content.ProjectPeople ?? [])
        {
            if (string.IsNullOrWhiteSpace(person.Id) || !PackageIdRegex().IsMatch(person.Id))
            {
                errors.Add($"Project person has invalid id: '{person.Id}'.");
            }
            else if (!personIds.Add(person.Id))
            {
                errors.Add($"Duplicate project person id: '{person.Id}'.");
            }
            if (string.IsNullOrWhiteSpace(person.Name))
            {
                errors.Add($"Project person '{person.Id}' has no name.");
            }
            if (!string.IsNullOrWhiteSpace(person.ImageUrl))
            {
                ValidatePublicWebUrl(person.ImageUrl, $"Project person '{person.Id}' has unsafe image URL", errors);
            }
            ValidateSocialLinks(person.Links, $"Project person '{person.Id}'", errors);
            foreach (var language in person.Translations?.Keys ?? [])
            {
                if (!AnthologyLanguages.IsSupported(language))
                {
                    errors.Add($"Project person '{person.Id}' has unsupported translation '{language}'.");
                }
            }
        }

        var streamIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stream in content.LiveStreams ?? [])
        {
            if (string.IsNullOrWhiteSpace(stream.Id) || !PackageIdRegex().IsMatch(stream.Id))
            {
                errors.Add($"Live stream has invalid id: '{stream.Id}'.");
            }
            else if (!streamIds.Add(stream.Id))
            {
                errors.Add($"Duplicate live stream id: '{stream.Id}'.");
            }
            if (string.IsNullOrWhiteSpace(stream.Title))
            {
                errors.Add($"Live stream '{stream.Id}' has no title.");
            }
            ValidatePublicWebUrl(stream.Url, $"Live stream '{stream.Id}' has unsafe URL", errors);
            foreach (var language in stream.Translations?.Keys ?? [])
            {
                if (!AnthologyLanguages.IsSupported(language))
                {
                    errors.Add($"Live stream '{stream.Id}' has unsupported translation '{language}'.");
                }
            }
        }

        foreach (var language in content.Changelog?.Translations?.Keys ?? [])
        {
            if (!AnthologyLanguages.IsSupported(language))
            {
                errors.Add($"Release changelog has unsupported translation '{language}'.");
            }
        }
    }

    private static void ValidateSocialLinks(
        IReadOnlyList<SocialLink>? links,
        string label,
        List<string> errors)
    {
        var socialIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var social in links ?? [])
        {
            if (string.IsNullOrWhiteSpace(social.Id) || !PackageIdRegex().IsMatch(social.Id))
            {
                errors.Add($"{label} link has invalid id: '{social.Id}'.");
                continue;
            }
            if (!socialIds.Add(social.Id))
            {
                errors.Add($"{label} has duplicate link id: '{social.Id}'.");
            }
            if (string.IsNullOrWhiteSpace(social.Title))
            {
                errors.Add($"{label} link '{social.Id}' has no title.");
            }
            ValidatePublicWebUrl(social.Url, $"{label} link '{social.Id}' has unsafe URL", errors);
        }
    }

    private static void ValidatePublicWebUrl(string value, string prefix, List<string> errors)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !IsWebUri(uri)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            errors.Add($"{prefix} '{value}'.");
        }
    }

    private static bool IsWebUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,79}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageIdRegex();

    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}
