using System.IO.Compression;
using System.Text;
using Anthology.Mo2.Core;

namespace Anthology.Update.Core.Tests;

public sealed class FomodInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"anthology-fomod-{Guid.NewGuid():N}");
    private readonly List<FomodPackage> _packages = [];

    [Fact]
    public void InspectDistinguishesRegularArchiveFromFomod()
    {
        var archivePath = CreateArchive("regular.zip", new Dictionary<string, string>
        {
            ["gamedata/configs/test.ltx"] = "[test]"
        });

        var inspection = Mo2ArchiveInstaller.InspectFomod(archivePath);

        Assert.False(inspection.IsFomod);
        Assert.False(inspection.Success);
        Assert.Null(inspection.Package);
    }

    [Fact]
    public void InspectParsesMetadataWrapperOrderingAndAsset()
    {
        var archivePath = CreateArchive("ordered.zip", new Dictionary<string, string>
        {
            ["Wrapper/fomod/ModuleConfig.xml"] = """
                <?xml version="1.0" encoding="utf-8"?>
                <config xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                  <moduleName>Installer title</moduleName>
                  <moduleImage path="fomod/header.png" showImage="false" />
                  <requiredInstallFiles>
                    <file source="base.txt" destination="gamedata/base.txt" />
                  </requiredInstallFiles>
                  <installSteps order="Descending">
                    <installStep name="Alpha">
                      <optionalFileGroups order="Descending">
                        <group name="First" type="SelectAny">
                          <plugins order="Descending">
                            <plugin name="A"><description>first</description><typeDescriptor><type name="Optional" /></typeDescriptor></plugin>
                            <plugin name="Z"><description>last</description><typeDescriptor><type name="Recommended" /></typeDescriptor></plugin>
                          </plugins>
                        </group>
                        <group name="Second" type="SelectAll">
                          <plugins><plugin name="Only"><description>all</description><typeDescriptor><type name="Required" /></typeDescriptor></plugin></plugins>
                        </group>
                      </optionalFileGroups>
                    </installStep>
                    <installStep name="Zulu">
                      <optionalFileGroups><group name="Empty" type="SelectAny"><plugins /></group></optionalFileGroups>
                    </installStep>
                  </installSteps>
                </config>
                """,
            ["Wrapper/fomod/info.xml"] = """
                <fomod><Name>Package name</Name><Author>Author</Author><Version>2.4</Version><Website>https://example.invalid</Website><Description>Info</Description><Id>42</Id></fomod>
                """,
            ["Wrapper/fomod/header.png"] = "image-bytes",
            ["Wrapper/base.txt"] = "base"
        });

        var inspection = Mo2ArchiveInstaller.InspectFomod(archivePath);

        Assert.True(inspection.Success, inspection.Message);
        var package = Assert.IsType<FomodPackage>(inspection.Package);
        _packages.Add(package);
        Assert.Equal("Wrapper/", package.ContentPrefix);
        Assert.Equal("Installer title", package.Module.Name);
        Assert.False(package.Module.ShowImage);
        Assert.Equal("Package name", package.Metadata.Name);
        Assert.Equal("Author", package.Metadata.Author);
        Assert.Equal("2.4", package.Metadata.Version);
        Assert.Equal(["Zulu", "Alpha"], package.Module.Steps.Select(step => step.Name));
        Assert.Equal(["Second", "First"], package.Module.Steps[1].Groups.Select(group => group.Name));
        Assert.Equal(["Z", "A"], package.Module.Steps[1].Groups[1].Plugins.Select(plugin => plugin.Name));
        Assert.Equal("step-0", package.Module.Steps[1].Id);
        Assert.Equal("image-bytes", Encoding.UTF8.GetString(FomodArchiveReader.ReadAsset(package, "fomod/header.png")));
    }

    [Fact]
    public void InspectRejectsDtdAndExternalEntities()
    {
        var archivePath = CreateArchive("xxe.zip", new Dictionary<string, string>
        {
            ["fomod/ModuleConfig.xml"] = """
                <!DOCTYPE config [<!ENTITY xxe SYSTEM "file:///C:/Windows/win.ini">]>
                <config><moduleName>&xxe;</moduleName></config>
                """
        });

        var inspection = Mo2ArchiveInstaller.InspectFomod(archivePath);

        Assert.True(inspection.IsFomod);
        Assert.False(inspection.Success);
        Assert.Contains("XML", inspection.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InspectReadsWindows1251ModuleConfigWithoutDamagingText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(1251);
        var archivePath = CreateArchive(
            "windows-1251.zip",
            new Dictionary<string, byte[]>
            {
                ["fomod/ModuleConfig.xml"] = encoding.GetBytes(
                    "<?xml version=\"1.0\" encoding=\"windows-1251\"?><config><moduleName>Русский мастер</moduleName></config>")
            });

        var package = InspectPackage(archivePath);

        Assert.Equal("Русский мастер", package.Module.Name);
    }

    [Fact]
    public void InspectReturnsFailureForMalformedArchiveAndDuplicateMasters()
    {
        Directory.CreateDirectory(_root);
        var malformedPath = Path.Combine(_root, "malformed.zip");
        File.WriteAllText(malformedPath, "not an archive");

        var malformed = Mo2ArchiveInstaller.InspectFomod(malformedPath);
        var duplicate = Mo2ArchiveInstaller.InspectFomod(CreateArchive(
            "duplicate.zip",
            new Dictionary<string, string>
            {
                ["A/fomod/ModuleConfig.xml"] = "<config><moduleName>A</moduleName></config>",
                ["B/fomod/ModuleConfig.xml"] = "<config><moduleName>B</moduleName></config>"
            }));

        Assert.False(malformed.Success);
        Assert.False(malformed.IsFomod);
        Assert.True(duplicate.IsFomod);
        Assert.False(duplicate.Success);
        Assert.Contains("несколько", duplicate.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InspectRejectsUnsafeWrapperInvalidEnumsAndDeepDependencies()
    {
        var unsafeWrapper = Mo2ArchiveInstaller.InspectFomod(CreateArchive(
            "unsafe-wrapper.zip",
            new Dictionary<string, string>
            {
                ["../fomod/ModuleConfig.xml"] = "<config><moduleName>Unsafe</moduleName></config>"
            }));
        var invalidEnum = Mo2ArchiveInstaller.InspectFomod(CreateArchive(
            "invalid-enum.zip",
            new Dictionary<string, string>
            {
                ["fomod/ModuleConfig.xml"] = "<config><moduleName>Bad enum</moduleName><installSteps order=\"Random\" /></config>"
            }));
        var nested = "<flagDependency flag=\"x\" value=\"y\" />";
        for (var index = 0; index < 66; index++)
        {
            nested = $"<dependencies>{nested}</dependencies>";
        }
        var deep = Mo2ArchiveInstaller.InspectFomod(CreateArchive(
            "deep.zip",
            new Dictionary<string, string>
            {
                ["fomod/ModuleConfig.xml"] = $"<config><moduleName>Deep</moduleName><moduleDependencies>{nested}</moduleDependencies></config>"
            }));

        Assert.True(unsafeWrapper.IsFomod);
        Assert.False(unsafeWrapper.Success);
        Assert.False(invalidEnum.Success);
        Assert.Contains("Random", invalidEnum.Message, StringComparison.Ordinal);
        Assert.False(deep.Success);
        Assert.Contains("вложенность", deep.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadAssetEnforcesTraversalAndSizeLimits()
    {
        var package = InspectPackage(CreateArchive(
            "asset.zip",
            new Dictionary<string, string>
            {
                ["fomod/ModuleConfig.xml"] = "<config><moduleName>Asset</moduleName></config>",
                ["fomod/image.bin"] = "12345678"
            }));

        Assert.Throws<InvalidDataException>(() => FomodArchiveReader.ReadAsset(package, "../image.bin"));
        Assert.Throws<InvalidDataException>(() => FomodArchiveReader.ReadAsset(package, "fomod/image.bin", 4));
    }

    [Fact]
    public void DefaultSelectionAndEvaluationFollowFlagsVisibilityAndDependentTypes()
    {
        var package = InspectPackage(CreateWizardArchive("evaluation.zip"));

        var defaults = FomodEngine.CreateDefaultSelection(package.Module);
        var evaluation = FomodEngine.Evaluate(package.Module, defaults);

        Assert.Contains("step-0/group-0/plugin-1", defaults.SelectedPluginIds);
        var dependentStep = evaluation.Steps.Single(step => step.Step.Name == "Dependent");
        Assert.True(dependentStep.Visible);
        var dependent = Assert.Single(Assert.Single(dependentStep.Groups).Plugins);
        Assert.Equal(FomodPluginType.Required, dependent.EffectiveType);
        Assert.True(dependent.Forced);
        Assert.True(dependent.Selected);
        Assert.True(evaluation.IsValid, string.Join(" | ", evaluation.Errors));
        Assert.Equal("B", evaluation.Flags["edition"]);

        var alternate = new FomodSelection(["step-0/group-0/plugin-0"]);
        var alternateEvaluation = FomodEngine.Evaluate(package.Module, alternate);
        Assert.False(alternateEvaluation.Steps.Single(step => step.Step.Name == "Dependent").Visible);
        Assert.True(alternateEvaluation.IsValid, string.Join(" | ", alternateEvaluation.Errors));
        Assert.Equal("A", alternateEvaluation.Flags["edition"]);
    }

    [Fact]
    public void EvaluationValidatesGroupCardinalityAndUnknownIds()
    {
        var package = InspectPackage(CreateWizardArchive("invalid-selection.zip"));
        var evaluation = FomodEngine.Evaluate(
            package.Module,
            new FomodSelection(["unknown", "step-0/group-0/plugin-0", "step-0/group-0/plugin-1"]));

        Assert.False(evaluation.IsValid);
        Assert.Contains(evaluation.Errors, error => error.Contains("Неизвестный", StringComparison.Ordinal));
        Assert.Contains(evaluation.Errors, error => error.Contains("ровно один", StringComparison.Ordinal));
    }

    [Fact]
    public void DependenciesSupportNestedBooleanFileFlagAndVersions()
    {
        var dependency = new FomodCompositeDependency(
            FomodDependencyOperator.And,
            [
                new FomodFileDependency("base.esm", FomodFileState.Active),
                new FomodVersionDependency(FomodVersionDependencyKind.Game, "1.5.3"),
                new FomodVersionDependency(FomodVersionDependencyKind.Fomod, "0.13.20"),
                new FomodCompositeDependency(
                    FomodDependencyOperator.Or,
                    [
                        new FomodFlagDependency("renderer", "r4"),
                        new FomodFileDependency("fallback.esp", FomodFileState.Inactive)
                    ])
            ]);
        var context = new FomodDependencyContext(
            new Dictionary<string, FomodFileState> { ["BASE.ESM"] = FomodFileState.Active },
            GameVersion: "1.5.3.2",
            FomodVersion: "0.13.21");

        Assert.True(FomodEngine.TestDependency(
            dependency,
            context,
            new Dictionary<string, string> { ["Renderer"] = "r4" }));
        Assert.False(FomodEngine.TestDependency(
            dependency,
            context with { GameVersion = "1.5.2" },
            new Dictionary<string, string> { ["Renderer"] = "r4" }));
    }

    [Fact]
    public void PlanAppliesRequiredSelectedConditionalFolderAndPriorityRules()
    {
        var package = InspectPackage(CreatePlanningArchive("planning.zip"));
        var selection = new FomodSelection(["step-0/group-0/plugin-0"]);

        var plan = FomodEngine.BuildPlan(package, selection);

        Assert.True(plan.Success, string.Join(" | ", plan.Errors));
        Assert.Contains(plan.Files, file => file.DestinationPath == "gamedata/base.txt");
        Assert.Contains(plan.Files, file => file.DestinationPath == "gamedata/folder/one.txt");
        Assert.Contains(plan.Files, file => file.DestinationPath == "gamedata/folder/sub/two.txt");
        Assert.Contains(plan.Files, file => file.DestinationPath == "gamedata/copied-low.txt");
        Assert.Contains(plan.Files, file => file.DestinationPath == "gamedata/always.txt");
        Assert.Contains(plan.Files, file => file.DestinationPath == "gamedata/hidden-always.txt");
        Assert.Contains(plan.Files, file => file.DestinationPath == "gamedata/usable.txt");
        Assert.DoesNotContain(plan.Files, file => file.DestinationPath == "gamedata/not-usable.txt");
        var conflict = Assert.Single(plan.Files, file => file.DestinationPath == "gamedata/conflict.txt");
        Assert.EndsWith("conditional/high.txt", conflict.ArchivePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10, conflict.Priority);
    }

    [Fact]
    public void PlanHonorsCancellationBeforeArchiveExpansion()
    {
        var package = InspectPackage(CreatePlanningArchive("cancelled-plan.zip"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => FomodEngine.BuildPlan(
            package,
            new FomodSelection(["step-0/group-0/plugin-0"]),
            cancellationToken: cancellation.Token));
    }

    [Fact]
    public void InstallFomodUsesAtomicMo2PipelineAndOnlyPlannedFiles()
    {
        CreateMo2Instance();
        var archivePath = CreatePlanningArchive("Install Me.zip");
        var selection = new FomodSelection(["step-0/group-0/plugin-0"]);
        var package = InspectPackage(archivePath);
        var plan = FomodEngine.BuildPlan(package, selection);

        var result = Mo2ArchiveInstaller.InstallFomod(
            _root,
            "Anthology Стандарт",
            package,
            plan);

        Assert.True(result.Success, result.Message);
        var modRoot = Path.Combine(_root, "mods", "Install Me");
        Assert.Equal("high", File.ReadAllText(Path.Combine(modRoot, "gamedata", "conflict.txt")));
        Assert.Equal("one", File.ReadAllText(Path.Combine(modRoot, "gamedata", "folder", "one.txt")));
        Assert.Equal("low", File.ReadAllText(Path.Combine(modRoot, "gamedata", "copied-low.txt")));
        Assert.False(File.Exists(Path.Combine(modRoot, "unselected", "normal.txt")));
        Assert.True(File.Exists(Path.Combine(modRoot, "meta.ini")));
        Assert.Contains("+Install Me", File.ReadAllLines(Path.Combine(_root, "profiles", "Anthology Стандарт", "modlist.txt")));
        Assert.DoesNotContain("+Install Me", File.ReadAllLines(Path.Combine(_root, "profiles", "Anthology Стандарт", "modlist.txt.anthology-backup")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "mods"), ".__anthology_*"));
    }

    [Fact]
    public void CanceledFomodInstallLeavesProfileAndModsUnchanged()
    {
        CreateMo2Instance();
        var package = InspectPackage(CreatePlanningArchive("cancelled-install.zip"));
        var plan = FomodEngine.BuildPlan(
            package,
            new FomodSelection(["step-0/group-0/plugin-0"]));
        var modListPath = Path.Combine(_root, "profiles", "Anthology Стандарт", "modlist.txt");
        var originalModList = File.ReadAllBytes(modListPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => Mo2ArchiveInstaller.InstallFomod(
            _root,
            "Anthology Стандарт",
            package,
            plan,
            cancellationToken: cancellation.Token));

        Assert.Equal(originalModList, File.ReadAllBytes(modListPath));
        Assert.False(Directory.Exists(Path.Combine(_root, "mods", "cancelled-install")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "mods"), ".__anthology_*"));
    }

    [Fact]
    public void RegularInstallStillRefusesFomodWithoutSelection()
    {
        CreateMo2Instance();
        var archivePath = CreateWizardArchive("needs-wizard.zip");

        var result = Mo2ArchiveInstaller.Install(_root, "Anthology Стандарт", archivePath);

        Assert.False(result.Success);
        Assert.Contains("FOMOD", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_root, "mods", "needs-wizard")));
    }

    [Fact]
    public void UnsafeDestinationIsRejectedWithoutWritingOutsideMod()
    {
        CreateMo2Instance();
        var archivePath = CreateArchive("unsafe.zip", new Dictionary<string, string>
        {
            ["fomod/ModuleConfig.xml"] = """
                <config><moduleName>Unsafe</moduleName><installSteps order="Explicit"><installStep name="One"><optionalFileGroups order="Explicit"><group name="One" type="SelectAny"><plugins order="Explicit"><plugin name="Unsafe"><description>x</description><files><file source="payload.txt" destination="../escaped.txt" /></files><typeDescriptor><type name="Optional" /></typeDescriptor></plugin></plugins></group></optionalFileGroups></installStep></installSteps></config>
                """,
            ["payload.txt"] = "escape"
        });
        var package = InspectPackage(archivePath);
        var selection = new FomodSelection(["step-0/group-0/plugin-0"]);

        var plan = FomodEngine.BuildPlan(package, selection);
        var result = Mo2ArchiveInstaller.InstallFomod(_root, "Anthology Стандарт", package, plan);

        Assert.False(plan.Success);
        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, "mods", "unsafe")));
    }

    [Fact]
    public void WindowsDeviceDestinationIsRejected()
    {
        var archivePath = CreateArchive("device.zip", new Dictionary<string, string>
        {
            ["fomod/ModuleConfig.xml"] = "<config><moduleName>Device</moduleName><requiredInstallFiles><file source=\"payload.txt\" destination=\"gamedata/CON.txt\" /></requiredInstallFiles></config>",
            ["payload.txt"] = "payload"
        });

        var plan = FomodEngine.BuildPlan(InspectPackage(archivePath));

        Assert.False(plan.Success);
        Assert.Contains(plan.Errors, error => error.Contains("Windows", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RegularInstallerDoesNotTreatSimilarFolderNameAsFomod()
    {
        CreateMo2Instance();
        var archivePath = CreateArchive("similar.zip", new Dictionary<string, string>
        {
            ["notfomod/moduleconfig.xml"] = "ordinary payload"
        });

        var result = Mo2ArchiveInstaller.Install(_root, "Anthology Стандарт", archivePath);

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Path.Combine(_root, "mods", "similar", "notfomod", "moduleconfig.xml")));
    }

    [Fact]
    public void MissingSelectedSourceFailsBeforeCreatingMod()
    {
        CreateMo2Instance();
        var archivePath = CreateArchive("missing.zip", new Dictionary<string, string>
        {
            ["fomod/ModuleConfig.xml"] = """
                <config><moduleName>Missing</moduleName><requiredInstallFiles><file source="absent.txt" destination="gamedata/absent.txt" /></requiredInstallFiles></config>
                """
        });

        var package = InspectPackage(archivePath);
        var plan = FomodEngine.BuildPlan(package);
        var result = Mo2ArchiveInstaller.InstallFomod(
            _root,
            "Anthology Стандарт",
            package,
            plan);

        Assert.False(result.Success);
        Assert.Contains("absent.txt", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_root, "mods", "missing")));
    }

    [Theory]
    [InlineData("CON.txt")]
    [InlineData("file.txt:stream")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("nested//file.txt")]
    public void RegularInstallRejectsWindowsUnsafeArchiveNames(string unsafeName)
    {
        CreateMo2Instance();
        var archivePath = CreateArchive(
            $"unsafe-regular-{Guid.NewGuid():N}.zip",
            new Dictionary<string, string> { [$"gamedata/{unsafeName}"] = "payload" });

        Assert.Throws<InvalidDataException>(() =>
            Mo2ArchiveInstaller.Install(_root, "Anthology Стандарт", archivePath));
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(Path.Combine(_root, "mods")),
            path => Path.GetFileName(path).StartsWith("unsafe-regular-", StringComparison.Ordinal));
    }

    [Fact]
    public void RegularInstallEnforcesExpandedAndCompressionRatioLimits()
    {
        CreateMo2Instance();
        var archivePath = CreateArchive(
            "oversized.zip",
            new Dictionary<string, byte[]>
            {
                ["gamedata/large.bin"] = new byte[1024 * 1024]
            });
        var limits = new Mo2ArchiveExtractionLimits(
            MaxExpandedBytes: 4096,
            MaxSingleEntryBytes: 4096,
            MaxCompressionRatio: 2,
            MinimumRatioAllowanceBytes: 0,
            MinimumFreeSpaceReserveBytes: 0);

        Assert.Throws<InvalidDataException>(() => Mo2ArchiveInstaller.Install(
            _root,
            "Anthology Стандарт",
            archivePath,
            extractionLimits: limits));
        Assert.False(Directory.Exists(Path.Combine(_root, "mods", "oversized")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "mods"), ".__anthology_*"));
    }

    [Fact]
    public void RegularInstallEnforcesFreeSpaceReserveBeforeStaging()
    {
        CreateMo2Instance();
        var archivePath = CreateArchive("no-space.zip", new Dictionary<string, string>
        {
            ["gamedata/file.txt"] = "payload"
        });
        var limits = new Mo2ArchiveExtractionLimits(
            MaxExpandedBytes: long.MaxValue,
            MaxSingleEntryBytes: long.MaxValue,
            MaxCompressionRatio: double.MaxValue,
            MinimumRatioAllowanceBytes: 0,
            MinimumFreeSpaceReserveBytes: long.MaxValue);

        Assert.Throws<IOException>(() => Mo2ArchiveInstaller.Install(
            _root,
            "Anthology Стандарт",
            archivePath,
            extractionLimits: limits));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "mods"), ".__anthology_*"));
    }

    [Fact]
    public void InstallFomodRejectsPlanFromAnotherInspectedArchive()
    {
        CreateMo2Instance();
        var first = InspectPackage(CreatePlanningArchive("first.zip"));
        var second = InspectPackage(CreatePlanningArchive("second.zip"));
        var foreignPlan = FomodEngine.BuildPlan(
            second,
            new FomodSelection(["step-0/group-0/plugin-0"]));

        var result = Mo2ArchiveInstaller.InstallFomod(
            _root,
            "Anthology Стандарт",
            first,
            foreignPlan);

        Assert.False(result.Success);
        Assert.Contains("другому архиву", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_root, "mods", "first")));
    }

    [Fact]
    public void InstallFomodRejectsCopiedOrMutatedReviewedPlan()
    {
        CreateMo2Instance();
        var package = InspectPackage(CreatePlanningArchive("tampered-plan.zip"));
        var reviewedPlan = FomodEngine.BuildPlan(
            package,
            new FomodSelection(["step-0/group-0/plugin-0"]));
        var copiedPlan = reviewedPlan with
        {
            Files =
            [
                new FomodPlannedFile(
                    reviewedPlan.Files[0].ArchivePath,
                    "gamedata/injected.txt",
                    int.MaxValue,
                    int.MaxValue)
            ]
        };

        var result = Mo2ArchiveInstaller.InstallFomod(
            _root,
            "Anthology Стандарт",
            package,
            copiedPlan);

        Assert.False(result.Success);
        Assert.Contains("изменён", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_root, "mods", "tampered-plan")));
    }

    [Fact]
    public void InspectedPackageKeepsArchiveImmutableUntilDisposed()
    {
        var package = InspectPackage(CreatePlanningArchive("leased.zip"));

        Assert.Throws<IOException>(() => new FileStream(
            package.ArchivePath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite).Dispose());
    }

    [Fact]
    public void ReadAssetsLoadsSeveralFilesAndReusesPackageCache()
    {
        var package = InspectPackage(CreateArchive(
            "assets-batch.zip",
            new Dictionary<string, string>
            {
                ["fomod/ModuleConfig.xml"] = "<config><moduleName>Images</moduleName></config>",
                ["fomod/one.png"] = "one",
                ["fomod/two.png"] = "two"
            }));

        var first = FomodArchiveReader.ReadAssets(
            package,
            ["fomod/one.png", "fomod/two.png"]);
        var cached = FomodArchiveReader.ReadAssets(package, ["fomod/two.png"]);

        Assert.Equal("one", Encoding.UTF8.GetString(first["fomod/one.png"]));
        Assert.Equal("two", Encoding.UTF8.GetString(first["fomod/two.png"]));
        Assert.Same(first["fomod/two.png"], cached["fomod/two.png"]);
    }

    [Fact]
    public void FailedProfileMutationRollsBackReplacementAndModList()
    {
        CreateMo2Instance();
        var modRoot = Path.Combine(_root, "mods", "Rollback");
        Directory.CreateDirectory(modRoot);
        File.WriteAllText(Path.Combine(modRoot, "old.txt"), "old");
        var modListPath = Path.Combine(_root, "profiles", "Anthology Стандарт", "modlist.txt");
        var originalModList = File.ReadAllBytes(modListPath);
        Directory.CreateDirectory(modListPath + ".anthology-backup");
        var archivePath = CreateArchive("rollback.zip", new Dictionary<string, string>
        {
            ["gamedata/new.txt"] = "new"
        });

        Assert.ThrowsAny<Exception>(() => Mo2ArchiveInstaller.Install(
            _root,
            "Anthology Стандарт",
            archivePath,
            installName: "Rollback",
            replaceExisting: true));

        Assert.Equal("old", File.ReadAllText(Path.Combine(modRoot, "old.txt")));
        Assert.False(File.Exists(Path.Combine(modRoot, "gamedata", "new.txt")));
        Assert.Equal(originalModList, File.ReadAllBytes(modListPath));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "mods"), ".__anthology_*"));
    }

    private string CreateWizardArchive(string fileName) => CreateArchive(fileName, new Dictionary<string, string>
    {
        ["fomod/ModuleConfig.xml"] = """
            <config>
              <moduleName>Choice test</moduleName>
              <installSteps order="Explicit">
                <installStep name="Base">
                  <optionalFileGroups order="Explicit">
                    <group name="Edition" type="SelectExactlyOne">
                      <plugins order="Explicit">
                        <plugin name="A"><description>A</description><conditionFlags><flag name="edition">A</flag></conditionFlags><typeDescriptor><type name="Optional" /></typeDescriptor></plugin>
                        <plugin name="B"><description>B</description><conditionFlags><flag name="edition">B</flag></conditionFlags><typeDescriptor><type name="Recommended" /></typeDescriptor></plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
                <installStep name="Dependent">
                  <visible><flagDependency flag="edition" value="B" /></visible>
                  <optionalFileGroups order="Explicit">
                    <group name="Dependency" type="SelectAny">
                      <plugins order="Explicit">
                        <plugin name="Patch">
                          <description>Patch</description>
                          <typeDescriptor><dependencyType><defaultType name="NotUsable" /><patterns><pattern><dependencies><flagDependency flag="edition" value="B" /></dependencies><type name="Required" /></pattern></patterns></dependencyType></typeDescriptor>
                        </plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """
    });

    private string CreatePlanningArchive(string fileName) => CreateArchive(fileName, new Dictionary<string, string>
    {
        ["Pack/fomod/ModuleConfig.xml"] = """
            <config>
              <moduleName>Planning</moduleName>
              <requiredInstallFiles><file source="required/base.txt" destination="gamedata/base.txt" /></requiredInstallFiles>
              <installSteps order="Explicit">
                <installStep name="Options">
                  <optionalFileGroups order="Explicit">
                    <group name="Files" type="SelectAny">
                      <plugins order="Explicit">
                        <plugin name="Selected">
                          <description>selected</description>
                          <files>
                            <file source="selected/low.txt" destination="gamedata/conflict.txt" priority="1" />
                            <file source="selected/low.txt" destination="gamedata/copied-low.txt" priority="1" />
                            <folder source="selected/folder" destination="gamedata/folder" priority="2" />
                          </files>
                          <conditionFlags><flag name="edition">selected</flag></conditionFlags>
                          <typeDescriptor><type name="Optional" /></typeDescriptor>
                        </plugin>
                        <plugin name="Automatic">
                          <description>automatic</description>
                          <files>
                            <file source="unselected/normal.txt" destination="unselected/normal.txt" />
                            <file source="unselected/always.txt" destination="gamedata/always.txt" alwaysInstall="true" />
                            <file source="unselected/usable.txt" destination="gamedata/usable.txt" installIfUsable="true" />
                          </files>
                          <typeDescriptor><type name="Optional" /></typeDescriptor>
                        </plugin>
                        <plugin name="Unavailable">
                          <description>unavailable</description>
                          <files><file source="unselected/not-usable.txt" destination="gamedata/not-usable.txt" installIfUsable="true" /></files>
                          <typeDescriptor><type name="NotUsable" /></typeDescriptor>
                        </plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
                <installStep name="Hidden">
                  <visible><flagDependency flag="never" value="true" /></visible>
                  <optionalFileGroups order="Explicit">
                    <group name="Hidden files" type="SelectAny">
                      <plugins order="Explicit"><plugin name="Hidden automatic"><description>hidden</description><files><file source="unselected/hidden-always.txt" destination="gamedata/hidden-always.txt" alwaysInstall="true" /></files><typeDescriptor><type name="Optional" /></typeDescriptor></plugin></plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
              <conditionalFileInstalls><patterns><pattern><dependencies><flagDependency flag="edition" value="selected" /></dependencies><files><file source="conditional/high.txt" destination="gamedata/conflict.txt" priority="10" /></files></pattern></patterns></conditionalFileInstalls>
            </config>
            """,
        ["Pack/required/base.txt"] = "base",
        ["Pack/selected/low.txt"] = "low",
        ["Pack/selected/folder/one.txt"] = "one",
        ["Pack/selected/folder/sub/two.txt"] = "two",
        ["Pack/unselected/normal.txt"] = "normal",
        ["Pack/unselected/always.txt"] = "always",
        ["Pack/unselected/hidden-always.txt"] = "hidden always",
        ["Pack/unselected/usable.txt"] = "usable",
        ["Pack/unselected/not-usable.txt"] = "not usable",
        ["Pack/conditional/high.txt"] = "high"
    });

    private FomodPackage InspectPackage(string archivePath)
    {
        var inspection = Mo2ArchiveInstaller.InspectFomod(archivePath);
        Assert.True(inspection.Success, inspection.Message);
        var package = Assert.IsType<FomodPackage>(inspection.Package);
        _packages.Add(package);
        return package;
    }

    private string CreateArchive(string fileName, IReadOnlyDictionary<string, string> entries)
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, fileName);
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (var pair in entries)
        {
            var entry = archive.CreateEntry(pair.Key);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(pair.Value);
        }
        return archivePath;
    }

    private string CreateArchive(string fileName, IReadOnlyDictionary<string, byte[]> entries)
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, fileName);
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (var pair in entries)
        {
            var entry = archive.CreateEntry(pair.Key);
            using var output = entry.Open();
            output.Write(pair.Value);
        }
        return archivePath;
    }

    private void CreateMo2Instance()
    {
        var profile = Path.Combine(_root, "profiles", "Anthology Стандарт");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        File.WriteAllText(Path.Combine(_root, "ModOrganizer.exe"), string.Empty);
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# generated\n");
        File.WriteAllText(Path.Combine(_root, "ModOrganizer.ini"), "[General]\n");
    }

    public void Dispose()
    {
        foreach (var package in _packages)
        {
            package.Dispose();
        }
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
