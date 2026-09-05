namespace Anthology.Mo2.Core;

public enum FomodOrder
{
    Ascending,
    Descending,
    Explicit
}

public enum FomodGroupType
{
    SelectAtLeastOne,
    SelectAtMostOne,
    SelectExactlyOne,
    SelectAny,
    SelectAll
}

public enum FomodPluginType
{
    Required,
    Recommended,
    Optional,
    NotUsable,
    CouldBeUsable
}

public enum FomodDependencyOperator
{
    And,
    Or
}

public enum FomodFileState
{
    Missing,
    Inactive,
    Active
}

public enum FomodVersionDependencyKind
{
    Game,
    Fomod,
    ScriptExtender
}

public abstract record FomodDependency;

public sealed record FomodCompositeDependency(
    FomodDependencyOperator Operator,
    IReadOnlyList<FomodDependency> Dependencies) : FomodDependency;

public sealed record FomodFlagDependency(string Flag, string Value) : FomodDependency;

public sealed record FomodFileDependency(string File, FomodFileState State) : FomodDependency;

public sealed record FomodVersionDependency(
    FomodVersionDependencyKind Kind,
    string MinimumVersion) : FomodDependency;

public sealed record FomodFileMapping(
    string Source,
    string Destination,
    bool IsFolder,
    int Priority,
    bool AlwaysInstall,
    bool InstallIfUsable,
    int Sequence);

public sealed record FomodConditionFlag(string Name, string Value);

public sealed record FomodDependencyPattern(
    FomodCompositeDependency Dependencies,
    FomodPluginType Type);

public sealed record FomodPluginTypeDescriptor(
    FomodPluginType DefaultType,
    IReadOnlyList<FomodDependencyPattern> Patterns);

public sealed record FomodPlugin(
    string Id,
    string Name,
    string Description,
    string? ImagePath,
    IReadOnlyList<FomodFileMapping> Files,
    IReadOnlyList<FomodConditionFlag> ConditionFlags,
    FomodPluginTypeDescriptor TypeDescriptor,
    int DeclarationIndex);

public sealed record FomodGroup(
    string Id,
    string Name,
    FomodGroupType Type,
    FomodOrder Order,
    IReadOnlyList<FomodPlugin> Plugins,
    int DeclarationIndex);

public sealed record FomodStep(
    string Id,
    string Name,
    FomodCompositeDependency? Visibility,
    FomodOrder GroupOrder,
    IReadOnlyList<FomodGroup> Groups,
    int DeclarationIndex);

public sealed record FomodConditionalInstall(
    FomodCompositeDependency Dependencies,
    IReadOnlyList<FomodFileMapping> Files);

public sealed record FomodMetadata(
    string? Name,
    string? Author,
    string? Version,
    string? Website,
    string? Description,
    string? Id);

public sealed record FomodModule(
    string Name,
    string? ImagePath,
    bool ShowImage,
    FomodCompositeDependency? Dependencies,
    IReadOnlyList<FomodFileMapping> RequiredFiles,
    FomodOrder StepOrder,
    IReadOnlyList<FomodStep> Steps,
    IReadOnlyList<FomodConditionalInstall> ConditionalInstalls);

public sealed class FomodPackage : IDisposable
{
    private readonly FileStream _archiveLease;
    private readonly object _assetCacheLock = new();
    private readonly Dictionary<string, byte[]> _assetCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _planLock = new();
    private FomodInstallPlan? _issuedPlan;
    private PlanSnapshot? _issuedPlanSnapshot;
    private long _cachedAssetBytes;
    private bool _disposed;

    internal FomodPackage(
        string archivePath,
        string contentPrefix,
        string moduleConfigArchivePath,
        FomodModule module,
        FomodMetadata metadata,
        IReadOnlyList<string> archiveFiles,
        FileStream archiveLease)
    {
        ArchivePath = archivePath;
        ContentPrefix = contentPrefix;
        ModuleConfigArchivePath = moduleConfigArchivePath;
        Module = module;
        Metadata = metadata;
        ArchiveFiles = archiveFiles;
        _archiveLease = archiveLease;
        InspectionId = Guid.NewGuid();
    }

