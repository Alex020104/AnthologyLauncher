using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Anthology.Mo2.Core;

public static class FomodArchiveReader
{
    private const int MaxConfigurationBytes = 8 * 1024 * 1024;
    private const int MaxArchiveEntries = 1_000_000;
    private const long MaxCachedAssetBytes = 18L * 1024 * 1024;
    private const long MaxNativeInspectionCacheBytes = 128L * 1024 * 1024;
    private const int MaxNativeInspectionCacheEntries = 258;

    public static FomodArchiveInspection Inspect(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath))
        {
            return new FomodArchiveInspection(false, $"Архив не найден: {archivePath}");
        }

        var isFomod = false;
        FileStream? archiveLease = null;
        NativeSevenZipCache? nativeCache = null;
        try
        {
            // Keep a non-writable, non-deletable handle for the lifetime of the
            // wizard. The reviewed package and plan must describe the exact file
            // later extracted by the installer.
            archiveLease = OpenArchiveStream(archivePath);
            var archiveEntries = ArchiveFileAccess.ReadFileEntries(archivePath, cancellationToken);
            var archiveFiles = archiveEntries.Select(entry => entry.Path).ToArray();
            var candidates = archiveFiles
                .Where(IsModuleConfigPath)
                .OrderBy(path => path.Count(character => character == '/'))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (candidates.Length == 0)
            {
                return new FomodArchiveInspection(false, "В архиве нет fomod/ModuleConfig.xml.");
            }
            isFomod = true;
            if (candidates.Length > 1)
            {
                return new FomodArchiveInspection(
                    true,
                    "В архиве найдено несколько мастеров FOMOD. Установка неоднозначна.");
            }

            var moduleConfigPath = candidates[0];
            var suffixOffset = moduleConfigPath.LastIndexOf("fomod/moduleconfig.xml", StringComparison.OrdinalIgnoreCase);
            var contentPrefix = suffixOffset <= 0 ? string.Empty : moduleConfigPath[..suffixOffset];
            if (contentPrefix.Length > 0)
            {
                _ = FomodPath.NormalizeRelativePath(contentPrefix, allowEmpty: false);
            }

            var infoPath = FindArchiveFile(archiveFiles, contentPrefix + "fomod/info.xml");
            if (ArchiveFileAccess.UsesNativeSevenZip(archivePath))
            {
                var moduleEntry = archiveEntries.Single(entry =>
                    entry.Path.Equals(moduleConfigPath, StringComparison.OrdinalIgnoreCase));
                if (moduleEntry.Size > MaxConfigurationBytes)
                {
                    throw new InvalidDataException(
                        $"FOMOD ModuleConfig.xml exceeds the safe limit of {MaxConfigurationBytes} bytes.");
                }
                if (infoPath is not null)
                {
                    var infoEntry = archiveEntries.Single(entry =>
                        entry.Path.Equals(infoPath, StringComparison.OrdinalIgnoreCase));
                    if (infoEntry.Size > MaxConfigurationBytes)
                    {
                        infoPath = null;
                    }
                }

                nativeCache = NativeSevenZipCache.Create(archivePath, archiveEntries);
                nativeCache.EnsureExtracted(
                    SelectNativeInspectionEntries(
                        archiveEntries,
                        contentPrefix,
                        moduleConfigPath,
                        infoPath),
                    cancellationToken);
            }

            var moduleBytes = ReadEntryBytes(
                archivePath,
                moduleConfigPath,
                MaxConfigurationBytes,
                nativeCache,
                cancellationToken);
            var module = new FomodModuleConfigParser().Parse(moduleBytes);

            var metadata = new FomodMetadata(null, null, null, null, null, null);
            if (infoPath is not null)
            {
                try
                {
                    var infoBytes = ReadEntryBytes(
                        archivePath,
                        infoPath,
                        MaxConfigurationBytes,
                        nativeCache,
                        cancellationToken);
                    metadata = ParseMetadata(infoBytes);
                }
                catch (InvalidDataException)
                {
                    // Metadata is optional and must not make an otherwise valid installer unusable.
                }
            }

            var package = new FomodPackage(
                Path.GetFullPath(archivePath),
                contentPrefix,
                moduleConfigPath,
                module,
                metadata,
                archiveEntries,
                archiveLease,
                nativeCache);
            archiveLease = null;
            nativeCache = null;
            return new FomodArchiveInspection(true, "Мастер FOMOD готов к выбору компонентов.", package);
        }
        catch (Exception exception) when (exception is InvalidDataException
                                          or XmlException
                                          or IOException
                                          or SharpCompressException
                                          or InvalidOperationException
                                          or NotSupportedException
                                          or ArgumentException)
        {
            return new FomodArchiveInspection(isFomod, $"Не удалось прочитать FOMOD: {exception.Message}");
        }
        finally
        {
            nativeCache?.Dispose();
            archiveLease?.Dispose();
        }
    }

    public static byte[] ReadAsset(
        FomodPackage package,
        string relativePath,
        int maxBytes = 16 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var safePath = FomodPath.NormalizeRelativePath(relativePath, allowEmpty: false);
        var assets = ReadAssets(package, [safePath], maxBytes, maxBytes, cancellationToken);
        return assets.TryGetValue(safePath, out var bytes)
            ? bytes
            : throw new FileNotFoundException("Ресурс FOMOD не найден в архиве.", relativePath);
    }

    private static List<string> SelectNativeInspectionEntries(
        IReadOnlyList<ArchiveFileEntry> archiveEntries,
        string contentPrefix,
        string moduleConfigPath,
        string? infoPath)
    {
        var selected = new List<string> { moduleConfigPath };
        long selectedBytes = archiveEntries.Single(entry =>
            entry.Path.Equals(moduleConfigPath, StringComparison.OrdinalIgnoreCase)).Size;
        if (infoPath is not null)
        {
            selected.Add(infoPath);
            selectedBytes += archiveEntries.Single(entry =>
                entry.Path.Equals(infoPath, StringComparison.OrdinalIgnoreCase)).Size;
        }

        // A solid 7z stream may otherwise be decompressed from the beginning
        // again for every wizard picture. Cache the ordinary FOMOD artwork in
        // the same native extraction pass as ModuleConfig.xml. The cache is
        // temporary, bounded and owned by FomodPackage.
        var fomodPrefix = contentPrefix + "fomod/";
        foreach (var entry in archiveEntries)
        {
            if (selected.Count >= MaxNativeInspectionCacheEntries)
            {
                break;
            }
            if (!entry.Path.StartsWith(fomodPrefix, StringComparison.OrdinalIgnoreCase)
                || selected.Contains(entry.Path, StringComparer.OrdinalIgnoreCase)
                || entry.Size > 16L * 1024 * 1024
                || !IsWizardImagePath(entry.Path))
            {
                continue;
            }
            if (entry.Size > MaxNativeInspectionCacheBytes - selectedBytes)
            {
                continue;
            }

            selected.Add(entry.Path);
            selectedBytes += entry.Size;
        }
        return selected;
    }

    private static bool IsWizardImagePath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyDictionary<string, byte[]> ReadAssets(
        FomodPackage package,
        IEnumerable<string> relativePaths,
        int maxBytesPerAsset = 16 * 1024 * 1024,
        long maxTotalBytes = MaxCachedAssetBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(relativePaths);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytesPerAsset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTotalBytes);

        var safePaths = relativePaths
            .Select(path => FomodPath.NormalizeRelativePath(path, allowEmpty: false))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (safePaths.Length == 0)
        {
            return result;
        }

        lock (package.AssetCacheLock)
        {
            package.ThrowIfDisposed();
            long totalBytes = 0;
            var pendingByArchivePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var safePath in safePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (package.TryGetCachedAsset(safePath, out var cached))
                {
                    EnsureAssetBudget(cached.LongLength, totalBytes, maxBytesPerAsset, maxTotalBytes);
                    result[safePath] = cached;
                    totalBytes += cached.LongLength;
                    continue;
                }

                var archivePath = FindArchiveFile(package.ArchiveFiles, package.ContentPrefix + safePath);
                if (archivePath is not null)
                {
                    pendingByArchivePath[archivePath] = safePath;
                }
            }

            if (pendingByArchivePath.Count == 0)
            {
                return result;
            }

            if (package.NativeCache is not null)
            {
                var entriesByPath = package.ArchiveEntries
                    .ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
                var declaredTotal = totalBytes;
                foreach (var archivePath in pendingByArchivePath.Keys)
                {
                    if (!entriesByPath.TryGetValue(archivePath, out var entry))
                    {
                        throw new InvalidDataException($"Archive entry is missing: {archivePath}");
                    }
                    EnsureAssetBudget(entry.Size, declaredTotal, maxBytesPerAsset, maxTotalBytes);
                    declaredTotal += entry.Size;
                }
                package.NativeCache.EnsureExtracted(pendingByArchivePath.Keys, cancellationToken);
                foreach (var pair in pendingByArchivePath)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var source = new FileStream(
                        package.NativeCache.GetPath(pair.Key),
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        81920,
                        FileOptions.SequentialScan);
                    var bytes = ReadBoundedBytes(
                        source,
                        maxBytesPerAsset,
                        maxTotalBytes - totalBytes,
                        cancellationToken);
                    _ = package.TryCacheAsset(pair.Value, bytes, MaxCachedAssetBytes);
                    result[pair.Value] = bytes;
                    totalBytes += bytes.LongLength;
                }
                return result;
            }

            var entryCount = 0;
            using var archiveStream = OpenArchiveStream(package.ArchivePath);
            using var reader = ReaderFactory.OpenReader(archiveStream);
            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                entryCount++;
                if (entryCount > MaxArchiveEntries)
                {
                    throw new InvalidDataException("Архив содержит слишком много элементов.");
                }
                if (reader.Entry.IsDirectory
                    || !pendingByArchivePath.TryGetValue(
                        NormalizeArchivePath(reader.Entry.Key ?? string.Empty),
                        out var safePath))
                {
                    continue;
                }

                using var source = reader.OpenEntryStream();
                var bytes = ReadBoundedBytes(
                    source,
                    maxBytesPerAsset,
                    maxTotalBytes - totalBytes,
                    cancellationToken);
                // A full session cache must not make a later wizard page lose
                // its image. Return the bounded asset even when it no longer
                // fits in the reusable cache; the UI applies its own cap too.
                _ = package.TryCacheAsset(safePath, bytes, MaxCachedAssetBytes);
                result[safePath] = bytes;
                totalBytes += bytes.LongLength;
                pendingByArchivePath.Remove(NormalizeArchivePath(reader.Entry.Key ?? string.Empty));
                if (pendingByArchivePath.Count == 0)
                {
                    break;
                }
            }
        }

        return result;
    }

    private static List<string> ReadFileKeys(
        string archivePath,
        CancellationToken cancellationToken)
    {
        if (ArchiveFileAccess.UsesNativeSevenZip(archivePath))
        {
            return ArchiveFileAccess.ReadFileEntries(archivePath, cancellationToken)
                .Select(entry => entry.Path)
                .ToList();
        }

        var keys = new List<string>();
        var entryCount = 0;
        using var archiveStream = OpenArchiveStream(archivePath);
        using var reader = ReaderFactory.OpenReader(archiveStream);
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            entryCount++;
            if (entryCount > MaxArchiveEntries)
            {
                throw new InvalidDataException("Архив содержит слишком много элементов.");
            }
            if (reader.Entry.IsDirectory)
            {
                continue;
            }

            var key = NormalizeArchivePath(reader.Entry.Key ?? string.Empty);
            if (key.Length > 0)
            {
                keys.Add(key);
            }
        }
        return keys;
    }

    private static byte[] ReadEntryBytes(
        string archivePath,
        string entryPath,
        int maxBytes,
        NativeSevenZipCache? nativeCache,
        CancellationToken cancellationToken)
    {
        if (nativeCache is not null)
        {
            nativeCache.EnsureExtracted([entryPath], cancellationToken);
            using var cached = new FileStream(
                nativeCache.GetPath(entryPath),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.SequentialScan);
            return ReadBoundedBytes(cached, maxBytes, maxBytes, cancellationToken);
        }

        var entryCount = 0;
        using var archiveStream = OpenArchiveStream(archivePath);
        using var reader = ReaderFactory.OpenReader(archiveStream);
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            entryCount++;
            if (entryCount > MaxArchiveEntries)
            {
                throw new InvalidDataException("Архив содержит слишком много элементов.");
            }
            if (reader.Entry.IsDirectory
                || !NormalizeArchivePath(reader.Entry.Key ?? string.Empty)
                    .Equals(entryPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var source = reader.OpenEntryStream();
            return ReadBoundedBytes(source, maxBytes, maxBytes, cancellationToken);
        }

        throw new InvalidDataException($"Файл FOMOD отсутствует в архиве: {entryPath}");
    }

    private static byte[] ReadBoundedBytes(
        Stream source,
        int maxBytes,
        long remainingTotalBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                return output.ToArray();
            }
            EnsureAssetBudget(output.Length + read, 0, maxBytes, remainingTotalBytes);
            output.Write(buffer, 0, read);
        }
    }

    private static void EnsureAssetBudget(
        long assetBytes,
        long currentTotalBytes,
        int maxBytesPerAsset,
        long maxTotalBytes)
    {
        if (assetBytes > maxBytesPerAsset)
        {
            throw new InvalidDataException($"Файл FOMOD превышает безопасный предел {maxBytesPerAsset} байт.");
        }
        if (assetBytes > maxTotalBytes - currentTotalBytes)
        {
            throw new InvalidDataException($"Ресурсы FOMOD превышают безопасный предел {maxTotalBytes} байт.");
        }
    }

    private static FomodMetadata ParseMetadata(byte[] bytes)
    {
        var document = FomodXml.Load(bytes);
        var root = document.Root ?? throw new InvalidDataException("info.xml не содержит корневой элемент.");
        return new FomodMetadata(
            ChildValue(root, "Name"),
            ChildValue(root, "Author"),
            ChildValue(root, "Version"),
            ChildValue(root, "Website"),
            ChildValue(root, "Description"),
            ChildValue(root, "Id"));
    }

    private static string? ChildValue(XElement root, string name)
    {
        var value = root.Elements().FirstOrDefault(element => IsNamed(element, name))?.Value.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    internal static bool IsModuleConfigPath(string path) =>
        path.Equals("fomod/moduleconfig.xml", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("/fomod/moduleconfig.xml", StringComparison.OrdinalIgnoreCase);

    internal static string NormalizeArchivePath(string path) => path.Replace('\\', '/').TrimStart('/');

    internal static string? FindArchiveFile(IEnumerable<string> files, string path) =>
        files.FirstOrDefault(file => file.Equals(path, StringComparison.OrdinalIgnoreCase));

    internal static bool IsNamed(XElement element, string localName) =>
        element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase);

    private static FileStream OpenArchiveStream(string archivePath) => new(
        archivePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        1024 * 1024,
        FileOptions.SequentialScan);
}

