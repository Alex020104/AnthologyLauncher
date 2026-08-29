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

    public string? SetupExecutable { get; set; }

    public string? SelectedMo2Profile { get; set; }

    public string? SelectedMo2Executable { get; set; }

    public string ManifestSource { get; set; } = string.Empty;

    public string PublicKeyPath { get; set; } = string.Empty;

    public string UpdateChannel { get; set; } = "next";

    public string PreferredMirrorProvider { get; set; } = "auto";

    public string CommunityApiUrl { get; set; } = "http://127.0.0.1:5249";

    public string CommunityNickname { get; set; } = $"Stalker-{Random.Shared.Next(1000, 9999)}";

    public string UserId { get; set; } = $"local-{Guid.NewGuid():N}";

    public string Renderer { get; set; } = DefaultRenderer;

    public bool DebugMode { get; set; }

    public bool SoundWorkaround { get; set; }

    public bool PrefetchSounds { get; set; } = true;

    public bool ResetUserLtx { get; set; }

    public bool UseAvx { get; set; } = true;

    public bool RelayChatAlways { get; set; } = true;

    public string RelayChatChannel { get; set; } = "#cocrc_slavik";

    public bool RelayChatAutoFaction { get; set; } = true;

    public string RelayChatFaction { get; set; } = "actor_stalker";

    public bool RelayChatShowTimestamps { get; set; } = true;

    public bool RelayChatSendDeaths { get; set; } = true;

    public bool RelayChatReceiveDeaths { get; set; } = true;

    public int RelayChatDeathInterval { get; set; } = 90;

    public int RelayChatNewsDuration { get; set; } = 10;

    public string RelayChatKey { get; set; } = "RETURN";

    public bool RelayChatNewsSound { get; set; } = true;

    public bool RelayChatCloseAfterSend { get; set; } = true;

    public string InterfaceLanguage { get; set; } = "ru";

    public int ShadowMapSize { get; set; } = 1536;

    public List<BugReportReference> BugReports { get; set; } = [];

    public LauncherSettings Copy() => new()
    {
        GameRoot = GameRoot,
        ModpackRoot = ModpackRoot,
        InstallDestination = InstallDestination,
        SetupExecutable = SetupExecutable,
        SelectedMo2Profile = SelectedMo2Profile,
        SelectedMo2Executable = SelectedMo2Executable,
        ManifestSource = ManifestSource,
        PublicKeyPath = PublicKeyPath,
        UpdateChannel = UpdateChannel,
        PreferredMirrorProvider = PreferredMirrorProvider,
        CommunityApiUrl = CommunityApiUrl,
        CommunityNickname = CommunityNickname,
        UserId = UserId,
        Renderer = Renderer,
        DebugMode = DebugMode,
        SoundWorkaround = SoundWorkaround,
        PrefetchSounds = PrefetchSounds,
        ResetUserLtx = ResetUserLtx,
        UseAvx = UseAvx,
        RelayChatAlways = RelayChatAlways,
        RelayChatChannel = RelayChatChannel,
        RelayChatAutoFaction = RelayChatAutoFaction,
        RelayChatFaction = RelayChatFaction,
        RelayChatShowTimestamps = RelayChatShowTimestamps,
        RelayChatSendDeaths = RelayChatSendDeaths,
        RelayChatReceiveDeaths = RelayChatReceiveDeaths,
        RelayChatDeathInterval = RelayChatDeathInterval,
        RelayChatNewsDuration = RelayChatNewsDuration,
        RelayChatKey = RelayChatKey,
        RelayChatNewsSound = RelayChatNewsSound,
        RelayChatCloseAfterSend = RelayChatCloseAfterSend,
        InterfaceLanguage = InterfaceLanguage,
        ShadowMapSize = ShadowMapSize,
        BugReports = (BugReports ?? []).Select(report => report.Copy()).ToList(),
    };
}

public sealed class BugReportReference
{
    public string Id { get; set; } = string.Empty;

