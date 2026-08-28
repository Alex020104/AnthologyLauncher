using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Anthology.Contracts;
using Anthology.Update.Core;

namespace Anthology.Releaser.Core;

public static class UnifiedReleaseBuilder
{
    private static readonly string[] CommonExcludedRoots =
    [
        ".git",
        ".anthology-releaser",
        "$RECYCLE.BIN",
        "System Volume Information",
    ];

    private static readonly string[] GameExcludedRoots =
    [
        "appdata",
        "logs",
        "screenshots",
        "crashdumps",
        "AnomalyLauncher.cfg",
        "commandline.txt",
    ];

    private static readonly string[] Mo2ExcludedRoots =
    [
        "overwrite",
        "logs",
        "crash_dumps",
        "webcache",
        "ModOrganizer.ini",
    ];

    public static async Task<UnifiedReleaseResult> BuildAsync(
        UnifiedReleaseRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var workspace = request.Workspace;
        var machine = request.Machine;
        ReleaseVersionRules.Validate(workspace.Version);
        ValidateMachine(machine);
        if (string.IsNullOrWhiteSpace(machine.GameSourceRoot) || string.IsNullOrWhiteSpace(machine.Mo2SourceRoot))
        {
            throw new InvalidOperationException("Для выпуска всей сборки выберите оба подготовленных корня: игру и MO2.");
        }

        var outputRoot = Path.Combine(Path.GetFullPath(machine.OutputRoot), workspace.Version);
        ValidatePathSeparation(machine.GameSourceRoot, outputRoot, machine.PrivateKeyPath, machine.PublicKeyPath);
        ValidatePathSeparation(machine.Mo2SourceRoot, outputRoot, machine.PrivateKeyPath, machine.PublicKeyPath);
        Directory.CreateDirectory(outputRoot);
        var packages = new List<PackageManifest>();
        var artifactPaths = new List<string>();

        if (!string.IsNullOrWhiteSpace(machine.GameSourceRoot))
        {
            progress?.Report("Сканирование полного корня игры…");
            var result = await BuildPackageAsync(
                "anthology-game",
                "Anthology — корень игры",
                PackageKind.Game,
                "game",
                machine.GameSourceRoot,
                outputRoot,
                workspace,
                static mirror => mirror.GameUrl,
                GameExcludedRoots,
                progress,
                cancellationToken);
            packages.Add(result.Package);
            artifactPaths.Add(result.ArtifactPath);
        }

        if (!string.IsNullOrWhiteSpace(machine.Mo2SourceRoot))
        {
            progress?.Report("Сканирование полного корня MO2…");
            var result = await BuildPackageAsync(
                "anthology-mo2",
                "Anthology — Mod Organizer 2",
                PackageKind.Modpack,
                "modpack",
                machine.Mo2SourceRoot,
                outputRoot,
                workspace,
                static mirror => mirror.Mo2Url,
                Mo2ExcludedRoots,
                progress,
                cancellationToken);
            packages.Add(result.Package);
            artifactPaths.Add(result.ArtifactPath);
        }

        if (packages.Count == 0)
        {
            throw new InvalidOperationException("Укажите подготовленный корень игры и/или MO2.");
        }

        var catalog = CreateContentCatalog(workspace);
        var payload = new UpdateManifest(
            2,
            string.IsNullOrWhiteSpace(workspace.Channel) ? "next" : workspace.Channel.Trim().ToLowerInvariant(),
            workspace.Version.Trim(),
            DateTimeOffset.UtcNow,
            null,
            packages,
            catalog);

        progress?.Report("Подпись единого манифеста…");
        using var privateKey = ECDsa.Create();
        privateKey.ImportFromPem(await File.ReadAllTextAsync(Path.GetFullPath(machine.PrivateKeyPath), cancellationToken));
        var signed = ManifestSecurity.Sign(payload, privateKey, machine.KeyId.Trim());
        ManifestValidator.ValidateAndThrow(signed);

        var manifestPath = Path.Combine(outputRoot, "manifest.json");
        await WriteJsonAtomicallyAsync(manifestPath, signed, cancellationToken);
        await WriteJsonAtomicallyAsync(Path.Combine(outputRoot, "content.json"), catalog, cancellationToken);
        await WriteJsonAtomicallyAsync(Path.Combine(outputRoot, "release-workspace.json"), workspace, cancellationToken);

        progress?.Report("Единый релиз готов.");
        return new UnifiedReleaseResult(
            workspace.Version,
            manifestPath,
            artifactPaths,
            packages.Sum(package => package.Files.Count),
            packages.Sum(package => package.Size),
            catalog.Items.Count);
    }