internal sealed class FomodModuleConfigParser
{
    private const int MaxDependencyDepth = 64;
    private int _fileSequence;

    public FomodModule Parse(byte[] bytes)
    {
        var document = FomodXml.Load(bytes);
        var root = document.Root ?? throw new InvalidDataException("ModuleConfig.xml не содержит корневой элемент.");
        if (!FomodArchiveReader.IsNamed(root, "config"))
        {
            throw new InvalidDataException("Корневой элемент ModuleConfig.xml должен называться config.");
        }

        var moduleName = Element(root, "moduleName")?.Value.Trim();
        if (string.IsNullOrWhiteSpace(moduleName))
        {
            throw new InvalidDataException("В FOMOD не задано имя модуля.");
        }

        var image = Element(root, "moduleImage");
        var imagePath = OptionalAttribute(image, "path");
        var showImage = ParseBoolean(OptionalAttribute(image, "showImage"), defaultValue: true);
        var dependenciesElement = Element(root, "moduleDependencies");
        var dependencies = dependenciesElement is null ? null : ParseCompositeDependency(dependenciesElement);
        var required = ParseFileList(Element(root, "requiredInstallFiles"));

        var installSteps = Element(root, "installSteps");
        var stepOrder = ParseOrder(OptionalAttribute(installSteps, "order"));
        var steps = installSteps is null
            ? Array.Empty<FomodStep>()
            : installSteps.Elements()
                .Where(element => FomodArchiveReader.IsNamed(element, "installStep"))
                .Select((element, index) => ParseStep(element, index))
                .ToArray();
        steps = Sort(steps, stepOrder, step => step.Name, step => step.DeclarationIndex).ToArray();

        var conditionalInstalls = ParseConditionalInstalls(Element(root, "conditionalFileInstalls"));
        return new FomodModule(
            moduleName,
            imagePath,
            showImage,
            dependencies,
            required,
            stepOrder,
            steps,
            conditionalInstalls);
    }