    public string AccessToken { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public BugReportReference Copy() => new()
    {
        Id = Id,
        AccessToken = AccessToken,
        Title = Title,
        CreatedAt = CreatedAt,
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
                ApplyEnvironmentOverrides(Current);
                ApplyBundledUpdateConfiguration(Current);
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
            ApplyEnvironmentOverrides(Current);
            ApplyBundledUpdateConfiguration(Current);
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
        settings.SetupExecutable = NormalizeOptionalPath(settings.SetupExecutable);
        settings.SelectedMo2Profile = NormalizeOptionalText(settings.SelectedMo2Profile);
        settings.SelectedMo2Executable = NormalizeOptionalText(settings.SelectedMo2Executable);
        settings.PublicKeyPath = NormalizeOptionalPath(settings.PublicKeyPath) ?? string.Empty;
        settings.ManifestSource = settings.ManifestSource.Trim();
        settings.UpdateChannel = string.IsNullOrWhiteSpace(settings.UpdateChannel)
            ? "next"
            : settings.UpdateChannel.Trim().ToLowerInvariant();
        settings.PreferredMirrorProvider = settings.PreferredMirrorProvider.Trim().ToLowerInvariant() is
            "github" or "yandex-disk" or "google-drive" or "http" or "auto"
                ? settings.PreferredMirrorProvider.Trim().ToLowerInvariant()
                : "auto";
        settings.CommunityApiUrl = NormalizeHttpUrl(settings.CommunityApiUrl, "http://127.0.0.1:5249");
        settings.CommunityNickname = string.IsNullOrWhiteSpace(settings.CommunityNickname)
            ? $"Stalker-{Random.Shared.Next(1000, 9999)}"
            : settings.CommunityNickname.Trim();
        settings.UserId = string.IsNullOrWhiteSpace(settings.UserId)
            ? $"local-{Guid.NewGuid():N}"
            : settings.UserId.Trim();
        settings.Renderer = settings.Renderer.ToUpperInvariant() is "DX11" or "DX10" or "DX9" or "DX8"
            ? settings.Renderer.ToUpperInvariant()
            : "DX11";
        settings.InterfaceLanguage = AnthologyLanguages.IsSupported(settings.InterfaceLanguage)
            ? AnthologyLanguages.Normalize(settings.InterfaceLanguage)
            : "ru";
        settings.RelayChatChannel = settings.RelayChatChannel.Trim().ToLowerInvariant() is
            "#cocrc_english" or "#cocrc_english_rp" or "#cocrc_slavik"
                ? settings.RelayChatChannel.Trim().ToLowerInvariant()
                : "#cocrc_slavik";
        settings.RelayChatFaction = settings.RelayChatFaction.Trim().ToLowerInvariant() is
            "actor_bandit" or "actor_csky" or "actor_dolg" or "actor_ecolog" or "actor_freedom"
            or "actor_stalker" or "actor_killer" or "actor_army" or "actor_monolith" or "actor_renegade"
                ? settings.RelayChatFaction.Trim().ToLowerInvariant()
                : "actor_stalker";
        settings.RelayChatDeathInterval = Math.Clamp(settings.RelayChatDeathInterval, 0, 3600);
        settings.RelayChatNewsDuration = Math.Clamp(settings.RelayChatNewsDuration, 1, 60);
        settings.RelayChatKey = string.IsNullOrWhiteSpace(settings.RelayChatKey)
            ? "RETURN"
            : settings.RelayChatKey.Trim().ToUpperInvariant().Replace("DIK_", string.Empty, StringComparison.OrdinalIgnoreCase);
        settings.ShadowMapSize = settings.ShadowMapSize is 1536 or 2048 or 2560 or 3072 or 4096
            ? settings.ShadowMapSize
            : 1536;
        settings.BugReports = (settings.BugReports ?? [])
            .Where(report => !string.IsNullOrWhiteSpace(report.Id)
                             && !string.IsNullOrWhiteSpace(report.AccessToken))
            .GroupBy(report => report.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(report => report.CreatedAt)
            .Take(100)
            .Select(report => new BugReportReference
            {
                Id = report.Id.Trim(),
                AccessToken = report.AccessToken.Trim(),
                Title = report.Title?.Trim() ?? string.Empty,
                CreatedAt = report.CreatedAt,
            })
            .ToList();
    }

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path.Trim());

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ApplyEnvironmentOverrides(LauncherSettings settings)
    {
        var gameRoot = Environment.GetEnvironmentVariable("ANTHOLOGY_GAME_ROOT");
        if (!string.IsNullOrWhiteSpace(gameRoot))
        {
            var fullPath = Path.GetFullPath(gameRoot);
            if (File.Exists(Path.Combine(fullPath, "fsgame.ltx"))
                && Directory.Exists(Path.Combine(fullPath, "bin")))
            {
                settings.GameRoot = fullPath;
            }
        }

        var modpackRoot = Environment.GetEnvironmentVariable("ANTHOLOGY_MO2_ROOT");
        if (!string.IsNullOrWhiteSpace(modpackRoot))
        {
            var fullPath = Path.GetFullPath(modpackRoot);
            if (File.Exists(Path.Combine(fullPath, "ModOrganizer.exe")))
            {
                settings.ModpackRoot = fullPath;
            }
        }

        var communityApi = Environment.GetEnvironmentVariable("ANTHOLOGY_COMMUNITY_API");
        if (!string.IsNullOrWhiteSpace(communityApi))
        {
            settings.CommunityApiUrl = NormalizeHttpUrl(communityApi, settings.CommunityApiUrl);
        }

        var manifestSource = Environment.GetEnvironmentVariable("ANTHOLOGY_MANIFEST_SOURCE");
        if (!string.IsNullOrWhiteSpace(manifestSource))
        {
            settings.ManifestSource = manifestSource.Trim();
        }
    }

    private static void ApplyBundledUpdateConfiguration(LauncherSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.PublicKeyPath)
            || !File.Exists(settings.PublicKeyPath))
        {
            var bundledKey = FindFirstExistingFile(
                Path.Combine(AppContext.BaseDirectory, "TrustedKeys", "anthology.public.pem"),
                Path.Combine(GetDeploymentRoot(), "TrustedKeys", "anthology.public.pem"));
            if (bundledKey is not null)
            {
                settings.PublicKeyPath = bundledKey;
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.ManifestSource)
            && !IsManagedLocalManifest(settings.ManifestSource))
        {
            return;
        }

