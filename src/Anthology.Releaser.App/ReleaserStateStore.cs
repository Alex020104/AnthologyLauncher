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
            _ = WorkspaceStorage.CleanupTemporaryFiles(DataRoot, TimeSpan.FromHours(1));
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
            try
            {
                _ = RepackBuilder.CleanupStaleJobs(machine.RepackTemporaryRoot, TimeSpan.FromDays(1));
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or NotSupportedException)
            {
                // Invalid user-entered paths stay visible in the editor and must not
                // prevent the releaser from starting. Build validation reports them.
            }
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
        var normalizedSchemaVersion = Math.Max(workspace.SchemaVersion, 10);
        if (workspace.SchemaVersion != normalizedSchemaVersion)
        {
            workspace.SchemaVersion = normalizedSchemaVersion;
            changed = true;
        }
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
        var sharedWorkspaceRootBeforeRepair = machine.SharedWorkspaceRoot;
        changed |= ReleaserMachinePathNormalizer.Normalize(machine);
        if (!string.Equals(sharedWorkspaceRootBeforeRepair, machine.SharedWorkspaceRoot, StringComparison.Ordinal))
        {
            // The previous hash belongs to the old (mojibake) shared directory. Reusing it
            // against the repaired directory can make an older shared draft look newer and
            // overwrite the current local workspace during the next automatic sync.
            machine.LastSyncedHash = string.Empty;
            changed = true;
        }
        changed |= NormalizeQuickReleaseDestinations(machine);
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

        // Google Drive was added after existing workspaces had already acquired
        // their source list. Add only the empty provider record during migration:
        // the authenticated /drive/home page is help, never a downloadable URL.
        if (!workspace.Mirrors.Any(mirror =>
                string.Equals(mirror.Provider, GoogleDrivePublisher.Provider, StringComparison.OrdinalIgnoreCase)))
        {
            workspace.Mirrors.Add(new ReleaseMirrorSet
            {
                Provider = GoogleDrivePublisher.Provider,
                Priority = 30,
            });
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
                mirror.ArtifactUrl = mirror.ArtifactUrl?.Trim() ?? string.Empty;
                mirror.ContentUrl = mirror.ContentUrl?.Trim() ?? string.Empty;
                mirror.ManifestUrl = mirror.ManifestUrl?.Trim() ?? string.Empty;
            }
        }

        // Apply source-folder templates only after missing mirror records have been
        // created and their generic defaults normalized. This also makes a fresh
        // workspace immediately reuse the already-synced Yandex project folders.
        changed |= ApplyYandexLooseSourceDefaults(workspace, machine);

        if (previousSchemaVersion < 3 || seedEditorialContent)
        {
            EditorialContentSeed.AddMissing(workspace.Content);
        }

        return changed;
    }

    private static bool NormalizeQuickReleaseDestinations(ReleaserMachineSettings machine)
    {
        var changed = false;
        foreach (var file in machine.QuickReleaseFiles)
        {
            if (string.IsNullOrWhiteSpace(file.SourcePath) || string.IsNullOrWhiteSpace(file.RelativePath))
            {
                continue;
            }

            try
            {
                var destination = QuickReleaseDestinationMapper.NormalizeFileDestination(
                    file.InstallRoot,
                    machine.Mo2SourceRoot,
                    file.SourcePath,
                    file.RelativePath);
                if (!string.Equals(destination, file.RelativePath, StringComparison.Ordinal))
                {
                    file.RelativePath = destination;
                    changed = true;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                // Keep manually entered invalid values visible so the editor can correct them.
            }
        }

        foreach (var folder in machine.QuickReleaseFolders)
        {
            if (string.IsNullOrWhiteSpace(folder.SourcePath))
            {
                continue;
            }

            try
            {
                var destination = QuickReleaseDestinationMapper.NormalizeFolderDestination(
                    folder.InstallRoot,
                    machine.Mo2SourceRoot,
                    folder.SourcePath,
                    folder.RelativePath);
                if (!string.Equals(destination, folder.RelativePath, StringComparison.Ordinal))
                {
                    folder.RelativePath = destination;
                    changed = true;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                // Keep manually entered invalid values visible so the editor can correct them.
            }
        }

        return changed;
    }

    private static bool ApplyMirrorDefaults(ReleaseMirrorSet mirror)
    {
        var changed = false;
        mirror.Provider = mirror.Provider?.Trim().ToLowerInvariant() ?? "http";
        mirror.GameUrl = mirror.GameUrl?.Trim() ?? string.Empty;
        mirror.Mo2Url = mirror.Mo2Url?.Trim() ?? string.Empty;
        mirror.ArtifactUrl = mirror.ArtifactUrl?.Trim() ?? string.Empty;
        mirror.ContentUrl = mirror.ContentUrl?.Trim() ?? string.Empty;
        mirror.ManifestUrl = mirror.ManifestUrl?.Trim() ?? string.Empty;

        var defaults = mirror.Provider switch
        {
            "github" => new[]
            {
                $"{GitHubRawRoot}/{{version}}/{{file}}",
                $"{GitHubRawRoot}/{{version}}/{{file}}",
                $"{GitHubRawRoot}/{{version}}/{{file}}",
                $"{GitHubRawRoot}/{{version}}/addons/{{id}}/{{file}}",
                $"{GitHubRawRoot}/manifest.json",
            },
            "yandex-disk" => new[]
            {
                $"{YandexPublicRoot}?path={YandexChannelPath}/{{version}}/{{file}}",
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
        if (NeedsDefault(mirror.ArtifactUrl))
        {
            mirror.ArtifactUrl = defaults[2];
            changed = true;
        }
        if (NeedsDefault(mirror.ContentUrl))
        {
            mirror.ContentUrl = defaults[3];
            changed = true;
        }
        if (NeedsDefault(mirror.ManifestUrl))
        {
            mirror.ManifestUrl = defaults[4];
            changed = true;
        }
        return changed;
    }

    private static bool NeedsDefault(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || value.Contains("ЗАМЕНИТЕ", StringComparison.OrdinalIgnoreCase)
        || value.Contains("ВАШ_PUBLIC_KEY", StringComparison.OrdinalIgnoreCase);

    private static bool ApplyYandexLooseSourceDefaults(
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine)
    {
        var mirror = workspace.Mirrors.FirstOrDefault(item =>
            string.Equals(item.Provider, "yandex-disk", StringComparison.OrdinalIgnoreCase));
        if (mirror is null || string.IsNullOrWhiteSpace(machine.SharedWorkspaceRoot))
        {
            return false;
        }

        var changed = false;
        var gameTemplate = CreateYandexSourceTemplate(machine.SharedWorkspaceRoot, machine.GameSourceRoot);
        var mo2Template = CreateYandexSourceTemplate(machine.SharedWorkspaceRoot, machine.Mo2SourceRoot);
        if (gameTemplate is not null && IsLegacyArchiveTemplate(mirror.GameUrl))
        {
            mirror.GameUrl = gameTemplate;
            changed = true;
        }
        if (mo2Template is not null && IsLegacyArchiveTemplate(mirror.Mo2Url))
        {
            mirror.Mo2Url = mo2Template;
            changed = true;
        }
        return changed;
    }

    private static string? CreateYandexSourceTemplate(string sharedRoot, string sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot))
        {
            return null;
        }

        var shared = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sharedRoot));
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        if (source.Equals(shared, StringComparison.OrdinalIgnoreCase)
            || !source.StartsWith(shared + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = Path.GetRelativePath(shared, source).Replace('\\', '/').Trim('/');
        return relative.Length == 0
            ? null
            : $"{YandexPublicRoot}?path=/{relative}/{{path}}";
    }

    private static bool IsLegacyArchiveTemplate(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || value.Contains("/AnthologyUpdateChannel/{version}/{file}", StringComparison.OrdinalIgnoreCase)
        || value.Contains("\\AnthologyUpdateChannel\\{version}\\{file}", StringComparison.OrdinalIgnoreCase);

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

        var roomyDrive = Directory.Exists(@"B:\") ? @"B:\" : Path.GetPathRoot(machine.OutputRoot) ?? @"C:\";
        if (string.IsNullOrWhiteSpace(machine.RepackTemporaryRoot))
        {
            machine.RepackTemporaryRoot = Path.Combine(roomyDrive, "AnthologyReleaserTemp");
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(machine.RepackOutputRoot))
        {
            machine.RepackOutputRoot = Path.Combine(roomyDrive, "Anthology Repack");
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(machine.RepackProjectName))
        {
            machine.RepackProjectName = "ANTHOLOGY";
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(machine.SevenZipPath))
        {
            machine.SevenZipPath = FindFirstExistingFile(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"));
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(machine.InnoSetupCompilerPath)
            || !File.Exists(machine.InnoSetupCompilerPath))
        {
            machine.InnoSetupCompilerPath = FindFirstExistingFile(
                @"B:\AnthologyProjectTools\Inno Setup 6\ISCC.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Inno Setup 6", "ISCC.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Inno Setup 6", "ISCC.exe"));
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(machine.InstallerTemplateRoot)
            || !Directory.Exists(machine.InstallerTemplateRoot))
        {
            machine.InstallerTemplateRoot = FindFirstExistingDirectory(
                Path.Combine(AppContext.BaseDirectory, "InstallerTemplate"),
                @"X:\OpenAI\anomaly-codex-main\projects\Anthology-Work-Git\projects\installer");
            changed = true;
        }

        changed |= EnsureGoogleDriveDefaults(machine);

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

    private static bool EnsureGoogleDriveDefaults(ReleaserMachineSettings machine)
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(machine.GoogleDriveRclonePath))
        {
            machine.GoogleDriveRclonePath = FindRcloneExecutable();
            changed |= machine.GoogleDriveRclonePath.Length > 0;
        }
        if (string.IsNullOrWhiteSpace(machine.GoogleDriveRcloneConfigPath))
        {
            machine.GoogleDriveRcloneConfigPath = @"B:\AnthologyProjectTools\rclone\rclone.conf";
            changed = true;
        }

        changed |= NormalizeSetting(machine.GoogleDriveRemoteName, string.Empty, out var remoteName);
        machine.GoogleDriveRemoteName = remoteName;
        changed |= NormalizeSetting(machine.GoogleDriveProjectPath, "ANTHOLOGY", out var projectPath);
        machine.GoogleDriveProjectPath = projectPath;
        changed |= NormalizeSetting(machine.GoogleDriveGamePath, string.Empty, out var gamePath);
        machine.GoogleDriveGamePath = gamePath;
        changed |= NormalizeSetting(machine.GoogleDriveMo2Path, string.Empty, out var mo2Path);
        machine.GoogleDriveMo2Path = mo2Path;
        changed |= NormalizeSetting(machine.GoogleDriveReleasePath, "AnthologyUpdateChannel", out var releasePath);
        machine.GoogleDriveReleasePath = releasePath;
        changed |= NormalizeSetting(
            machine.GoogleDriveManifestPath,
            $"{machine.GoogleDriveReleasePath.Trim().TrimEnd('/', '\\')}/manifest.json",
            out var manifestPath);
        machine.GoogleDriveManifestPath = manifestPath;

        if (!string.Equals(machine.GoogleDriveAccountUrl, GoogleDrivePublisher.AccountHomeUrl, StringComparison.Ordinal))
        {
            machine.GoogleDriveAccountUrl = GoogleDrivePublisher.AccountHomeUrl;
            changed = true;
        }
        changed |= NormalizeSetting(machine.GoogleDriveProjectPublicUrl, string.Empty, out var publicUrl);
        machine.GoogleDriveProjectPublicUrl = publicUrl;
        if (string.Equals(
                machine.GoogleDriveProjectPublicUrl,
                GoogleDrivePublisher.AccountHomeUrl,
                StringComparison.OrdinalIgnoreCase))
        {
            machine.GoogleDriveProjectPublicUrl = string.Empty;
            changed = true;
        }
        if (machine.GoogleDriveMirrorPriority is < 0 or > 10_000)
        {
            machine.GoogleDriveMirrorPriority = 30;
            changed = true;
        }
        return changed;
    }

    private static bool NormalizeSetting(string? value, string fallback, out string normalized)
    {
        normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return !string.Equals(value, normalized, StringComparison.Ordinal);
    }

    private static string FindRcloneExecutable()
    {
        var candidates = new List<string>
        {
            @"B:\AnthologyProjectTools\rclone\rclone.exe",
            Path.Combine(AppContext.BaseDirectory, "rclone.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "rclone", "rclone.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "rclone", "rclone.exe"),
        };
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    candidates.Add(Path.Combine(directory, "rclone.exe"));
                }
                catch (ArgumentException)
                {
                    // Ignore malformed PATH entries and keep checking known locations.
                }
            }
        }
        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private static string FindFirstExistingFile(params string[] candidates) =>
        candidates.FirstOrDefault(File.Exists) ?? candidates.FirstOrDefault() ?? string.Empty;

    private static string FindFirstExistingDirectory(params string[] candidates) =>
        candidates.FirstOrDefault(Directory.Exists) ?? candidates.FirstOrDefault() ?? string.Empty;

    public void Dispose() => _gate.Dispose();
}
