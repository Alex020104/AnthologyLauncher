using Anthology.Releaser.Core;
using System.IO;
using System.Security.Cryptography;

namespace Anthology.Releaser.App;

public sealed class ReleaserStateStore : IDisposable
{
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
            var requiresMigrationSave = !workspaceExists || workspace.SchemaVersion < 3;
            Normalize(workspace, machine, seedEditorialContent: requiresMigrationSave);
            var machineDefaultsChanged = EnsureMachineDefaults(machine);
            if (requiresMigrationSave || machineDefaultsChanged)
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
            Normalize(workspace, machine);
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

    private static void Normalize(
        ReleaserWorkspace workspace,
        ReleaserMachineSettings machine,
        bool seedEditorialContent = false)
    {
        var previousSchemaVersion = workspace.SchemaVersion;
        workspace.Mirrors ??= [];
        workspace.Content ??= [];
        workspace.SchemaVersion = Math.Max(workspace.SchemaVersion, 3);
        foreach (var content in workspace.Content)
        {
            // Schema 1 treated every existing entry as published. Keep that state during migration;
            // newly created schema 2 entries start as explicit drafts.
            if (previousSchemaVersion < 2)
            {
                content.IsPublished = true;
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
        machine.QuickReleaseFiles ??= [];
        machine.QuickDeleteFiles ??= [];
        machine.PublicationRoots = new Dictionary<string, string>(machine.PublicationRoots ?? [], StringComparer.OrdinalIgnoreCase);
        machine.DeveloperName = string.IsNullOrWhiteSpace(machine.DeveloperName) ? Environment.UserName : machine.DeveloperName.Trim();
        machine.AutoSyncSeconds = Math.Clamp(machine.AutoSyncSeconds, 30, 3600);
        if (workspace.Mirrors.Count == 0)
        {
            workspace.Mirrors.AddRange(
            [
                new ReleaseMirrorSet { Provider = "github", Priority = 10 },
                new ReleaseMirrorSet { Provider = "yandex-disk", Priority = 20 },
                new ReleaseMirrorSet { Provider = "google-drive", Priority = 30 },
                new ReleaseMirrorSet { Provider = "http", Priority = 40 },
            ]);
        }

        foreach (var mirror in workspace.Mirrors)
        {
            mirror.Id = string.IsNullOrWhiteSpace(mirror.Id) ? $"source-{Guid.NewGuid():N}" : mirror.Id.Trim();
        }

        if (previousSchemaVersion < 3 || seedEditorialContent)
        {
            EditorialContentSeed.AddMissing(workspace.Content);
        }
    }

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

    private bool EnsureMachineDefaults(ReleaserMachineSettings machine)
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

        var privateExists = File.Exists(machine.PrivateKeyPath);
        var publicExists = File.Exists(machine.PublicKeyPath);
        if (!privateExists && !publicExists)
        {
            UnifiedReleaseBuilder.GenerateKeys(machine.PrivateKeyPath, machine.PublicKeyPath);
            changed = true;
        }
        else if (privateExists && !publicExists)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(machine.PublicKeyPath)!);
            using var key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(machine.PrivateKeyPath));
            File.WriteAllText(machine.PublicKeyPath, key.ExportSubjectPublicKeyInfoPem());
            changed = true;
        }

        return changed;
    }

    public void Dispose() => _gate.Dispose();
}
