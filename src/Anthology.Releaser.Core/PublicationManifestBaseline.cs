using System.Security.Cryptography;
using System.Text.Json;
using Anthology.Contracts;
using Anthology.Update.Core;

namespace Anthology.Releaser.Core;

internal static class PublicationManifestBaseline
{
    public static async Task<SignedUpdateManifest?> LoadAsync(
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(machine);
        _ = ReleaserMachinePathNormalizer.Normalize(machine);

        var outputRoot = Path.GetFullPath(machine.OutputRoot);
        var versionManifestPath = Path.Combine(outputRoot, workspace.Version.Trim(), "manifest.json");
        var versionManifest = await LoadExistingAsync(versionManifestPath, cancellationToken);
        if (versionManifest is not null)
        {
            await ValidateAsync(
                versionManifest,
                workspace,
                machine,
                requireCurrentVersion: true,
                cancellationToken);
            return versionManifest;
        }

        var workspacePublicationRoots = (workspace.Mirrors ?? [])
                .Where(mirror => machine.PublicationRoots.TryGetValue(mirror.Id, out var root)
                                 && !string.IsNullOrWhiteSpace(root))
                .Select(mirror => machine.PublicationRoots[mirror.Id]);
        var publicationRoots = new[] { outputRoot }
            .Concat(workspacePublicationRoots)
            .Concat(machine.PublicationRoots.Values)
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var configuredStableManifest = ReleaseChannelLayout.GetStableManifestRelativePath(workspace);
        var stableRelativePaths = new[] { configuredStableManifest }
            // During the one-time transition the dedicated channel does not exist
            // yet. The signed schema 4 root manifest is a valid bootstrap baseline.
            .Concat(configuredStableManifest.Equals(
                ReleaseChannelLayout.ManifestFileName,
                StringComparison.OrdinalIgnoreCase)
                ? []
                : [ReleaseChannelLayout.ManifestFileName])
            .ToArray();
        foreach (var stableRelativePath in stableRelativePaths)
        {
            var invalidCandidates = new List<Exception>();
            foreach (var candidatePath in publicationRoots
                         .Select(root => PathSafety.ResolveUnderRoot(root, stableRelativePath))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var candidate = await LoadExistingAsync(candidatePath, cancellationToken);
                    if (candidate is null)
                    {
                        continue;
                    }

                    await ValidateAsync(
                        candidate,
                        workspace,
                        machine,
                        requireCurrentVersion: false,
                        cancellationToken);
                    return candidate;
                }
                catch (Exception exception) when (exception is JsonException
                                                   or InvalidDataException
                                                   or CryptographicException)
                {
                    invalidCandidates.Add(new InvalidDataException(
                        $"Invalid signed publication baseline: {candidatePath}",
                        exception));
                }
            }

            // The root manifest is only a bootstrap fallback while a dedicated
            // channel does not exist yet. If a dedicated manifest exists but is
            // corrupt, never silently downgrade the next publication to schema 4.
            if (invalidCandidates.Count > 0)
            {
                throw new InvalidDataException(
                    "No valid signed publication baseline was found. Existing manifests were left untouched.",
                    new AggregateException(invalidCandidates));
            }
        }

