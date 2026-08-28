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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
