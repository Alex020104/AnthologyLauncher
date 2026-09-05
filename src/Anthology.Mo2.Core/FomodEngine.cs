using System.Globalization;

namespace Anthology.Mo2.Core;

public static class FomodEngine
{
    public static FomodEvaluation Evaluate(
        FomodModule module,
        FomodSelection? selection = null,
        FomodDependencyContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        selection ??= FomodSelection.Empty;
        ArgumentNullException.ThrowIfNull(selection.SelectedPluginIds);
        context ??= FomodDependencyContext.Empty;

        var selectedIds = new HashSet<string>(selection.SelectedPluginIds, StringComparer.Ordinal);
        var knownIds = module.Steps
            .SelectMany(step => step.Groups)
            .SelectMany(group => group.Plugins)
            .Select(plugin => plugin.Id)
            .ToHashSet(StringComparer.Ordinal);
        var errors = selectedIds
            .Where(id => !knownIds.Contains(id))
            .Select(id => $"Неизвестный компонент FOMOD: {id}")
            .ToList();

        var flags = CopyFlags(context.InitialFlags);
        var moduleDependenciesSatisfied = module.Dependencies is null
                                          || TestDependency(module.Dependencies, context, flags);
        if (!moduleDependenciesSatisfied)
        {
            errors.Add("Не выполнены обязательные зависимости FOMOD.");
        }

        var stepEvaluations = new List<FomodStepEvaluation>(module.Steps.Count);
        foreach (var step in module.Steps)
        {
            var visible = moduleDependenciesSatisfied
                          && (step.Visibility is null || TestDependency(step.Visibility, context, flags));
            var groupEvaluations = new List<FomodGroupEvaluation>(step.Groups.Count);
            foreach (var group in step.Groups)
            {
                var typedPlugins = group.Plugins
                    .Select(plugin => (Plugin: plugin, Type: GetEffectiveType(plugin, context, flags)))
                    .ToArray();
                var radioRequired = group.Type is FomodGroupType.SelectAtMostOne or FomodGroupType.SelectExactlyOne
                                    && typedPlugins.Any(value => value.Type == FomodPluginType.Required);
                var pluginEvaluations = new List<FomodPluginEvaluation>(typedPlugins.Length);
                foreach (var value in typedPlugins)
                {
                    var forced = visible
                                 && (group.Type == FomodGroupType.SelectAll
                                     || value.Type == FomodPluginType.Required);
                    var selectable = visible
                                     && !forced
                                     && value.Type != FomodPluginType.NotUsable
                                     && !(radioRequired && value.Type != FomodPluginType.Required);
                    var selected = visible
                                   && (forced
                                       || (selectable && selectedIds.Contains(value.Plugin.Id)));
                    pluginEvaluations.Add(new FomodPluginEvaluation(
                        value.Plugin,
                        value.Type,
                        selected,
                        selectable,
                        forced));
                }

                if (visible)
                {
                    ValidateGroup(group, pluginEvaluations, errors);
                }
                groupEvaluations.Add(new FomodGroupEvaluation(group, pluginEvaluations));
            }

            var stepEvaluation = new FomodStepEvaluation(step, visible, groupEvaluations);
            stepEvaluations.Add(stepEvaluation);
            if (visible)
            {
                ApplyStepFlags(stepEvaluation, flags);
            }
        }

        return new FomodEvaluation(
            moduleDependenciesSatisfied,
            stepEvaluations,
            new Dictionary<string, string>(flags, StringComparer.OrdinalIgnoreCase),
            errors);
    }

    public static FomodSelection CreateDefaultSelection(
        FomodModule module,
        FomodDependencyContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        context ??= FomodDependencyContext.Empty;
        if (module.Dependencies is not null
            && !TestDependency(module.Dependencies, context, CopyFlags(context.InitialFlags)))
        {
            return FomodSelection.Empty;
        }

        var selected = new HashSet<string>(StringComparer.Ordinal);
        var flags = CopyFlags(context.InitialFlags);
        foreach (var step in module.Steps)
        {
            if (step.Visibility is not null && !TestDependency(step.Visibility, context, flags))
            {
                continue;
            }

            var selectedOnStep = new List<FomodPlugin>();
            foreach (var group in step.Groups)
            {
                var values = group.Plugins
                    .Select(plugin => (Plugin: plugin, Type: GetEffectiveType(plugin, context, flags)))
                    .ToArray();
                var groupDefaults = DefaultPlugins(group.Type, values).ToArray();
                foreach (var plugin in groupDefaults)
                {
                    selected.Add(plugin.Id);
                    selectedOnStep.Add(plugin);
                }
            }
            ApplyFlags(selectedOnStep, flags);
        }
        return new FomodSelection(selected.ToArray());
    }