        return null;
    }

    public static PublicationManifestShape ResolveShape(
        SignedUpdateManifest? baselineManifest,
        IEnumerable<PackageManifest> packages,
        string? requestedMinimumLauncherVersion = null)
    {
        var hasLoosePackages = packages.Any(package => package.LooseFiles is not null);
        var schemaVersion = baselineManifest?.Payload.SchemaVersion == 5 || hasLoosePackages ? 5 : 4;
        var minimumLauncherVersion = PreserveMinimumLauncherVersion(
            baselineManifest?.Payload.MinimumLauncherVersion,
            requestedMinimumLauncherVersion);
        if (hasLoosePackages && string.IsNullOrWhiteSpace(minimumLauncherVersion))
        {
            throw new InvalidDataException(
                "A schema 5 loose-file publication requires a baseline or explicitly requested minimum launcher version.");
        }

        return new PublicationManifestShape(schemaVersion, minimumLauncherVersion);
    }

    public static string? PreserveMinimumLauncherVersion(
        string? baselineMinimumLauncherVersion,
        string? requestedMinimumLauncherVersion)
    {
        var baseline = string.IsNullOrWhiteSpace(baselineMinimumLauncherVersion)
            ? null
            : baselineMinimumLauncherVersion.Trim();
        var requested = string.IsNullOrWhiteSpace(requestedMinimumLauncherVersion)
            ? null
            : requestedMinimumLauncherVersion.Trim();
        if (baseline is null)
        {
            return requested;
        }
        if (requested is null || requested.Equals(baseline, StringComparison.OrdinalIgnoreCase))
        {
            return baseline;
        }

        return TryCompareSemanticVersions(requested, baseline, out var comparison)
               && comparison > 0
            ? requested
            : baseline;
    }

    public static async Task ValidateAsync(
        SignedUpdateManifest manifest,
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        bool requireCurrentVersion,
        CancellationToken cancellationToken = default)
    {
        ManifestValidator.ValidateAndThrow(manifest);
        var expectedChannel = string.IsNullOrWhiteSpace(workspace.Channel)
            ? "next"
            : workspace.Channel.Trim().ToLowerInvariant();
        if (!manifest.Payload.Channel.Equals(expectedChannel, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Базовый manifest.json относится к каналу {manifest.Payload.Channel}, ожидался {expectedChannel}.");
        }
        if (requireCurrentVersion
            && !manifest.Payload.Version.Equals(workspace.Version.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Manifest в папке версии относится к {manifest.Payload.Version}, ожидалась {workspace.Version.Trim()}.");
        }
        if (!manifest.Signature.KeyId.Equals(machine.KeyId.Trim(), StringComparison.Ordinal))
        {
            throw new CryptographicException(
                "Базовый manifest.json подписан другим идентификатором ключа.");
        }

        using var privateKey = ECDsa.Create();
        privateKey.ImportFromPem(await File.ReadAllTextAsync(
            Path.GetFullPath(machine.PrivateKeyPath),
            cancellationToken));
        if (!ManifestSecurity.Verify(manifest, privateKey))
        {
            throw new CryptographicException(
                "Подпись базового manifest.json не прошла проверку текущим ключом релизера.");
        }
    }

    private static async Task<SignedUpdateManifest?> LoadExistingAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        await using var stream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(
                   stream,
                   ManifestJson.Options,
                   cancellationToken)
               ?? throw new InvalidDataException("Existing manifest.json is empty or damaged.");
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool TryCompareSemanticVersions(string left, string right, out int comparison)
    {
        comparison = 0;
        if (!TryParseSemanticVersion(left, out var leftCore, out var leftPrerelease)
            || !TryParseSemanticVersion(right, out var rightCore, out var rightPrerelease))
        {
            return false;
        }

        var coreLength = Math.Max(leftCore.Length, rightCore.Length);
        for (var index = 0; index < coreLength; index++)
        {
            var leftPart = index < leftCore.Length ? leftCore[index] : 0;
            var rightPart = index < rightCore.Length ? rightCore[index] : 0;
            comparison = leftPart.CompareTo(rightPart);
            if (comparison != 0)
            {
                return true;
            }
        }

        if (leftPrerelease.Length == 0 || rightPrerelease.Length == 0)
        {
            comparison = leftPrerelease.Length == rightPrerelease.Length
                ? 0
                : leftPrerelease.Length == 0 ? 1 : -1;
            return true;
        }

        var prereleaseLength = Math.Min(leftPrerelease.Length, rightPrerelease.Length);
        for (var index = 0; index < prereleaseLength; index++)
        {
            var leftPart = leftPrerelease[index];
            var rightPart = rightPrerelease[index];
            var leftNumeric = ulong.TryParse(leftPart, out var leftNumber);
            var rightNumeric = ulong.TryParse(rightPart, out var rightNumber);
            comparison = leftNumeric && rightNumeric
                ? leftNumber.CompareTo(rightNumber)
                : leftNumeric != rightNumeric
                    ? leftNumeric ? -1 : 1
                    : string.Compare(leftPart, rightPart, StringComparison.OrdinalIgnoreCase);
            if (comparison != 0)
            {
                return true;
            }
        }

        comparison = leftPrerelease.Length.CompareTo(rightPrerelease.Length);
        return true;
    }

    private static bool TryParseSemanticVersion(
        string value,
        out ulong[] core,
        out string[] prerelease)
    {
        core = [];
        prerelease = [];
        var withoutBuild = value.Split('+', 2)[0];
        var pieces = withoutBuild.Split('-', 2);
        var coreParts = pieces[0].Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (coreParts.Length == 0)
        {
            return false;
        }

        core = new ulong[coreParts.Length];
        for (var index = 0; index < coreParts.Length; index++)
        {
            if (!ulong.TryParse(coreParts[index], out core[index]))
            {
                core = [];
                return false;
            }
        }

        if (pieces.Length == 2)
        {
            prerelease = pieces[1].Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (prerelease.Length == 0)
            {
                return false;
            }
        }
        return true;
    }
}

internal sealed record PublicationManifestShape(int SchemaVersion, string? MinimumLauncherVersion);
