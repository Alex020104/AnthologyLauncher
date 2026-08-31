using Anthology.Releaser.Core;
using System.IO;

namespace Anthology.Releaser.App;

public sealed class ReleaserStateStore : IDisposable
{
    private const string GitHubRawRoot = "https://raw.githubusercontent.com/Alex020104/AnthologyLauncher/addons-unified-library";
    private const string YandexPublicRoot = "https://disk.yandex.ru/d/V7pISmMO9ApI5w";
    private const string YandexChannelPath = "/AnthologyUpdateChannel";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ReleaserStateStore()
    {
        DataRoot = Path.Combine(AppContext.BaseDirectory, "Data");
        WorkspacePath = Path.Combine(DataRoot, "release-workspace.json");
        MachinePath = Path.Combine(DataRoot, "machine-settings.json");
    }

    public string DataRoot { get; }

    public string WorkspacePath { get; }

    public string MachinePath { get; }

    public async Task<(ReleaserWorkspace Workspace, ReleaserMachineSettings Machine)> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(DataRoot);
            var workspaceExists = File.Exists(WorkspacePath);
            var workspace = await WorkspaceStorage.LoadAsync(WorkspacePath, () => new ReleaserWorkspace(), cancellationToken);
            var machine = await WorkspaceStorage.LoadAsync(MachinePath, () => new ReleaserMachineSettings(), cancellationToken);
            var requiresMigrationSave = !workspaceExists || workspace.SchemaVersion < 6;
            var workspaceDefaultsChanged = Normalize(
                workspace,
                machine,
                seedEditorialContent: requiresMigrationSave,
                applyMirrorDefaults: requiresMigrationSave);
            var machineDefaultsChanged = EnsureMachineDefaults(workspace, machine);
            if (requiresMigrationSave || workspaceDefaultsChanged || machineDefaultsChanged)
            {
                await WorkspaceStorage.SaveAsync(WorkspacePath, workspace, cancellationToken);
                await WorkspaceStorage.SaveAsync(MachinePath, machine, cancellationToken);
            }
            return (workspace, machine);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveWorkspaceAsync(
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        bool incrementRevision,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _ = Normalize(workspace, machine);
            if (incrementRevision)
            {
                workspace.Revision++;
                workspace.UpdatedAt = DateTimeOffset.UtcNow;
                workspace.UpdatedBy = machine.DeveloperName;
            }

            await WorkspaceStorage.SaveAsync(WorkspacePath, workspace, cancellationToken);
            await WorkspaceStorage.SaveAsync(MachinePath, machine, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SaveMachineAsync(
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        CancellationToken cancellationToken = default) =>
        SaveWorkspaceAsync(workspace, machine, false, cancellationToken);

    private static bool Normalize(
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        bool seedEditorialContent = false,
        bool applyMirrorDefaults = false)
    {
        var changed = false;
        var previousSchemaVersion = workspace.SchemaVersion;
        workspace.Mirrors ??= [];
        workspace.Content ??= [];
        workspace.SocialLinks ??= [];
        workspace.ProjectPeople ??= [];
        workspace.LiveStreams ??= [];
        workspace.Changelog ??= new ReleaseChangelogDraft();
        workspace.Changelog.Translations = new Dictionary<string, ReleaseChangelogTranslationDraft>(
            workspace.Changelog.Translations ?? [],
            StringComparer.OrdinalIgnoreCase);
        foreach (var defaultLink in SocialLinkDraft.CreateDefaults())
        {
            if (workspace.SocialLinks.Any(link => string.Equals(link.Id, defaultLink.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            workspace.SocialLinks.Add(defaultLink);
            changed = true;
        }
        foreach (var link in workspace.SocialLinks)
        {
            link.Id = link.Id?.Trim().ToLowerInvariant() ?? string.Empty;
            link.Title = link.Title?.Trim() ?? string.Empty;
            link.Subtitle = link.Subtitle?.Trim() ?? string.Empty;
            link.Url = link.Url?.Trim() ?? string.Empty;
            if (link.Id == "moddb"
                && string.Equals(link.Url, "https://www.moddb.com/mods/stalker-anomaly", StringComparison.OrdinalIgnoreCase))
            {
                link.Url = "https://www.moddb.com/mods/anthology";
                changed = true;
            }
        }
        foreach (var person in workspace.ProjectPeople)
        {
            person.Id = string.IsNullOrWhiteSpace(person.Id) ? $"person-{Guid.NewGuid():N}" : person.Id.Trim().ToLowerInvariant();
            person.Name = person.Name?.Trim() ?? string.Empty;
            person.Role = person.Role?.Trim() ?? string.Empty;
            person.Description = person.Description?.Trim() ?? string.Empty;
            person.ImageUrl = person.ImageUrl?.Trim() ?? string.Empty;
            person.Links ??= [];
            foreach (var defaultLink in SocialLinkDraft.CreateAuthorDefaults())
            {
                if (!person.Links.Any(link => string.Equals(link.Id, defaultLink.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    person.Links.Add(defaultLink);
                    changed = true;
                }
            }
            person.Translations = new Dictionary<string, ProjectPersonTranslationDraft>(person.Translations ?? [], StringComparer.OrdinalIgnoreCase);
        }
        foreach (var stream in workspace.LiveStreams)
        {
            stream.Id = string.IsNullOrWhiteSpace(stream.Id) ? $"stream-{Guid.NewGuid():N}" : stream.Id.Trim().ToLowerInvariant();
            stream.Title = stream.Title?.Trim() ?? string.Empty;
            stream.Subtitle = stream.Subtitle?.Trim() ?? string.Empty;
            stream.Url = stream.Url?.Trim() ?? string.Empty;
            stream.Translations = new Dictionary<string, LiveStreamTranslationDraft>(stream.Translations ?? [], StringComparer.OrdinalIgnoreCase);
        }
        workspace.SchemaVersion = Math.Max(workspace.SchemaVersion, 8);
        foreach (var content in workspace.Content)
        {
            // Schema 1 treated every existing entry as published. Keep that state during migration;
            // newly created schema 2 entries start as explicit drafts.
            if (previousSchemaVersion < 2)
            {
                content.IsPublished = true;
            }
            content.AuthorLinks ??= [];
            foreach (var defaultLink in SocialLinkDraft.CreateAuthorDefaults())
            {
                if (content.AuthorLinks.Any(link => string.Equals(link.Id, defaultLink.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                content.AuthorLinks.Add(defaultLink);
                changed = true;
            }
            foreach (var link in content.AuthorLinks)
            {
                link.Id = link.Id?.Trim().ToLowerInvariant() ?? string.Empty;
                link.Title = link.Title?.Trim() ?? string.Empty;
                link.Subtitle = link.Subtitle?.Trim() ?? string.Empty;
                link.Url = link.Url?.Trim() ?? string.Empty;
            }
            content.Blocks ??= [];
            content.Translations = new Dictionary<string, ContentTranslationDraft>(content.Translations ?? [], StringComparer.OrdinalIgnoreCase);
            MigrateLegacyTranslation(content.Translations, "en", content.TitleEn, content.SummaryEn, content.BodyEn);
            MigrateLegacyTranslation(content.Translations, "de", content.TitleDe, content.SummaryDe, content.BodyDe);
            foreach (var block in content.Blocks)
            {
                block.Id = string.IsNullOrWhiteSpace(block.Id) ? $"block-{Guid.NewGuid():N}" : block.Id.Trim();
                block.Translations = new Dictionary<string, ContentBlockTranslationDraft>(block.Translations ?? [], StringComparer.OrdinalIgnoreCase);
                MigrateLegacyBlockTranslation(block.Translations, "en", block.TitleEn, block.BodyEn);
                MigrateLegacyBlockTranslation(block.Translations, "de", block.TitleDe, block.BodyDe);
            }
        }
        machine.ContentArchivePaths = new Dictionary<string, string>(machine.ContentArchivePaths ?? [], StringComparer.OrdinalIgnoreCase);
        machine.ContentImagePaths = new Dictionary<string, List<string>>(
            machine.ContentImagePaths ?? [],
            StringComparer.OrdinalIgnoreCase);
        foreach (var key in machine.ContentImagePaths.Keys.ToArray())
        {
            machine.ContentImagePaths[key] = (machine.ContentImagePaths[key] ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        machine.ContentVideoPaths = new Dictionary<string, List<string>>(
            machine.ContentVideoPaths ?? [],
            StringComparer.OrdinalIgnoreCase);
        foreach (var key in machine.ContentVideoPaths.Keys.ToArray())
        {
            machine.ContentVideoPaths[key] = (machine.ContentVideoPaths[key] ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        machine.QuickReleaseFiles ??= [];
        machine.QuickReleaseFolders ??= [];
        machine.QuickDeleteFiles ??= [];
        machine.QuickDeleteFolders ??= [];
        machine.PublicationRoots = new Dictionary<string, string>(machine.PublicationRoots ?? [], StringComparer.OrdinalIgnoreCase);
        machine.DeveloperName = string.IsNullOrWhiteSpace(machine.DeveloperName) ? Environment.UserName : machine.DeveloperName.Trim();
        machine.CommunityApiUrl = string.IsNullOrWhiteSpace(machine.CommunityApiUrl)
            ? Environment.GetEnvironmentVariable("ANTHOLOGY_COMMUNITY_API") ?? "http://127.0.0.1:5249"
            : machine.CommunityApiUrl.Trim();
        machine.CommunityDeveloperToken = machine.CommunityDeveloperToken?.Trim() ?? string.Empty;
        machine.AutoSyncSeconds = Math.Clamp(machine.AutoSyncSeconds, 30, 3600);
        if (workspace.Mirrors.Count == 0)
        {
            workspace.Mirrors.AddRange(
            [
                new ReleaseMirrorSet { Provider = "yandex-disk", Priority = 10 },
                new ReleaseMirrorSet { Provider = "github", Priority = 20 },
                new ReleaseMirrorSet { Provider = "google-drive", Priority = 30 },
                new ReleaseMirrorSet { Provider = "http", Priority = 40 },
            ]);
            changed = true;
        }

        foreach (var mirror in workspace.Mirrors)
        {
            if (string.IsNullOrWhiteSpace(mirror.Id))
            {
                mirror.Id = $"source-{Guid.NewGuid():N}";
                changed = true;
            }
            else
            {
                mirror.Id = mirror.Id.Trim();
            }

            if (applyMirrorDefaults)
            {
                changed |= ApplyMirrorDefaults(mirror);
            }
            else
            {
                mirror.Provider = mirror.Provider?.Trim().ToLowerInvariant() ?? "http";
                mirror.GameUrl = mirror.GameUrl?.Trim() ?? string.Empty;
                mirror.Mo2Url = mirror.Mo2Url?.Trim() ?? string.Empty;
                mirror.ContentUrl = mirror.ContentUrl?.Trim() ?? string.Empty;
                mirror.ManifestUrl = mirror.ManifestUrl?.Trim() ?? string.Empty;
            }
        }

        if (previousSchemaVersion < 3 || seedEditorialContent)
        {
            EditorialContentSeed.AddMissing(workspace.Content);
        }

        return changed;
    }

    private static bool ApplyMirrorDefaults(ReleaseMirrorSet mirror)
    {
        var changed = false;
        mirror.Provider = mirror.Provider?.Trim().ToLowerInvariant() ?? "http";
        mirror.GameUrl = mirror.GameUrl?.Trim() ?? string.Empty;
        mirror.Mo2Url = mirror.Mo2Url?.Trim() ?? string.Empty;
        mirror.ContentUrl = mirror.ContentUrl?.Trim() ?? string.Empty;
        mirror.ManifestUrl = mirror.ManifestUrl?.Trim() ?? string.Empty;

        var defaults = mirror.Provider switch
        {
            "github" => new[]
            {
                $"{GitHubRawRoot}/{{version}}/{{file}}",
                $"{GitHubRawRoot}/{{version}}/{{file}}",
                $"{GitHubRawRoot}/{{version}}/addons/{{id}}/{{file}}",
                $"{GitHubRawRoot}/manifest.json",
            },
            "yandex-disk" => new[]
            {
                $"{YandexPublicRoot}?path={YandexChannelPath}/{{version}}/{{file}}",
                $"{YandexPublicRoot}?path={YandexChannelPath}/{{version}}/{{file}}",
                $"{YandexPublicRoot}?path={YandexChannelPath}/{{version}}/addons/{{id}}/{{file}}",
                $"{YandexPublicRoot}?path={YandexChannelPath}/manifest.json",
            },
            _ => null,
        };
        if (defaults is null)
        {
            return false;
        }

        if (NeedsDefault(mirror.GameUrl))
        {
            mirror.GameUrl = defaults[0];
            changed = true;
        }
        if (NeedsDefault(mirror.Mo2Url))
        {
            mirror.Mo2Url = defaults[1];
            changed = true;
        }
        if (NeedsDefault(mirror.ContentUrl))
        {
            mirror.ContentUrl = defaults[2];
            changed = true;
        }
        if (NeedsDefault(mirror.ManifestUrl))
        {
            mirror.ManifestUrl = defaults[3];
            changed = true;
        }
        return changed;
    }

    private static bool NeedsDefault(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || value.Contains("ЗАМЕНИТЕ", StringComparison.OrdinalIgnoreCase)
        || value.Contains("ВАШ_PUBLIC_KEY", StringComparison.OrdinalIgnoreCase);

    private static void MigrateLegacyTranslation(
        Dictionary<string, ContentTranslationDraft> translations,
        string language,
        string title,
        string summary,
        string body)
    {
        if (translations.ContainsKey(language)
            || string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(summary) && string.IsNullOrWhiteSpace(body))
        {
            return;
        }
        translations[language] = new ContentTranslationDraft { Title = title, Summary = summary, Body = body };
    }

    private static void MigrateLegacyBlockTranslation(
        Dictionary<string, ContentBlockTranslationDraft> translations,
        string language,
        string title,
        string body)
    {
        if (translations.ContainsKey(language) || string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
        {
            return;
        }
        translations[language] = new ContentBlockTranslationDraft { Title = title, Body = body };
    }

    private bool EnsureMachineDefaults(ReleaserWorkspace workspace, ReleaserMachineSettings machine)
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(machine.OutputRoot))
        {
            machine.OutputRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Releases"));
            changed = true;
        }

        var keyRoot = Path.Combine(DataRoot, "Keys");
        if (string.IsNullOrWhiteSpace(machine.PrivateKeyPath))
        {
            machine.PrivateKeyPath = Path.Combine(keyRoot, "anthology.private.pem");
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(machine.PublicKeyPath))
        {
            machine.PublicKeyPath = Path.Combine(keyRoot, "anthology.public.pem");
            changed = true;
        }

        if (!string.Equals(machine.KeyId, ProductionSigningKeyPolicy.KeyId, StringComparison.Ordinal))
        {
            machine.KeyId = ProductionSigningKeyPolicy.KeyId;
            changed = true;
        }

        var githubMirror = workspace.Mirrors.FirstOrDefault(mirror =>
            string.Equals(mirror.Provider, "github", StringComparison.OrdinalIgnoreCase));
        const string githubWorkingTree = @"A:\AnthologyUnifiedAddons";
        if (githubMirror is not null
            && Directory.Exists(githubWorkingTree)
            && (!machine.PublicationRoots.TryGetValue(githubMirror.Id, out var githubRoot)
                || string.IsNullOrWhiteSpace(githubRoot)))
        {
            machine.PublicationRoots[githubMirror.Id] = githubWorkingTree;
            changed = true;
        }

        var yandexMirror = workspace.Mirrors.FirstOrDefault(mirror =>
            string.Equals(mirror.Provider, "yandex-disk", StringComparison.OrdinalIgnoreCase));
        if (yandexMirror is not null && Directory.Exists(machine.SharedWorkspaceRoot))
        {
            var yandexPublicationRoot = Path.Combine(machine.SharedWorkspaceRoot, "AnthologyUpdateChannel");
            try
            {
                Directory.CreateDirectory(yandexPublicationRoot);
                machine.PublicationRoots.TryGetValue(yandexMirror.Id, out var currentYandexRoot);
                var pointsAtGame = !string.IsNullOrWhiteSpace(currentYandexRoot)
                    && !string.IsNullOrWhiteSpace(machine.GameSourceRoot)
                    && Path.GetFullPath(currentYandexRoot).Equals(
                        Path.GetFullPath(machine.GameSourceRoot),
                        StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(currentYandexRoot) || pointsAtGame)
                {
                    machine.PublicationRoots[yandexMirror.Id] = yandexPublicationRoot;
                    changed = true;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A disconnected sync folder must not prevent the releaser from starting.
            }
        }

        return changed;
    }

    public void Dispose() => _gate.Dispose();
}