    public string ArchivePath { get; }

    public string ContentPrefix { get; }

    public string ModuleConfigArchivePath { get; }

    public FomodModule Module { get; }

    public FomodMetadata Metadata { get; }

    internal IReadOnlyList<string> ArchiveFiles { get; }

    internal Guid InspectionId { get; }

    internal object AssetCacheLock => _assetCacheLock;

    internal bool TryGetCachedAsset(string path, out byte[] bytes)
    {
        ThrowIfDisposed();
        return _assetCache.TryGetValue(path, out bytes!);
    }

    internal bool TryCacheAsset(string path, byte[] bytes, long maxCachedBytes)
    {
        ThrowIfDisposed();
        if (_assetCache.ContainsKey(path))
        {
            return true;
        }
        if (bytes.LongLength > maxCachedBytes - _cachedAssetBytes)
        {
            return false;
        }

        _assetCache[path] = bytes;
        _cachedAssetBytes += bytes.LongLength;
        return true;
    }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal FomodInstallPlan BindPlan(FomodInstallPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (_planLock)
        {
            ThrowIfDisposed();
            _issuedPlan = plan;
            _issuedPlanSnapshot = new PlanSnapshot(
                plan.Success,
                plan.Files.ToArray(),
                plan.Errors.ToArray());
        }
        return plan;
    }

    internal bool IsBoundPlanUnchanged(FomodInstallPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (_planLock)
        {
            ThrowIfDisposed();
            var snapshot = _issuedPlanSnapshot;
            return ReferenceEquals(_issuedPlan, plan)
                   && snapshot is not null
                   && snapshot.Success == plan.Success
                   && snapshot.Files.SequenceEqual(plan.Files)
                   && snapshot.Errors.SequenceEqual(plan.Errors, StringComparer.Ordinal);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_assetCacheLock)
        {
            _assetCache.Clear();
            _cachedAssetBytes = 0;
        }
        lock (_planLock)
        {
            _issuedPlan = null;
            _issuedPlanSnapshot = null;
        }
        _archiveLease.Dispose();
    }

    private sealed record PlanSnapshot(
        bool Success,
        FomodPlannedFile[] Files,
        string[] Errors);
}

public sealed record FomodArchiveInspection(
    bool IsFomod,
    string Message,
    FomodPackage? Package = null)
{
    public bool Success => IsFomod && Package is not null;
}

public sealed record FomodSelection(IReadOnlyCollection<string> SelectedPluginIds)
{
    public static FomodSelection Empty { get; } = new(Array.Empty<string>());
}

public sealed record FomodDependencyContext(
    IReadOnlyDictionary<string, FomodFileState>? FileStates = null,
    IReadOnlyDictionary<string, string>? InitialFlags = null,
    string? GameVersion = null,
    string? FomodVersion = "0.13.21",
    string? ScriptExtenderVersion = null)
{
    public static FomodDependencyContext Empty { get; } = new();
}

public sealed record FomodPluginEvaluation(
    FomodPlugin Plugin,
    FomodPluginType EffectiveType,
    bool Selected,
    bool Selectable,
    bool Forced);

public sealed record FomodGroupEvaluation(
    FomodGroup Group,
    IReadOnlyList<FomodPluginEvaluation> Plugins);

public sealed record FomodStepEvaluation(
    FomodStep Step,
    bool Visible,
    IReadOnlyList<FomodGroupEvaluation> Groups);

public sealed record FomodEvaluation(
    bool ModuleDependenciesSatisfied,
    IReadOnlyList<FomodStepEvaluation> Steps,
    IReadOnlyDictionary<string, string> Flags,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => ModuleDependenciesSatisfied && Errors.Count == 0;
}

public sealed record FomodPlannedFile(
    string ArchivePath,
    string DestinationPath,
    int Priority,
    int Sequence);

public sealed record FomodInstallPlan(
    bool Success,
    IReadOnlyList<FomodPlannedFile> Files,
    IReadOnlyList<string> Errors)
{
    internal Guid InspectionId { get; init; }
}
