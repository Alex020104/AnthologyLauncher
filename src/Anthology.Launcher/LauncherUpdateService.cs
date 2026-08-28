using Anthology.Update.Core;
using System.Net.Http;

namespace Anthology.Launcher;

public sealed class LauncherUpdateService(
    HttpClient httpClient,
    LauncherSettingsStore settingsStore)
{
    private readonly UpdateCoordinator _coordinator = new(httpClient);

    public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.Current;
        return _coordinator.CheckAsync(
            settings.ManifestSource,
            settings.PublicKeyPath,
            settings.UpdateChannel,
            settingsStore.UpdaterStateRoot,
            cancellationToken);
    }

    public Task<UpdateApplyResult> ApplyAsync(
        UpdateCheckResult check,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.Current;
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddRoots(roots, settings.GameRoot, "game", "engine", "database");
        AddRoots(roots, settings.ModpackRoot, "modpack", "mods", "tools");
        return _coordinator.ApplyAsync(
            check,
            roots,
            settingsStore.UpdaterStateRoot,
            progress,
            cancellationToken);
    }

    private static void AddRoots(
        Dictionary<string, string> roots,
        string? path,
        params string[] names)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        foreach (var name in names)
        {
            roots[name] = path;
        }
    }
}