    public static FomodInstallPlan BuildPlan(
        FomodPackage package,
        FomodSelection? selection = null,
        FomodDependencyContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        package.ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        context ??= FomodDependencyContext.Empty;
        var evaluation = Evaluate(package.Module, selection, context);
        if (!evaluation.IsValid)
        {
            return package.BindPlan(
                new FomodInstallPlan(false, Array.Empty<FomodPlannedFile>(), evaluation.Errors)
                {
                    InspectionId = package.InspectionId
                });
        }

        var mappings = new List<FomodFileMapping>();
        mappings.AddRange(package.Module.RequiredFiles);
        foreach (var step in evaluation.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var group in step.Groups)
            {
                foreach (var plugin in group.Plugins)
                {
                    foreach (var file in plugin.Plugin.Files)
                    {
                        if (plugin.Selected
                            || file.AlwaysInstall
                            || (file.InstallIfUsable && plugin.EffectiveType != FomodPluginType.NotUsable))
                        {
                            mappings.Add(file);
                        }
                    }
                }
            }
        }

        foreach (var conditional in package.Module.ConditionalInstalls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TestDependency(conditional.Dependencies, context, evaluation.Flags))
            {
                mappings.AddRange(conditional.Files);
            }
        }

        var plannedByDestination = new Dictionary<string, FomodPlannedFile>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var mapping in mappings
                     .OrderBy(file => file.Priority)
                     .ThenBy(file => file.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var expanded = ExpandMapping(package, mapping, cancellationToken).ToArray();
                if (expanded.Length == 0)
                {
                    errors.Add($"Источник FOMOD отсутствует в архиве: {mapping.Source}");
                    continue;
                }
                foreach (var file in expanded)
                {
                    plannedByDestination[file.DestinationPath] = file;
                }
            }
            catch (InvalidDataException exception)
            {
                errors.Add(exception.Message);
            }
        }

        if (errors.Count > 0)
        {
            return package.BindPlan(
                new FomodInstallPlan(false, Array.Empty<FomodPlannedFile>(), errors)
                {
                    InspectionId = package.InspectionId
                });
        }

        return package.BindPlan(
            new FomodInstallPlan(
                true,
                plannedByDestination.Values
                    .OrderBy(file => file.Priority)
                    .ThenBy(file => file.Sequence)
                    .ThenBy(file => file.DestinationPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                Array.Empty<string>())
            {
                InspectionId = package.InspectionId
            });
    }

    public static bool TestDependency(
        FomodDependency dependency,
        FomodDependencyContext? context = null,
        IReadOnlyDictionary<string, string>? flags = null)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        context ??= FomodDependencyContext.Empty;
        flags ??= context.InitialFlags ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return dependency switch
        {
            FomodCompositeDependency composite => TestComposite(composite, context, flags),
            FomodFlagDependency flag => TestFlag(flag, flags),
            FomodFileDependency file => GetFileState(context, file.File) == file.State,
            FomodVersionDependency version => TestVersion(version, context),
            _ => false
        };
    }

    private static IEnumerable<FomodPlannedFile> ExpandMapping(
        FomodPackage package,
        FomodFileMapping mapping,
        CancellationToken cancellationToken)
    {
        var source = FomodPath.NormalizeRelativePath(mapping.Source, allowEmpty: false);
        var destinationEndsWithSeparator = mapping.Destination.EndsWith('/')
                                           || mapping.Destination.EndsWith('\\');
        var destination = FomodPath.NormalizeRelativePath(mapping.Destination, allowEmpty: true);
        var archiveSource = package.ContentPrefix + source;

        if (mapping.IsFolder)
        {
            var sourcePrefix = archiveSource.TrimEnd('/') + "/";
            foreach (var archiveFile in package.ArchiveFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!archiveFile.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var relative = archiveFile[sourcePrefix.Length..];
                if (relative.Length == 0)
                {
                    continue;
                }
                var target = CombineRelative(destination, relative);
                yield return new FomodPlannedFile(
                    archiveFile,
                    FomodPath.NormalizeRelativePath(target, allowEmpty: false),
                    mapping.Priority,
                    mapping.Sequence);
            }
            yield break;
        }

        var sourceFile = FomodArchiveReader.FindArchiveFile(package.ArchiveFiles, archiveSource);
        if (sourceFile is null)
        {
            yield break;
        }
        if (destination.Length == 0 || destinationEndsWithSeparator)
        {
            destination = CombineRelative(destination, source.Split('/')[^1]);
        }
        yield return new FomodPlannedFile(
            sourceFile,
            FomodPath.NormalizeRelativePath(destination, allowEmpty: false),
            mapping.Priority,
            mapping.Sequence);
    }

    private static string CombineRelative(string left, string right) =>
        left.Length == 0 ? right : left.TrimEnd('/') + "/" + right.TrimStart('/');

    private static void ValidateGroup(
        FomodGroup group,
        List<FomodPluginEvaluation> plugins,
        List<string> errors)
    {
        var selectedCount = plugins.Count(plugin => plugin.Selected);
        var valid = group.Type switch
        {
            FomodGroupType.SelectAtLeastOne => selectedCount >= 1,
            FomodGroupType.SelectAtMostOne => selectedCount <= 1,
            FomodGroupType.SelectExactlyOne => selectedCount == 1,
            FomodGroupType.SelectAll => selectedCount == plugins.Count,
            _ => true
        };
        if (!valid)
        {
            errors.Add(group.Type switch
            {
                FomodGroupType.SelectAtLeastOne => $"В группе «{group.Name}» нужно выбрать хотя бы один компонент.",
                FomodGroupType.SelectAtMostOne => $"В группе «{group.Name}» можно выбрать не более одного компонента.",
                FomodGroupType.SelectExactlyOne => $"В группе «{group.Name}» нужно выбрать ровно один компонент.",
                FomodGroupType.SelectAll => $"В группе «{group.Name}» обязательны все компоненты.",
                _ => $"Некорректный выбор в группе «{group.Name}»."
            });
        }
    }

    private static IEnumerable<FomodPlugin> DefaultPlugins(
        FomodGroupType groupType,
        IReadOnlyList<(FomodPlugin Plugin, FomodPluginType Type)> values)
    {
        var usable = values.Where(value => value.Type != FomodPluginType.NotUsable).ToArray();
        if (groupType == FomodGroupType.SelectAll)
        {
            return values.Select(value => value.Plugin);
        }

        var required = usable.Where(value => value.Type == FomodPluginType.Required).ToArray();
        var recommended = usable.Where(value => value.Type == FomodPluginType.Recommended).ToArray();
        if (groupType is FomodGroupType.SelectExactlyOne or FomodGroupType.SelectAtMostOne)
        {
            var single = required.FirstOrDefault();
            if (single.Plugin is not null)
            {
                return new[] { single.Plugin };
            }
            single = recommended.FirstOrDefault();
            if (single.Plugin is not null)
            {
                return new[] { single.Plugin };
            }
            if (groupType == FomodGroupType.SelectAtMostOne)
            {
                return Array.Empty<FomodPlugin>();
            }
            single = usable.FirstOrDefault(value => value.Type == FomodPluginType.Optional);
            if (single.Plugin is null)
            {
                single = usable.FirstOrDefault();
            }
            return single.Plugin is null ? Array.Empty<FomodPlugin>() : new[] { single.Plugin };
        }

        var defaults = required.Concat(recommended).Select(value => value.Plugin).ToList();
        if (groupType == FomodGroupType.SelectAtLeastOne && defaults.Count == 0)
        {
            var fallback = usable.FirstOrDefault(value => value.Type == FomodPluginType.Optional);
            if (fallback.Plugin is null)
            {
                fallback = usable.FirstOrDefault();
            }
            if (fallback.Plugin is not null)
            {
                defaults.Add(fallback.Plugin);
            }
        }
        return defaults;
    }

    private static FomodPluginType GetEffectiveType(
        FomodPlugin plugin,
        FomodDependencyContext context,
        IReadOnlyDictionary<string, string> flags)
    {
        foreach (var pattern in plugin.TypeDescriptor.Patterns)
        {
            if (TestDependency(pattern.Dependencies, context, flags))
            {
                return pattern.Type;
            }
        }
        return plugin.TypeDescriptor.DefaultType;
    }

    private static void ApplyStepFlags(
        FomodStepEvaluation step,
        IDictionary<string, string> flags)
    {
        ApplyFlags(
            step.Groups
                .SelectMany(group => group.Plugins)
                .Where(plugin => plugin.Selected)
                .Select(plugin => plugin.Plugin),
            flags);
    }

    private static void ApplyFlags(
        IEnumerable<FomodPlugin> plugins,
        IDictionary<string, string> flags)
    {
        var assignedOnStep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in plugins)
        {
            foreach (var flag in plugin.ConditionFlags)
            {
                if (assignedOnStep.Add(flag.Name))
                {
                    flags[flag.Name] = flag.Value;
                }
            }
        }
    }

    private static bool TestComposite(
        FomodCompositeDependency composite,
        FomodDependencyContext context,
        IReadOnlyDictionary<string, string> flags)
    {
        return composite.Operator == FomodDependencyOperator.And
            ? composite.Dependencies.All(dependency => TestDependency(dependency, context, flags))
            : composite.Dependencies.Any(dependency => TestDependency(dependency, context, flags));
    }

    private static bool TestFlag(
        FomodFlagDependency dependency,
        IReadOnlyDictionary<string, string> flags)
    {
        var value = FindValue(flags, dependency.Flag);
        return value is null ? dependency.Value.Length == 0 : value.Equals(dependency.Value, StringComparison.Ordinal);
    }

    private static FomodFileState GetFileState(FomodDependencyContext context, string file)
    {
        if (context.FileStates is null)
        {
            return FomodFileState.Missing;
        }
        foreach (var pair in context.FileStates)
        {
            if (pair.Key.Equals(file, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }
        return FomodFileState.Missing;
    }

    private static bool TestVersion(FomodVersionDependency dependency, FomodDependencyContext context)
    {
        var installed = dependency.Kind switch
        {
            FomodVersionDependencyKind.Game => context.GameVersion,
            FomodVersionDependencyKind.Fomod => context.FomodVersion,
            FomodVersionDependencyKind.ScriptExtender => context.ScriptExtenderVersion,
            _ => null
        };
        return installed is not null && CompareVersions(installed, dependency.MinimumVersion) >= 0;
    }

    private static int CompareVersions(string left, string right)
    {
        var leftParts = VersionParts(left);
        var rightParts = VersionParts(right);
        var count = Math.Max(leftParts.Count, rightParts.Count);
        for (var index = 0; index < count; index++)
        {
            var leftValue = index < leftParts.Count ? leftParts[index] : 0;
            var rightValue = index < rightParts.Count ? rightParts[index] : 0;
            var comparison = leftValue.CompareTo(rightValue);
            if (comparison != 0)
            {
                return comparison;
            }
        }
        return 0;
    }

    private static IReadOnlyList<long> VersionParts(string value)
    {
        var result = new List<long>();
        var current = new List<char>();
        foreach (var character in value)
        {
            if (char.IsDigit(character))
            {
                current.Add(character);
                continue;
            }
            FlushVersionPart(current, result);
        }
        FlushVersionPart(current, result);
        return result.Count == 0 ? new long[] { 0 } : result;
    }

    private static void FlushVersionPart(List<char> current, List<long> result)
    {
        if (current.Count == 0)
        {
            return;
        }
        var text = new string(current.ToArray());
        result.Add(long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : long.MaxValue);
        current.Clear();
    }

    private static Dictionary<string, string> CopyFlags(IReadOnlyDictionary<string, string>? source)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source is not null)
        {
            foreach (var pair in source)
            {
                result[pair.Key] = pair.Value;
            }
        }
        return result;
    }

    private static string? FindValue(IReadOnlyDictionary<string, string> values, string key)
    {
        foreach (var pair in values)
        {
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }
        return null;
    }
}
