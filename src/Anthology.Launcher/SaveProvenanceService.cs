using System.IO;
using System.Text.Json;
using Anthology.Contracts;
using Anthology.Mo2.Core;

namespace Anthology.Launcher;

public enum SaveRuntimeOrigin
{
    Unknown,
    Original,
    ModOrganizer,
}

public sealed record SaveOriginResolution(
    SaveRuntimeOrigin Origin,
    string? Profile,
    string Label,
    string Description)
{
    public bool IsKnown => Origin != SaveRuntimeOrigin.Unknown;
}

public sealed class SaveProvenanceService(LauncherSettingsStore settingsStore)
{
    private readonly object _gate = new();
    private SaveProvenanceState? _state;

    public void BeginSession(string gameRoot, SaveRuntimeOrigin origin, string? profile = null)
    {
        if (origin == SaveRuntimeOrigin.Unknown || string.IsNullOrWhiteSpace(gameRoot))
        {
            return;
        }

        lock (_gate)
        {
            var state = LoadState();
            state.ActiveSession = new SaveSessionRecord
            {
                GameRoot = Path.GetFullPath(gameRoot),
                Origin = origin,
                Profile = Normalize(profile),
                StartedAtUtc = DateTime.UtcNow,
                Baseline = EnumerateSharedSaves(gameRoot)
                    .ToDictionary(save => NormalizePath(save.FullPath), Fingerprint, StringComparer.OrdinalIgnoreCase),
            };
            SaveState(state);
        }
    }

    public SaveOriginResolution Resolve(
        string gameRoot,
        Mo2SaveEntry save,
        string? mo2Root = null)
    {
        var localProfile = ResolveLocalMo2Profile(save.FullPath, mo2Root);
        if (localProfile is not null)
        {
            return Known(SaveRuntimeOrigin.ModOrganizer, localProfile);
        }

        lock (_gate)
        {
            var state = LoadState();
            var path = NormalizePath(save.FullPath);
            var fingerprint = Fingerprint(save);
            var session = state.ActiveSession;
            if (session is not null
                && PathEquals(session.GameRoot, gameRoot)
                && IsSharedSavePath(save.FullPath, gameRoot)
                && (!session.Baseline.TryGetValue(path, out var baseline)
                    || !string.Equals(baseline, fingerprint, StringComparison.Ordinal))
                && save.LastWriteTimeUtc >= session.StartedAtUtc.AddSeconds(-3))
            {
                var detected = new KnownSaveRecord
                {
                    Path = path,
                    Fingerprint = fingerprint,
                    Origin = session.Origin,
                    Profile = session.Profile,
                    DetectedAtUtc = DateTime.UtcNow,
                };
                state.Saves.RemoveAll(item => PathEquals(item.Path, path));
                state.Saves.Add(detected);
                TrimState(state);
                SaveState(state);
                return Known(detected.Origin, detected.Profile);
            }

            var known = state.Saves.LastOrDefault(item => PathEquals(item.Path, path));
            return known is null
                ? Unknown()
                : Known(known.Origin, known.Profile);
        }
    }

