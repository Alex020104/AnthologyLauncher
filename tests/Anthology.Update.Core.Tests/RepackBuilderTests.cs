using Anthology.Releaser.Core;

namespace Anthology.Update.Core.Tests;

public sealed class RepackBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"anthology-repack-tests-{Guid.NewGuid():N}");

    public RepackBuilderTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void InstallerScriptUsesProjectNameRootsAndDesktopShortcut()
    {
        var request = CreateRequest();

        var script = RepackBuilder.GenerateInstallerScript(request, Path.Combine(_root, "stage"), 60L * 1024 * 1024, 80L * 1024 * 1024);

        Assert.Contains("#define MyAppName \"MY ANTHOLOGY\"", script, StringComparison.Ordinal);
        Assert.Contains("#define LauncherRelativePath \"Game Root\\AnomalyLauncher.exe\"", script, StringComparison.Ordinal);
        Assert.Contains("Name: \"{userdesktop}\\{#MyAppName}\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Tasks: desktopicon", script, StringComparison.Ordinal);
        Assert.Contains("Name: \"modpack\"; Description: \"MO2 Root\"", script, StringComparison.Ordinal);
        Assert.Contains("RequiredFullInstallSpaceMB", script, StringComparison.Ordinal);
        Assert.Contains("Exec(ExpandConstant('{tmp}\\7z.exe')", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ExtractWithProgress", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildCreatesVerifiedOutputsAndRemovesTemporaryJob()
    {
        var request = CreateRequest();
        var runner = new FakeRepackToolRunner();

        var result = await new RepackBuilder(runner).BuildAsync(request);

        Assert.Equal(3, result.Outputs.Count);
        Assert.All(result.Outputs, path => Assert.True(File.Exists(path), path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(request.TemporaryRoot));
        Assert.Contains(
            runner.Commands.SelectMany(command => command.Arguments),
            argument => argument.Equals("-x!Game Root\\appdata\\*", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, runner.Commands.Count(command => command.Arguments.Count > 0 && command.Arguments[0] == "t"));
    }

    [Fact]
    public async Task CancellationRemovesPartiallyWrittenArchive()
    {
        var request = CreateRequest();
        var runner = new FakeRepackToolRunner(cancelOnFirstArchive: true);

        await Assert.ThrowsAsync<OperationCanceledException>(() => new RepackBuilder(runner).BuildAsync(request));

        Assert.Empty(Directory.EnumerateFileSystemEntries(request.TemporaryRoot));
        Assert.True(!Directory.Exists(request.OutputRoot) || !Directory.EnumerateFileSystemEntries(request.OutputRoot).Any());
    }

    [Fact]
    public void WorkspaceCleanupOnlyRemovesOldTransactionFiles()
    {
        var oldTemporary = Path.Combine(_root, "release-workspace.json.tmp-old");
        var freshTemporary = Path.Combine(_root, "release-workspace.json.tmp-fresh");
        var ordinary = Path.Combine(_root, "keep.json");
        File.WriteAllText(oldTemporary, string.Empty);
        File.WriteAllText(freshTemporary, string.Empty);
        File.WriteAllText(ordinary, string.Empty);
        File.SetLastWriteTimeUtc(oldTemporary, DateTime.UtcNow.AddHours(-3));

        var removed = WorkspaceStorage.CleanupTemporaryFiles(_root, TimeSpan.FromHours(1));

        Assert.Equal(1, removed);
        Assert.False(File.Exists(oldTemporary));
        Assert.True(File.Exists(freshTemporary));
        Assert.True(File.Exists(ordinary));
    }

    [Fact]
    public void RepackCleanupOnlyRemovesOldNamedJobDirectories()
    {
        var temporaryRoot = Path.Combine(_root, "temporary-cleanup");
        var stale = Path.Combine(temporaryRoot, "repack-stale");
        var recent = Path.Combine(temporaryRoot, "repack-recent");
        var unrelated = Path.Combine(temporaryRoot, "keep-this");
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(recent);
        Directory.CreateDirectory(unrelated);
        var staleFile = Path.Combine(stale, "large.partial");
        File.WriteAllText(staleFile, "stale");
        File.SetLastWriteTimeUtc(staleFile, DateTime.UtcNow.AddDays(-2));
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

        var removed = RepackBuilder.CleanupStaleJobs(temporaryRoot, TimeSpan.FromDays(1));

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(recent));
        Assert.True(Directory.Exists(unrelated));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }
        foreach (var path in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private RepackBuildRequest CreateRequest()
    {
        var game = Path.Combine(_root, "Game Root");
        var mo2 = Path.Combine(_root, "MO2 Root");
        var output = Path.Combine(_root, "output");
        var temporary = Path.Combine(_root, "temporary");
        var tools = Path.Combine(_root, "tools");
        var template = Path.Combine(_root, "template");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(mo2);
        Directory.CreateDirectory(temporary);
        Directory.CreateDirectory(tools);
        Directory.CreateDirectory(template);
        File.WriteAllText(Path.Combine(game, "game.exe"), "game");
        Directory.CreateDirectory(Path.Combine(game, "appdata"));
        File.WriteAllText(Path.Combine(game, "appdata", "personal.ltx"), "excluded");
        File.WriteAllText(Path.Combine(mo2, "ModOrganizer.exe"), "mo2");
        File.WriteAllText(Path.Combine(tools, "7z.exe"), "fake");
        File.WriteAllText(Path.Combine(tools, "7z.dll"), "fake");
        File.WriteAllText(Path.Combine(tools, "ISCC.exe"), "fake");
        File.WriteAllText(Path.Combine(template, "AnthologyLauncher.ico"), "fake");
        return new RepackBuildRequest
        {
            ProjectName = "MY ANTHOLOGY",
            Version = "2.1.200",
            GameSourceRoot = game,
            Mo2SourceRoot = mo2,
            OutputRoot = output,
            TemporaryRoot = temporary,
            SevenZipPath = Path.Combine(tools, "7z.exe"),
            InnoSetupCompilerPath = Path.Combine(tools, "ISCC.exe"),
            InstallerTemplateRoot = template,
        };
    }

    private sealed class FakeRepackToolRunner(bool cancelOnFirstArchive = false) : IRepackToolRunner
    {
        private readonly Dictionary<string, string> _archiveRoots = new(StringComparer.OrdinalIgnoreCase);
        private bool _cancelled;

        public List<RepackToolCommand> Commands { get; } = [];

        public Task<RepackToolResult> RunAsync(
            RepackToolCommand command,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            if (Path.GetFileName(command.FileName).Equals("7z.exe", StringComparison.OrdinalIgnoreCase))
            {
                var operation = command.Arguments[0];
                var archive = command.Arguments[2];
                if (operation == "a")
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
                    File.WriteAllText(archive, "verified archive");
                    _archiveRoots[archive] = command.Arguments[3];
                    if (cancelOnFirstArchive && !_cancelled)
                    {
                        _cancelled = true;
                        throw new OperationCanceledException();
                    }
                    return Task.FromResult(new RepackToolResult(0, "Everything is Ok", string.Empty));
                }
                if (operation == "l")
                {
                    archive = command.Arguments[^1];
                    var root = _archiveRoots[archive];
                    return Task.FromResult(new RepackToolResult(0, $"----------{Environment.NewLine}Path = {root}{Environment.NewLine}", string.Empty));
                }
                return Task.FromResult(new RepackToolResult(0, "Everything is Ok", string.Empty));
            }

            var script = command.Arguments[^1];
            var lines = File.ReadAllLines(script);
            var output = lines.Single(line => line.StartsWith("OutputDir=", StringComparison.Ordinal))["OutputDir=".Length..];
            var name = lines.Single(line => line.StartsWith("OutputBaseFilename=", StringComparison.Ordinal))["OutputBaseFilename=".Length..];
            File.WriteAllText(Path.Combine(output, name + ".exe"), "setup");
            return Task.FromResult(new RepackToolResult(0, "Successful compile", string.Empty));
        }
    }
}
