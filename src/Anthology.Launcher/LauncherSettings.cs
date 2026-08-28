using System.IO;
using System.Text.Json;
using Anthology.Contracts;

namespace Anthology.Launcher;

public sealed class LauncherSettings
{
    private const string DefaultRenderer = "DX11";

    public string? GameRoot { get; set; }

    public string? ModpackRoot { get; set; }

    public string? InstallDestination { get; set; }

    public string ManifestSource { get; set; } = string.Empty;

    public string PublicKeyPath { get; set; } = string.Empty;

    public string UpdateChannel { get; set; } = "next";

    public string CommunityNickname { get; set; } = $"Stalker-{Random.Shared.Next(1000, 9999)}";

    public string UserId { get; set; } = $"local-{Guid.NewGuid():N}";

    public string Renderer { get; set; } = DefaultRenderer;

    public bool DebugMode { get; set; }

    public bool SoundWorkaround { get; set; }

    public bool PrefetchSounds { get; set; } = true;

    public bool ResetUserLtx { get; set; }

    public bool UseAvx { get; set; } = true;

    public bool RelayChatAlways { get; set; } = true;

    public int ShadowMapSize { get; set; } = 1536;

    public LauncherSettings Copy() => new()
    {
        GameRoot = GameRoot,
        ModpackRoot = ModpackRoot,
        InstallDestination = InstallDestination,
        ManifestSource = ManifestSource,
        PublicKeyPath = PublicKeyPath,
        UpdateChannel = UpdateChannel,
        CommunityNickname = CommunityNickname,
        UserId = UserId,
        Renderer = Renderer,
        DebugMode = DebugMode,
        SoundWorkaround = SoundWorkaround,
        PrefetchSounds = PrefetchSounds,
        ResetUserLtx = ResetUserLtx,
        UseAvx = UseAvx,
        RelayChatAlways = RelayChatAlways,
        ShadowMapSize = ShadowMapSize,
    };
}

public sealed class LauncherSettingsStore : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _settingsPath;

    public LauncherSettingsStore()
    {
        DataRoot = ResolveDataRoot();
        _settingsPath = Path.Combine(DataRoot, "settings.json");
    }

    public string DataRoot { get; }

    public string UpdaterStateRoot => Path.Combine(DataRoot, "Updater");

    public LauncherSettings Current { get; private set; } = new();

    public async Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_settingsPath))
            {
                Directory.CreateDirectory(DataRoot);
                await WriteAsync(Current, cancellationToken);
                return Current.Copy();
            }

            await using var stream = new FileStream(
                _settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                32 * 1024,
                FileOptions.Asynchronous);
            Current = await JsonSerializer.DeserializeAsync<LauncherSettings>(
                stream,
                ManifestJson.Options,
                cancellationToken) ?? new LauncherSettings();
            Normalize(Current);
            return Current.Copy();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var copy = settings.Copy();
            Normalize(copy);
            Directory.CreateDirectory(DataRoot);
            await WriteAsync(copy, cancellationToken);
            Current = copy;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task WriteAsync(LauncherSettings settings, CancellationToken cancellationToken)
    {
        var temporary = _settingsPath + $".tmp-{Guid.NewGuid():N}";
        await using (var stream = new FileStream(
                         temporary,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         32 * 1024,
                         FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, settings, ManifestJson.Options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporary, _settingsPath, true);
    }

    private static void Normalize(LauncherSettings settings)
    {
        settings.GameRoot = NormalizeOptionalPath(settings.GameRoot);
        settings.ModpackRoot = NormalizeOptionalPath(settings.ModpackRoot);
        settings.InstallDestination = NormalizeOptionalPath(settings.InstallDestination);
        settings.PublicKeyPath = NormalizeOptionalPath(settings.PublicKeyPath) ?? string.Empty;
        settings.ManifestSource = settings.ManifestSource.Trim();
        settings.UpdateChannel = string.IsNullOrWhiteSpace(settings.UpdateChannel)
            ? "next"
            : settings.UpdateChannel.Trim().ToLowerInvariant();
        settings.CommunityNickname = string.IsNullOrWhiteSpace(settings.CommunityNickname)
            ? $"Stalker-{Random.Shared.Next(1000, 9999)}"
            : settings.CommunityNickname.Trim();
        settings.UserId = string.IsNullOrWhiteSpace(settings.UserId)
            ? $"local-{Guid.NewGuid():N}"
            : settings.UserId.Trim();
        settings.Renderer = settings.Renderer.ToUpperInvariant() is "DX11" or "DX10" or "DX9" or "DX8"
            ? settings.Renderer.ToUpperInvariant()
            : "DX11";
        settings.ShadowMapSize = settings.ShadowMapSize is 1536 or 2048 or 2560 or 3072 or 4096
            ? settings.ShadowMapSize
            : 1536;
    }

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path.Trim());

    private static string ResolveDataRoot()
    {
        var configured = Environment.GetEnvironmentVariable("ANTHOLOGY_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AnthologyLauncherNext");
    }
}