    public string LastSessionLabel(string? gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot))
        {
            return "режим не определён";
        }

        lock (_gate)
        {
            var session = LoadState().ActiveSession;
            if (session is null || !PathEquals(session.GameRoot, gameRoot))
            {
                return "режим ещё не определён";
            }

            return session.Origin == SaveRuntimeOrigin.ModOrganizer
                ? $"MO2 · {session.Profile ?? "профиль не указан"}"
                : "Оригинальная Anomaly";
        }
    }

    private SaveProvenanceState LoadState()
    {
        if (_state is not null)
        {
            return _state;
        }

        var path = StatePath;
        if (!File.Exists(path))
        {
            return _state = new SaveProvenanceState();
        }

        try
        {
            _state = JsonSerializer.Deserialize<SaveProvenanceState>(File.ReadAllText(path), ManifestJson.Options)
                     ?? new SaveProvenanceState();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _state = new SaveProvenanceState();
        }

        return _state;
    }

    private void SaveState(SaveProvenanceState state)
    {
        Directory.CreateDirectory(settingsStore.DataRoot);
        var temporary = StatePath + $".tmp-{Guid.NewGuid():N}";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, ManifestJson.Options));
        File.Move(temporary, StatePath, true);
        _state = state;
    }

    private string StatePath => Path.Combine(settingsStore.DataRoot, "save-provenance.json");

    private static IReadOnlyList<Mo2SaveEntry> EnumerateSharedSaves(string gameRoot) =>
        Mo2WorkspaceReader.ReadSaves(gameRoot);

    private static string Fingerprint(Mo2SaveEntry save) =>
        $"{save.Size}:{save.LastWriteTimeUtc.Ticks}:{save.HasScop}:{save.HasScoc}";

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ResolveLocalMo2Profile(string savePath, string? mo2Root)
    {
        if (string.IsNullOrWhiteSpace(mo2Root))
        {
            return null;
        }

        var profilesRoot = Path.Combine(Path.GetFullPath(mo2Root), "profiles");
        if (!Directory.Exists(profilesRoot))
        {
            return null;
        }

        foreach (var profileRoot in Directory.EnumerateDirectories(profilesRoot))
        {
            if (IsInsideDirectory(savePath, Path.Combine(profileRoot, "saves")))
            {
                return Path.GetFileName(profileRoot);
            }
        }

        return null;
    }

    private static bool IsSharedSavePath(string savePath, string gameRoot) =>
        new[]
        {
            Path.Combine(gameRoot, "appdata", "savedgames"),
            Path.Combine(gameRoot, "_appdata_", "savedgames"),
            Path.Combine(gameRoot, "savedgames"),
        }.Any(directory => IsInsideDirectory(savePath, directory));

    private static bool IsInsideDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative)
               && !relative.Equals("..", StringComparison.Ordinal)
               && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool PathEquals(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static SaveOriginResolution Known(SaveRuntimeOrigin origin, string? profile) => origin switch
    {
        SaveRuntimeOrigin.ModOrganizer => new(
            origin,
            profile,
            $"MO2 · {profile ?? "ПРОФИЛЬ НЕИЗВЕСТЕН"}",
            "Сохранение создано при запуске сборки через Mod Organizer 2"),
        SaveRuntimeOrigin.Original => new(
            origin,
            null,
            "ОРИГИНАЛЬНАЯ ИГРА",
            "Сохранение создано при запуске чистой Anomaly"),
        _ => Unknown(),
    };

    private static SaveOriginResolution Unknown() => new(
        SaveRuntimeOrigin.Unknown,
        null,
        "ИСТОЧНИК НЕ ОПРЕДЕЛЁН",
        "Старое сохранение: лаунчер не будет запускать его в несовместимом режиме");

    private static void TrimState(SaveProvenanceState state)
    {
        state.Saves = state.Saves
            .Where(item => File.Exists(item.Path))
            .OrderByDescending(item => item.DetectedAtUtc)
            .Take(1000)
            .ToList();
    }

    private sealed class SaveProvenanceState
    {
        public SaveSessionRecord? ActiveSession { get; set; }

        public List<KnownSaveRecord> Saves { get; set; } = [];
    }

    private sealed class SaveSessionRecord
    {
        public string GameRoot { get; set; } = string.Empty;

        public SaveRuntimeOrigin Origin { get; set; }

        public string? Profile { get; set; }

        public DateTime StartedAtUtc { get; set; }

        public Dictionary<string, string> Baseline { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class KnownSaveRecord
    {
        public string Path { get; set; } = string.Empty;

        public string Fingerprint { get; set; } = string.Empty;

        public SaveRuntimeOrigin Origin { get; set; }

        public string? Profile { get; set; }

        public DateTime DetectedAtUtc { get; set; }
    }
}