        foreach (var descriptorPath in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "Update", "channel.json"),
                     Path.Combine(GetDeploymentRoot(), "Update", "channel.json"),
                 })
        {
            var configuredSource = ReadManifestSource(descriptorPath);
            if (!string.IsNullOrWhiteSpace(configuredSource))
            {
                settings.ManifestSource = configuredSource;
                return;
            }
        }

        var localManifest = FindFirstExistingFile(
            Path.Combine(AppContext.BaseDirectory, "Update", "manifest.json"),
            Path.Combine(AppContext.BaseDirectory, "manifest.json"),
            Path.Combine(GetDeploymentRoot(), "Update", "manifest.json"),
            Path.Combine(GetDeploymentRoot(), "manifest.json"));
        if (localManifest is not null)
        {
            settings.ManifestSource = localManifest;
        }
    }

    private static string? ReadManifestSource(string descriptorPath)
    {
        if (!File.Exists(descriptorPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(descriptorPath));
            var source = document.RootElement.TryGetProperty("manifestSource", out var node)
                ? node.GetString()?.Trim()
                : null;
            if (string.IsNullOrWhiteSpace(source))
            {
                return null;
            }
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return uri.AbsoluteUri;
            }
            return Path.GetFullPath(source, Path.GetDirectoryName(descriptorPath)!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string GetDeploymentRoot() =>
        Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName
        ?? AppContext.BaseDirectory;

    private static bool IsManagedLocalManifest(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            return false;
        }

        try
        {
            var sourcePath = uri?.LocalPath ?? Path.GetFullPath(source);
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Update", "manifest.json"),
                Path.Combine(AppContext.BaseDirectory, "manifest.json"),
                Path.Combine(GetDeploymentRoot(), "Update", "manifest.json"),
                Path.Combine(GetDeploymentRoot(), "manifest.json"),
            };
            return candidates.Any(candidate => string.Equals(
                Path.GetFullPath(candidate),
                Path.GetFullPath(sourcePath),
                StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string? FindFirstExistingFile(params string[] candidates) =>
        candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);

    private static string NormalizeHttpUrl(string? value, string fallback)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }
        return uri.AbsoluteUri.TrimEnd('/');
    }

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