    public static void GenerateKeys(string privateKeyPath, string publicKeyPath, bool overwrite = false)
    {
        var privatePath = Path.GetFullPath(privateKeyPath);
        var publicPath = Path.GetFullPath(publicKeyPath);
        if (!overwrite && (File.Exists(privatePath) || File.Exists(publicPath)))
        {
            throw new IOException("Ключи уже существуют. Удалите их вручную или выберите другую папку.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(privatePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(publicPath)!);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(privatePath, key.ExportECPrivateKeyPem());
        File.WriteAllText(publicPath, key.ExportSubjectPublicKeyInfoPem());
    }

    private static async Task<(PackageManifest Package, string ArtifactPath)> BuildPackageAsync(
        string id,
        string displayName,
        PackageKind kind,
        string installRoot,
        string inputRoot,
        string outputRoot,
        ReleaserWorkspace workspace,
        Func<ReleaseMirrorSet, string> selectUrl,
        IReadOnlyList<string> specificExcludedRoots,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var input = Path.GetFullPath(inputRoot);
        if (!Directory.Exists(input))
        {
            throw new DirectoryNotFoundException($"Не найдена исходная папка: {input}");
        }

        var excluded = CommonExcludedRoots
            .Concat(specificExcludedRoots)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(input, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
            })
            .Select(path => Path.GetRelativePath(input, path).Replace('\\', '/'))
            .Where(path => !excluded.Contains(path.Split('/', 2)[0]))
            .Select(PathSafety.NormalizeRelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
        {
            throw new InvalidDataException($"Исходная папка {displayName} не содержит файлов для публикации.");
        }

        var artifactName = $"{id}-{workspace.Version}.zip";
        var artifactPath = Path.Combine(outputRoot, artifactName);
        progress?.Report($"Упаковка {displayName}: {files.Length:N0} файлов…");
        await CreateDeterministicZipAsync(input, artifactPath, files, cancellationToken);
        var size = new FileInfo(artifactPath).Length;
        var hash = await ArtifactHash.ComputeSha256Async(artifactPath, cancellationToken);
        var mirrors = workspace.Mirrors
            .Select(mirror => new { Mirror = mirror, Url = selectUrl(mirror).Trim() })
            .Where(item => !string.IsNullOrWhiteSpace(item.Url))
            .Select(item => new MirrorManifest(
                NormalizeProvider(item.Mirror.Provider),
                ExpandUrl(item.Url, workspace.Version, id, artifactName),
                item.Mirror.Priority))
            .OrderBy(item => item.Priority)
            .ToArray();
        if (mirrors.Length == 0)
        {
            mirrors = [new MirrorManifest("local-file", new Uri(artifactPath).AbsoluteUri, 1000)];
        }

        return (new PackageManifest(
            id,
            displayName,
            workspace.Version,
            kind,
            installRoot,
            "zip",
            size,
            hash,
            mirrors,
            files,
            PackageUpdateMode.ManagedExact,
            true,
            CommonExcludedRoots.Concat(specificExcludedRoots).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()), artifactPath);
    }

    public static ContentCatalog CreateContentCatalog(ReleaserWorkspace workspace)
    {
        var items = workspace.Content.Where(item => item.IsPublished).Select(item =>
        {
            var images = SplitLines(item.Images).ToArray();
            var videos = SplitLines(item.Videos)
                .Select(line => SplitPair(line, "Видео"))
                .Select(pair => new ContentVideo(pair.Left, pair.Right))
                .ToArray();
            ContentDownload? download = null;
            var hasDownload = !string.IsNullOrWhiteSpace(item.DownloadFileName)
                              || item.DownloadSize > 0
                              || !string.IsNullOrWhiteSpace(item.DownloadSha256)
                              || !string.IsNullOrWhiteSpace(item.DownloadMirrors);
            if (hasDownload)
            {
                var mirrors = SplitLines(item.DownloadMirrors)
                    .Select(line => SplitPair(line, "http"))
                    .Select((pair, index) => new MirrorManifest(
                        NormalizeProvider(pair.Left),
                        ExpandUrl(pair.Right, workspace.Version, item.Id, item.DownloadFileName),
                        (index + 1) * 10))
                    .ToArray();
                download = new ContentDownload(
                    item.DownloadFileName.Trim(),
                    item.DownloadSize,
                    item.DownloadSha256.Trim().ToLowerInvariant(),
                    mirrors,
                    string.IsNullOrWhiteSpace(item.InstallFolderName) ? item.Id.Trim() : item.InstallFolderName.Trim(),
                    ReplaceExisting: true);
            }

            var section = string.IsNullOrWhiteSpace(item.Section) ? "general" : item.Section.Trim().ToLowerInvariant();
            if (item.Kind == ContentKind.Mod && section is not ("dev" or "modmakers" or "solutions"))
            {
                section = "dev";
            }

            var translations = new Dictionary<string, ContentTranslation>(StringComparer.OrdinalIgnoreCase);
            AddTranslation(translations, "en", item.TitleEn, item.SummaryEn, item.BodyEn);
            AddTranslation(translations, "de", item.TitleDe, item.SummaryDe, item.BodyDe);
            foreach (var (language, translation) in item.Translations ?? [])
            {
                AddTranslation(translations, AnthologyLanguages.Normalize(language), translation.Title, translation.Summary, translation.Body);
            }
            var blocks = (item.Blocks ?? [])
                .Select(block =>
                {
                    var blockTranslations = new Dictionary<string, ContentBlockTranslation>(StringComparer.OrdinalIgnoreCase);
                    AddBlockTranslation(blockTranslations, "en", block.TitleEn, block.BodyEn);
                    AddBlockTranslation(blockTranslations, "de", block.TitleDe, block.BodyDe);
                    foreach (var (language, translation) in block.Translations ?? [])
                    {
                        AddBlockTranslation(blockTranslations, AnthologyLanguages.Normalize(language), translation.Title, translation.Body);
                    }
                    return new ContentBlock(
                        block.Id.Trim().ToLowerInvariant(),
                        block.Kind,
                        block.Title.Trim(),
                        block.Body.Trim(),
                        string.IsNullOrWhiteSpace(block.Url) ? null : block.Url.Trim(),
                        blockTranslations);
                })
                .ToArray();

            return new ContentDocument(
                item.Id.Trim().ToLowerInvariant(),
                item.Kind,
                section,
                item.Title.Trim(),
                item.Summary.Trim(),
                item.Body.Trim(),
                images,
                videos,
                download,
                translations,
                blocks);
        }).ToArray();

        return new ContentCatalog(3, workspace.Version, DateTimeOffset.UtcNow, items);
    }

    private static void AddTranslation(
        Dictionary<string, ContentTranslation> translations,
        string language,
        string title,
        string summary,
        string body)
    {
        if (string.IsNullOrWhiteSpace(title)
            && string.IsNullOrWhiteSpace(summary)
            && string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        translations[language] = new ContentTranslation(title.Trim(), summary.Trim(), body.Trim());
    }

    private static void AddBlockTranslation(
        Dictionary<string, ContentBlockTranslation> translations,
        string language,
        string title,
        string body)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        translations[language] = new ContentBlockTranslation(title.Trim(), body.Trim());
    }

    private static async Task CreateDeterministicZipAsync(
        string input,
        string artifact,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        var temporary = artifact + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var relativePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = PathSafety.ResolveUnderRoot(input, relativePath);
                var entry = archive.CreateEntry(relativePath, CompressionLevel.SmallestSize);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                await using var entryStream = entry.Open();
                await using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await sourceStream.CopyToAsync(entryStream, cancellationToken);
            }
        }
        catch
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            throw;
        }