    private FomodStep ParseStep(XElement element, int stepIndex)
    {
        var id = $"step-{stepIndex}";
        var name = RequiredAttribute(element, "name");
        var visible = Element(element, "visible");
        var groupsElement = Element(element, "optionalFileGroups");
        var groupOrder = ParseOrder(OptionalAttribute(groupsElement, "order"));
        var groups = groupsElement is null
            ? Array.Empty<FomodGroup>()
            : groupsElement.Elements()
                .Where(child => FomodArchiveReader.IsNamed(child, "group"))
                .Select((child, groupIndex) => ParseGroup(child, id, groupIndex))
                .ToArray();
        groups = Sort(groups, groupOrder, group => group.Name, group => group.DeclarationIndex).ToArray();
        return new FomodStep(
            id,
            name,
            visible is null ? null : ParseCompositeDependency(visible),
            groupOrder,
            groups,
            stepIndex);
    }

    private FomodGroup ParseGroup(XElement element, string stepId, int groupIndex)
    {
        var id = $"{stepId}/group-{groupIndex}";
        var name = RequiredAttribute(element, "name");
        var type = ParseEnum<FomodGroupType>(RequiredAttribute(element, "type"), "тип группы");
        var pluginsElement = Element(element, "plugins");
        var order = ParseOrder(OptionalAttribute(pluginsElement, "order"));
        var plugins = pluginsElement is null
            ? Array.Empty<FomodPlugin>()
            : pluginsElement.Elements()
                .Where(child => FomodArchiveReader.IsNamed(child, "plugin"))
                .Select((child, pluginIndex) => ParsePlugin(child, id, pluginIndex))
                .ToArray();
        plugins = Sort(plugins, order, plugin => plugin.Name, plugin => plugin.DeclarationIndex).ToArray();
        return new FomodGroup(id, name, type, order, plugins, groupIndex);
    }

