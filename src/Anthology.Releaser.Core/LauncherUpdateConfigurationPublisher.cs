using Anthology.Contracts;

namespace Anthology.Releaser.Core;

/// <summary>
/// Keeps the integrated launcher connected to the signed update channel produced
/// by the releaser. Only public material is copied into the launcher.
/// </summary>
public static class LauncherUpdateConfigurationPublisher
{
    private const string LauncherDirectoryName = "AnthologyLauncher";

    public static string? ResolveStableManifestSource(ReleaserWorkspace workspace) =>
        workspace.Mirrors
            // The manifest is tiny and GitHub raw is more responsive than a
            // public Yandex.Disk download. Large artifacts still use their own
            // ordered mirror lists from the signed manifest.
            .OrderBy(mirror => string.Equals(mirror.Provider, "github", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(mirror => mirror.Priority)
            .Select(mirror => mirror.ManifestUrl?.Trim())
            .FirstOrDefault(IsHttpAddress);

    public static async Task<LauncherUpdatePreparationResult> PrepareAsync(
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(machine);

        var launcherRoot = FindLauncherRoot(machine.GameSourceRoot);
        if (launcherRoot is null)
        {
            return new LauncherUpdatePreparationResult(false, false, null, null);
        }

        var keyCopied = await CopyPublicKeyAsync(launcherRoot, machine.PublicKeyPath, cancellationToken);
        var manifestSource = ResolveStableManifestSource(workspace);
        string? descriptorPath = null;
        if (manifestSource is not null)
        {
            descriptorPath = Path.Combine(launcherRoot, "Update", "channel.json");
            await UnifiedReleaseBuilder.WriteJsonAtomicallyAsync(
                descriptorPath,
                new LauncherUpdateChannel(1, manifestSource, NormalizeChannel(workspace.Channel)),
                cancellationToken);
            progress?.Report("Лаунчер подключён к постоянному подписанному каналу обновлений.");
        }
        else
        {
            progress?.Report("Публичный ключ добавлен в лаунчер. Для автоматического онлайн-канала укажите постоянный manifest URL в «Источниках».");
        }

        return new LauncherUpdatePreparationResult(true, keyCopied, descriptorPath, manifestSource);
    }

    public static async Task<bool> UpdateLocalManifestAsync(
        string manifestPath,
        ReleaserMachineSettings machine,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        var launcherRoot = FindLauncherRoot(machine.GameSourceRoot);
        if (launcherRoot is null)
        {
            return false;
        }

        await CopyFileAtomicallyAsync(
            manifestPath,
            Path.Combine(launcherRoot, "Update", "manifest.json"),
            cancellationToken);
        await CopyPublicKeyAsync(launcherRoot, machine.PublicKeyPath, cancellationToken);
        return true;
    }

    public static async Task<bool> RemoveLocalManifestAsync(
        ReleaserMachineSettings machine,
        string trashRoot,
        CancellationToken cancellationToken = default)
    {
        var launcherRoot = FindLauncherRoot(machine.GameSourceRoot);
        if (launcherRoot is null)
        {
            return false;
        }

        var manifestPath = Path.Combine(launcherRoot, "Update", "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        var destination = Path.Combine(trashRoot, "launcher", "manifest.json");
        await CopyFileAtomicallyAsync(manifestPath, destination, cancellationToken);
        File.Delete(manifestPath);
        return true;
    }

    private static string? FindLauncherRoot(string? gameSourceRoot)
    {
        if (string.IsNullOrWhiteSpace(gameSourceRoot))
        {
            return null;
        }

        var gameRoot = Path.GetFullPath(gameSourceRoot);
        var candidates = new[]
        {
            Path.Combine(gameRoot, LauncherDirectoryName),
            gameRoot,
        };
        return candidates.FirstOrDefault(candidate =>
            File.Exists(Path.Combine(candidate, "App", "AnthologyLauncher.Next.exe"))
            || Directory.Exists(Path.Combine(candidate, "App", "TrustedKeys")));
    }

    private static async Task<bool> CopyPublicKeyAsync(
        string launcherRoot,
        string? publicKeyPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPath) || !File.Exists(publicKeyPath))
        {
            return false;
        }

        await CopyFileAtomicallyAsync(
            publicKeyPath,
            Path.Combine(launcherRoot, "App", "TrustedKeys", "anthology.public.pem"),
            cancellationToken);
        return true;
    }

    private static async Task CopyFileAtomicallyAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        var sourcePath = Path.GetFullPath(source);
        var destinationPath = Path.GetFullPath(destination);
        if (PathsEqual(sourcePath, destinationPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temporary = destinationPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var input = new FileStream(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            File.Move(temporary, destinationPath, true);
        }
        catch
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
            throw;
        }
    }

    private static bool IsHttpAddress(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeChannel(string? channel) =>
        string.IsNullOrWhiteSpace(channel) ? "next" : channel.Trim().ToLowerInvariant();

    private sealed record LauncherUpdateChannel(int SchemaVersion, string ManifestSource, string Channel);
}

public sealed record LauncherUpdatePreparationResult(
    bool LauncherFound,
    bool PublicKeyCopied,
    string? DescriptorPath,
    string? ManifestSource);
