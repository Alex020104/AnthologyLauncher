namespace Anthology.Update.Core.Tests;

public sealed class TransactionalFileInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"anthology-installer-{Guid.NewGuid():N}");

    [Fact]
    public async Task ApplyReplacesExistingFileAddsNewFileAndWritesJournal()
    {
        var staged = Path.Combine(_root, "staged");
        var target = Path.Combine(_root, "target");
        var state = Path.Combine(_root, "state");
        Directory.CreateDirectory(Path.Combine(staged, "gamedata"));
        Directory.CreateDirectory(Path.Combine(target, "gamedata"));
        await File.WriteAllTextAsync(Path.Combine(staged, "gamedata", "existing.ltx"), "new");
        await File.WriteAllTextAsync(Path.Combine(staged, "gamedata", "added.ltx"), "added");
        await File.WriteAllTextAsync(Path.Combine(target, "gamedata", "existing.ltx"), "old");

        var result = await TransactionalFileInstaller.ApplyAsync(
            staged,
            target,
            state,
            ["gamedata/existing.ltx", "gamedata/added.ltx"]);

        Assert.Equal(2, result.InstalledFiles);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(target, "gamedata", "existing.ltx")));
        Assert.Equal("added", await File.ReadAllTextAsync(Path.Combine(target, "gamedata", "added.ltx")));
        Assert.Contains("\"status\": \"completed\"", await File.ReadAllTextAsync(result.JournalPath));
    }

    [Fact]
    public async Task MissingStagedFileIsRejectedBeforeTargetChanges()
    {
        var staged = Path.Combine(_root, "staged");
        var target = Path.Combine(_root, "target");
        var state = Path.Combine(_root, "state");
        Directory.CreateDirectory(staged);
        Directory.CreateDirectory(target);
        var protectedFile = Path.Combine(target, "protected.txt");
        await File.WriteAllTextAsync(protectedFile, "unchanged");

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            TransactionalFileInstaller.ApplyAsync(staged, target, state, ["missing.txt"]));

        Assert.Equal("unchanged", await File.ReadAllTextAsync(protectedFile));
        Assert.False(Directory.Exists(Path.Combine(state, "transactions")));
    }

    [Fact]
    public async Task CompletedTransactionCanBeRolledBackByUser()
    {
        var staged = Path.Combine(_root, "rollback-staged");
        var target = Path.Combine(_root, "rollback-target");
        var state = Path.Combine(_root, "rollback-state");
        Directory.CreateDirectory(staged);
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(staged, "existing.ltx"), "updated");
        await File.WriteAllTextAsync(Path.Combine(staged, "new.ltx"), "new");
        await File.WriteAllTextAsync(Path.Combine(target, "existing.ltx"), "previous");

        var install = await TransactionalFileInstaller.ApplyAsync(
            staged,
            target,
            state,
            ["existing.ltx", "new.ltx"]);
        var rollback = await TransactionalFileInstaller.RollbackAsync(
            target,
            state,
            install.OperationId);

        Assert.Equal(2, rollback.RestoredFiles);
        Assert.Equal("previous", await File.ReadAllTextAsync(Path.Combine(target, "existing.ltx")));
        Assert.False(File.Exists(Path.Combine(target, "new.ltx")));
        Assert.Contains("rolled-back-by-user", await File.ReadAllTextAsync(rollback.JournalPath));
    }

    [Fact]
    public async Task ManagedDeletionIsBackedUpAndRestoredByRollback()
    {
        var staged = Path.Combine(_root, "delete-staged");
        var target = Path.Combine(_root, "delete-target");
        var state = Path.Combine(_root, "delete-state");
        Directory.CreateDirectory(staged);
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(staged, "kept.txt"), "version-2");
        await File.WriteAllTextAsync(Path.Combine(target, "kept.txt"), "version-1");
        await File.WriteAllTextAsync(Path.Combine(target, "obsolete.txt"), "restore-me");

        var install = await TransactionalFileInstaller.ApplyAsync(
            staged,
            target,
            state,
            ["kept.txt"],
            ["obsolete.txt"]);

        Assert.Equal(1, install.InstalledFiles);
        Assert.Equal(1, install.DeletedFiles);
        Assert.False(File.Exists(Path.Combine(target, "obsolete.txt")));

        await TransactionalFileInstaller.RollbackAsync(target, state, install.OperationId);

        Assert.Equal("version-1", await File.ReadAllTextAsync(Path.Combine(target, "kept.txt")));
        Assert.Equal("restore-me", await File.ReadAllTextAsync(Path.Combine(target, "obsolete.txt")));
    }

    [Fact]
    public async Task DeletionOnlyTransactionIsBackedUpAndCanBeRolledBack()
    {
        var staged = Path.Combine(_root, "deletion-only-staged");
        var target = Path.Combine(_root, "deletion-only-target");
        var state = Path.Combine(_root, "deletion-only-state");
        Directory.CreateDirectory(staged);
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "obsolete.ltx"), "restore-me");

        var install = await TransactionalFileInstaller.ApplyAsync(
            staged,
            target,
            state,
            [],
            ["obsolete.ltx"]);

        Assert.Equal(0, install.InstalledFiles);
        Assert.Equal(1, install.DeletedFiles);
        Assert.False(File.Exists(Path.Combine(target, "obsolete.ltx")));

        await TransactionalFileInstaller.RollbackAsync(target, state, install.OperationId);

        Assert.Equal("restore-me", await File.ReadAllTextAsync(Path.Combine(target, "obsolete.ltx")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