    private FomodPlugin ParsePlugin(XElement element, string groupId, int pluginIndex)
    {
        var id = $"{groupId}/plugin-{pluginIndex}";
        var description = Element(element, "description")?.Value.Trim() ?? string.Empty;
        var imagePath = OptionalAttribute(Element(element, "image"), "path");
        var files = ParseFileList(Element(element, "files"));
        var flags = Element(element, "conditionFlags")?.Elements()
            .Where(child => FomodArchiveReader.IsNamed(child, "flag"))
            .Select(child => new FomodConditionFlag(RequiredAttribute(child, "name"), child.Value.Trim()))
            .ToArray() ?? Array.Empty<FomodConditionFlag>();
        var typeDescriptor = ParseTypeDescriptor(Element(element, "typeDescriptor"));
        return new FomodPlugin(
            id,
            RequiredAttribute(element, "name"),
            description,
            imagePath,
            files,
            flags,
            typeDescriptor,
            pluginIndex);
    }

    private FomodPluginTypeDescriptor ParseTypeDescriptor(XElement? element)
    {
        if (element is null)
        {
            return new FomodPluginTypeDescriptor(FomodPluginType.Optional, Array.Empty<FomodDependencyPattern>());
        }

        var directType = Element(element, "type");
        if (directType is not null)
        {
            return new FomodPluginTypeDescriptor(
                ParsePluginType(RequiredAttribute(directType, "name")),
                Array.Empty<FomodDependencyPattern>());
        }

        var dependencyType = Element(element, "dependencyType");
        if (dependencyType is null)
        {
            return new FomodPluginTypeDescriptor(FomodPluginType.Optional, Array.Empty<FomodDependencyPattern>());
        }

        var defaultTypeElement = Element(dependencyType, "defaultType")
                                 ?? throw new InvalidDataException("dependencyType не содержит defaultType.");
        var patternsElement = Element(dependencyType, "patterns");
        var patterns = patternsElement?.Elements()
            .Where(child => FomodArchiveReader.IsNamed(child, "pattern"))
            .Select(ParseDependencyPattern)
            .ToArray() ?? Array.Empty<FomodDependencyPattern>();
        return new FomodPluginTypeDescriptor(
            ParsePluginType(RequiredAttribute(defaultTypeElement, "name")),
            patterns);
    }

