using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Anthology.Releaser.Core;
using Xunit;

namespace Anthology.Update.Core.Tests;

public sealed class ReleaserMachinePathNormalizerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"anthology-path-repair-{Guid.NewGuid():N}");

    [Fact]
    public void NormalizeRepairsConfirmedMachineAndSelectedPaths()
    {
        var correctRoot = Path.Combine(_root, "S.T.A.L.K.E.R Anomaly — A.N.T.H.O.L.O.G.Y");
        var brokenRoot = CorruptUtf8AsWindows1251(correctRoot);
        Assert.Contains("вЂ”", brokenRoot, StringComparison.Ordinal);
        Directory.CreateDirectory(correctRoot);
        Directory.CreateDirectory(brokenRoot);

        var game = CreateDirectory(correctRoot, "game");
        var mo2 = CreateDirectory(correctRoot, "mo2");
        var shared = CreateDirectory(correctRoot, "shared");
        var publication = CreateDirectory(correctRoot, "published");
        var quickFolder = CreateDirectory(correctRoot, "selected-folder");
        var privateKey = CreateFile(correctRoot, "keys", "private.pem");
        var publicKey = CreateFile(correctRoot, "keys", "public.pem");
        var archive = CreateFile(correctRoot, "selected", "addon.zip");
        var image = CreateFile(correctRoot, "selected", "cover.png");
        var video = CreateFile(correctRoot, "selected", "clip.mp4");
        var quickFile = CreateFile(correctRoot, "selected", "patch.ltx");
        var futureOutput = Path.Combine(correctRoot, "future-output");

        var machine = new ReleaserMachineSettings
        {
            DeveloperName = CorruptUtf8AsWindows1251("Шура"),
            GameSourceRoot = CorruptUtf8AsWindows1251(game),
            Mo2SourceRoot = CorruptUtf8AsWindows1251(mo2),
            OutputRoot = CorruptUtf8AsWindows1251(futureOutput),
            SharedWorkspaceRoot = CorruptUtf8AsWindows1251(shared),
            PrivateKeyPath = CorruptUtf8AsWindows1251(privateKey),
            PublicKeyPath = CorruptUtf8AsWindows1251(publicKey),
            PublicationRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["yandex"] = CorruptUtf8AsWindows1251(publication),
            },
            ContentArchivePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["addon"] = CorruptUtf8AsWindows1251(archive),
            },
            ContentImagePaths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["content/addon"] = [CorruptUtf8AsWindows1251(image)],
            },
            ContentVideoPaths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["content/addon"] = [CorruptUtf8AsWindows1251(video)],
            },
            QuickReleaseFiles =
            [
                new QuickReleaseFileDraft { SourcePath = CorruptUtf8AsWindows1251(quickFile) },
            ],
            QuickReleaseFolders =
            [
                new QuickReleaseFolderDraft { SourcePath = CorruptUtf8AsWindows1251(quickFolder) },
            ],
        };

        var changed = ReleaserMachinePathNormalizer.Normalize(machine);

        Assert.True(changed);
        Assert.Equal("Шура", machine.DeveloperName);
        Assert.Equal(game, machine.GameSourceRoot);
        Assert.Equal(mo2, machine.Mo2SourceRoot);
        Assert.Equal(futureOutput, machine.OutputRoot);
        Assert.Equal(shared, machine.SharedWorkspaceRoot);
        Assert.Equal(privateKey, machine.PrivateKeyPath);
        Assert.Equal(publicKey, machine.PublicKeyPath);
        Assert.Equal(publication, machine.PublicationRoots["yandex"]);
        Assert.Equal(archive, machine.ContentArchivePaths["addon"]);
        Assert.Equal(image, Assert.Single(machine.ContentImagePaths["content/addon"]));
        Assert.Equal(video, Assert.Single(machine.ContentVideoPaths["content/addon"]));
        Assert.Equal(quickFile, Assert.Single(machine.QuickReleaseFiles).SourcePath);
        Assert.Equal(quickFolder, Assert.Single(machine.QuickReleaseFolders).SourcePath);
        Assert.False(ReleaserMachinePathNormalizer.Normalize(machine));
    }

    [Fact]
    public void RecoverConfirmedPathRepairsCyrillicMojibake()
    {
        var correct = CreateDirectory(_root, "Моды");
        var broken = CorruptUtf8AsWindows1251(correct);

        Assert.Contains("Р", broken, StringComparison.Ordinal);
        Assert.Equal(correct, ReleaserMachinePathNormalizer.RecoverConfirmedPath(broken));
    }

    [Fact]
    public void RecoverTextRepairsMojibakeWithoutTreatingItAsAFileSystemPath()
    {
        Assert.Equal("Шура", ReleaserMachinePathNormalizer.RecoverText(CorruptUtf8AsWindows1251("Шура")));
        Assert.Equal("Ratniy", ReleaserMachinePathNormalizer.RecoverText("Ratniy"));
    }

    [Fact]
    public void RecoverConfirmedPathDoesNotGuessWhenCorrectParentIsMissing()
    {
        var correctParent = Path.Combine(_root, "Anthology — correct");
        var brokenParent = CorruptUtf8AsWindows1251(correctParent);
        Directory.CreateDirectory(brokenParent);
        var broken = Path.Combine(brokenParent, "future-output");

        Assert.Equal(broken, ReleaserMachinePathNormalizer.RecoverConfirmedPath(broken));
        Assert.False(Directory.Exists(correctParent));
    }

    [Fact]
    public void RecoverConfirmedPathLeavesValidPathUnchanged()
    {
        var valid = CreateDirectory(_root, "Русская папка — Anthology");

        Assert.Equal(valid, ReleaserMachinePathNormalizer.RecoverConfirmedPath(valid));
    }

    private static string CorruptUtf8AsWindows1251(string value)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1251).GetString(Encoding.UTF8.GetBytes(value));
    }

    private static string CreateDirectory(string root, params string[] parts)
    {
        var path = parts.Aggregate(root, Path.Combine);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateFile(string root, params string[] parts)
    {
        var path = parts.Aggregate(root, Path.Combine);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
