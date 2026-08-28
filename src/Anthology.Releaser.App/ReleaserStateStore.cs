using Anthology.Releaser.Core;
using System.IO;

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
            var workspace = await WorkspaceStorage.LoadAsync(WorkspacePath, () => new ReleaserWorkspace(), cancellationToken);
            var machine = await WorkspaceStorage.LoadAsync(MachinePath, () => new ReleaserMachineSettings(), cancellationToken);
            Normalize(workspace, machine);
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

    private static void Normalize(ReleaserWorkspace workspace, ReleaserMachineSettings machine)
    {
        workspace.Mirrors ??= [];
        workspace.Content ??= [];
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
    }

    public void Dispose() => _gate.Dispose();
}