    private FomodDependencyPattern ParseDependencyPattern(XElement element)
    {
        var dependencies = Element(element, "dependencies")
                           ?? throw new InvalidDataException("Шаблон типа FOMOD не содержит dependencies.");
        var type = Element(element, "type")
                   ?? throw new InvalidDataException("Шаблон типа FOMOD не содержит type.");
        return new FomodDependencyPattern(
            ParseCompositeDependency(dependencies),
            ParsePluginType(RequiredAttribute(type, "name")));
    }

    private FomodConditionalInstall[] ParseConditionalInstalls(XElement? element)
    {
        var patterns = Element(element, "patterns");
        if (patterns is null)
        {
            return Array.Empty<FomodConditionalInstall>();
        }

        return patterns.Elements()
            .Where(child => FomodArchiveReader.IsNamed(child, "pattern"))
            // Some installers produced by common FOMOD tools leave an empty
            // placeholder pattern behind. MO2 ignores it, so accepting the
            // same harmless shape keeps otherwise valid packages compatible.
            .Where(pattern => pattern.HasElements
                              || pattern.HasAttributes
                              || !string.IsNullOrWhiteSpace(pattern.Value))
            .Select(pattern =>
            {
                var dependencies = Element(pattern, "dependencies")
                                   ?? throw new InvalidDataException("Условная установка не содержит dependencies.");
                return new FomodConditionalInstall(
                    ParseCompositeDependency(dependencies),
                    ParseFileList(Element(pattern, "files")));
            })
            .ToArray();
    }