        File.Move(temporary, artifact, true);
    }

    public static async Task WriteJsonAtomicallyAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, value, ManifestJson.Options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporary, path, true);
    }

    private static IEnumerable<string> SplitLines(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line));

    private static (string Left, string Right) SplitPair(string value, string defaultLeft)
    {
        var separator = value.IndexOf('|');
        if (separator < 0)
        {
            separator = value.IndexOf('=');
        }

        return separator > 0 && separator < value.Length - 1
            ? (value[..separator].Trim(), value[(separator + 1)..].Trim())
            : (defaultLeft, value.Trim());
    }

    public static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "yandex" or "яндекс" or "yandex-disk" => "yandex-disk",
        "google" or "google-drive" => "google-drive",
        "github" => "github",
        "local" or "local-file" => "local-file",
        _ => "http",
    };

    public static string ExpandUrl(string url, string version, string id, string fileName) => url
        .Replace("{version}", version.Trim(), StringComparison.OrdinalIgnoreCase)
        .Replace("{id}", id.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase)
        .Replace("{file}", fileName.Trim(), StringComparison.OrdinalIgnoreCase);

    public static void ValidateMachine(ReleaserMachineSettings machine)
    {
        if (string.IsNullOrWhiteSpace(machine.OutputRoot))
        {
            throw new ArgumentException("Выберите папку для готовых релизов.");
        }

        if (string.IsNullOrWhiteSpace(machine.PrivateKeyPath) || !File.Exists(Path.GetFullPath(machine.PrivateKeyPath)))
        {
            throw new FileNotFoundException("Не найден закрытый ключ подписи. Создайте или выберите ключ.", machine.PrivateKeyPath);
        }

        if (string.IsNullOrWhiteSpace(machine.KeyId))
        {
            throw new ArgumentException("Укажите идентификатор ключа.");
        }
    }

    private static void ValidatePathSeparation(
        string sourceRoot,
        string outputRoot,
        string privateKeyPath,
        string publicKeyPath)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot))
        {
            return;
        }

        var source = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sourcePrefix = source + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(outputRoot).StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Папка готовых релизов не может находиться внутри публикуемой сборки.");
        }

        foreach (var keyPath in new[] { privateKeyPath, publicKeyPath })
        {
            if (!string.IsNullOrWhiteSpace(keyPath)
                && Path.GetFullPath(keyPath).StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Ключи подписи нельзя хранить внутри публикуемого корня игры или MO2.");
            }
        }
    }
}