    private IReadOnlyList<FomodFileMapping> ParseFileList(XElement? element)
    {
        if (element is null)
        {
            return Array.Empty<FomodFileMapping>();
        }

        var files = new List<FomodFileMapping>();
        foreach (var child in element.Elements())
        {
            var isFile = FomodArchiveReader.IsNamed(child, "file");
            var isFolder = FomodArchiveReader.IsNamed(child, "folder");
            if (!isFile && !isFolder)
            {
                continue;
            }

            var source = RequiredAttribute(child, "source").Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }
            var destinationAttribute = OptionalAttribute(child, "destination");
            var destination = (destinationAttribute ?? source).Replace('\\', '/');
            var priorityText = OptionalAttribute(child, "priority");
            if (!int.TryParse(priorityText ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out var priority))
            {
                throw new InvalidDataException($"Некорректный приоритет FOMOD: {priorityText}");
            }

            files.Add(new FomodFileMapping(
                source,
                destination,
                isFolder,
                priority,
                ParseBoolean(OptionalAttribute(child, "alwaysInstall"), false),
                ParseBoolean(OptionalAttribute(child, "installIfUsable"), false),
                ++_fileSequence));
        }
        return files;
    }

    private static FomodCompositeDependency ParseCompositeDependency(XElement element, int depth = 0)
    {
        if (depth > MaxDependencyDepth)
        {
            throw new InvalidDataException($"Слишком большая вложенность зависимостей FOMOD (максимум {MaxDependencyDepth}).");
        }

        var operation = ParseEnum<FomodDependencyOperator>(
            OptionalAttribute(element, "operator") ?? "And",
            "оператор зависимостей");
        var dependencies = new List<FomodDependency>();
        foreach (var child in element.Elements())
        {
            if (FomodArchiveReader.IsNamed(child, "dependencies"))
            {
                dependencies.Add(ParseCompositeDependency(child, depth + 1));
            }
            else if (FomodArchiveReader.IsNamed(child, "flagDependency"))
            {
                dependencies.Add(new FomodFlagDependency(
                    RequiredAttribute(child, "flag"),
                    RequiredAttribute(child, "value")));
            }
            else if (FomodArchiveReader.IsNamed(child, "fileDependency"))
            {
                dependencies.Add(new FomodFileDependency(
                    RequiredAttribute(child, "file"),
                    ParseEnum<FomodFileState>(RequiredAttribute(child, "state"), "состояние файла")));
            }
            else if (FomodArchiveReader.IsNamed(child, "gameDependency"))
            {
                dependencies.Add(new FomodVersionDependency(
                    FomodVersionDependencyKind.Game,
                    RequiredAttribute(child, "version")));
            }
            else if (FomodArchiveReader.IsNamed(child, "fommDependency"))
            {
                dependencies.Add(new FomodVersionDependency(
                    FomodVersionDependencyKind.Fomod,
                    RequiredAttribute(child, "version")));
            }
            else if (FomodArchiveReader.IsNamed(child, "foseDependency"))
            {
                dependencies.Add(new FomodVersionDependency(
                    FomodVersionDependencyKind.ScriptExtender,
                    RequiredAttribute(child, "version")));
            }
            else
            {
                throw new InvalidDataException($"Неподдерживаемая зависимость FOMOD: {child.Name.LocalName}");
            }
        }
        return new FomodCompositeDependency(operation, dependencies);
    }

    private static FomodOrder ParseOrder(string? value) =>
        ParseEnum<FomodOrder>(value ?? "Ascending", "порядок элементов");

    private static FomodPluginType ParsePluginType(string value) =>
        ParseEnum<FomodPluginType>(value, "тип компонента");

    private static T ParseEnum<T>(string value, string description)
        where T : struct, Enum
    {
        if (Enum.TryParse<T>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }
        throw new InvalidDataException($"Неподдерживаемый {description}: {value}");
    }

    private static bool ParseBoolean(string? value, bool defaultValue)
    {
        if (value is null)
        {
            return defaultValue;
        }
        if (value.Equals("1", StringComparison.Ordinal))
        {
            return true;
        }
        if (value.Equals("0", StringComparison.Ordinal))
        {
            return false;
        }
        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }
        throw new InvalidDataException($"Некорректное логическое значение FOMOD: {value}");
    }

    private static IEnumerable<T> Sort<T>(
        IEnumerable<T> values,
        FomodOrder order,
        Func<T, string> name,
        Func<T, int> index)
    {
        return order switch
        {
            FomodOrder.Ascending => values
                .OrderBy(name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(index),
            FomodOrder.Descending => values
                .OrderByDescending(name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(index),
            _ => values.OrderBy(index)
        };
    }

    private static XElement? Element(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(child => FomodArchiveReader.IsNamed(child, name));

    private static string RequiredAttribute(XElement element, string name)
    {
        var value = OptionalAttribute(element, name);
        if (value is null)
        {
            throw new InvalidDataException($"Элемент {element.Name.LocalName} не содержит атрибут {name}.");
        }
        return value;
    }

    private static string? OptionalAttribute(XElement? element, string name) =>
        element?.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim();
}

internal static class FomodXml
{
    private const long MaxCharacters = 8 * 1024 * 1024;
    private const int MaxElements = 100_000;

    public static XDocument Load(byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = CreateReader(stream);
            return LoadDocument(reader);
        }
        catch (XmlException firstError)
        {
            foreach (var encoding in CandidateEncodings())
            {
                try
                {
                    var text = encoding.GetString(bytes).TrimStart('\uFEFF', '\0');
                    var declarationEnd = text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
                        ? text.IndexOf("?>", StringComparison.Ordinal)
                        : -1;
                    if (declarationEnd >= 0)
                    {
                        text = text[(declarationEnd + 2)..];
                    }
                    using var stringReader = new StringReader(text);
                    using var reader = CreateReader(stringReader);
                    return LoadDocument(reader);
                }
                catch (Exception exception) when (exception is XmlException or DecoderFallbackException)
                {
                    // Try the next encoding, matching MO2's tolerant FOMOD reader.
                }
            }
            throw new InvalidDataException($"Некорректный XML: {firstError.Message}", firstError);
        }
    }

    private static XmlReader CreateReader(Stream stream) => XmlReader.Create(stream, Settings());

    private static XmlReader CreateReader(TextReader reader) => XmlReader.Create(reader, Settings());

    private static XDocument LoadDocument(XmlReader reader)
    {
        var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        if (document.Descendants().Take(MaxElements + 1).Count() > MaxElements)
        {
            throw new InvalidDataException($"XML FOMOD содержит слишком много элементов (максимум {MaxElements}).");
        }
        return document;
    }

    private static XmlReaderSettings Settings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxCharacters,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true
    };

    private static IEnumerable<Encoding> CandidateEncodings()
    {
        yield return new UTF8Encoding(false, true);
        yield return new UnicodeEncoding(false, true, true);
        yield return new UnicodeEncoding(true, true, true);
        yield return Encoding.GetEncoding(
            1251,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        yield return Encoding.Latin1;
    }
}

internal static class FomodPath
{
    public static string NormalizeRelativePath(string path, bool allowEmpty)
    {
        if (path is null)
        {
            throw new InvalidDataException("Путь FOMOD не задан.");
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.Length == 0)
        {
            if (allowEmpty)
            {
                return string.Empty;
            }
            throw new InvalidDataException("Путь FOMOD пуст.");
        }
        if (normalized.StartsWith('/')
            || normalized.StartsWith("//", StringComparison.Ordinal)
            || (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':'))
        {
            throw new InvalidDataException($"Абсолютный путь запрещён в FOMOD: {path}");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException($"Переход за пределы каталога запрещён в FOMOD: {path}");
        }
        if (segments.Any(segment => segment.Any(character =>
                character < ' '
                || character is '<' or '>' or ':' or '"' or '|' or '?' or '*')
            || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new InvalidDataException($"Некорректный путь FOMOD: {path}");
        }
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment)
                                    || segment.EndsWith('.')
                                    || segment.EndsWith(' ')
                                    || IsWindowsDeviceName(segment)))
        {
            throw new InvalidDataException($"Путь FOMOD нельзя безопасно создать в Windows: {path}");
        }
        return string.Join('/', segments);
    }

    private static bool IsWindowsDeviceName(string segment)
    {
        var stem = segment.Split('.', 2)[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length == 4
               && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                   || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
               && stem[3] is >= '1' and <= '9';
    }
}
