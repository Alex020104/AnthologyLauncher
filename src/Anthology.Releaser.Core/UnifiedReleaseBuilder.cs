using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Anthology.Contracts;
using Anthology.Update.Core;

namespace Anthology.Releaser.Core;

public static class UnifiedReleaseBuilder
{
    // GitHub blocks files larger than 100 MiB in ordinary repositories. Keep a
    // safety margin so a publication checkout never receives an unpushable file.
    public const long GitHubRepositoryArtifactLimitBytes = 95L * 1024 * 1024;

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
        "webcache",
        "AnthologyLauncher",
        "AnomalyLauncher.cfg",
        "commandline.txt",
    ];

    private static readonly string[] Mo2ExcludedRoots =
    [
        "downloads",
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

        var mo2SourceRoot = ValidateFullMo2SourceRoot(machine.Mo2SourceRoot);

        var outputRoot = Path.Combine(Path.GetFullPath(machine.OutputRoot), workspace.Version);
        ValidatePathSeparation(machine.GameSourceRoot, outputRoot, machine.PrivateKeyPath, machine.PublicKeyPath);
        ValidatePathSeparation(machine.Mo2SourceRoot, outputRoot, machine.PrivateKeyPath, machine.PublicKeyPath);
        Directory.CreateDirectory(outputRoot);
        var packages = (await LoadPreservedPackagesAsync(
                Path.Combine(outputRoot, "manifest.json"),
                cancellationToken))
            .Where(package => package.Kind == PackageKind.Launcher
                              && !string.Equals(
                                  package.Id,
                                  PackageIntegrityCatalogBuilder.PackageId,
                                  StringComparison.OrdinalIgnoreCase))
            .ToList();
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
                mo2SourceRoot,
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

        var media = await ContentMediaPublisher.PrepareAsync(
            workspace,
            machine,
            outputRoot,
            progress,
            cancellationToken);
        progress?.Report("Подпись единого манифеста…");
        using var privateKey = ECDsa.Create();
        privateKey.ImportFromPem(await File.ReadAllTextAsync(Path.GetFullPath(machine.PrivateKeyPath), cancellationToken));
        var integrity = await PackageIntegrityCatalogBuilder.BuildAsync(
            packages,
            outputRoot,
            workspace,
            privateKey,
            machine.KeyId.Trim(),
            progress,
            cancellationToken);
        if (integrity is not null)
        {
            packages.Add(integrity.Package);
            artifactPaths.Add(integrity.ArtifactPath);
        }

        var catalog = CreateContentCatalog(workspace, media);
        var payload = new UpdateManifest(
            4,
            string.IsNullOrWhiteSpace(workspace.Channel) ? "next" : workspace.Channel.Trim().ToLowerInvariant(),
            workspace.Version.Trim(),
            DateTimeOffset.UtcNow,
            null,
            packages,
            catalog);
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
            .Where(item => SupportsArtifact(item.Mirror.Provider, size))
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
            CommonExcludedRoots
                .Concat(specificExcludedRoots)
                // Profiles are shipped and updated, but player-created profiles,
                // saves and other unknown files below this root must never be
                // removed by exact-prune updates.
                .Concat(installRoot.Equals("modpack", StringComparison.OrdinalIgnoreCase)
                    ? ["profiles"]
                    : Array.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()), artifactPath);
    }

    public static bool SupportsArtifact(string provider, long size) =>
        !NormalizeProvider(provider).Equals("github", StringComparison.OrdinalIgnoreCase)
        || size <= GitHubRepositoryArtifactLimitBytes;

    private static async Task<IReadOnlyList<PackageManifest>> LoadPreservedPackagesAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            return [];
        }

        await using var stream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous);
        var signed = await JsonSerializer.DeserializeAsync<SignedUpdateManifest>(
                         stream,
                         ManifestJson.Options,
                         cancellationToken)
                     ?? throw new InvalidDataException("Existing manifest.json is empty or damaged.");
        return signed.Payload.Packages;
    }

    public static ContentCatalog CreateContentCatalog(
        ReleaserWorkspace workspace,
        PreparedContentMedia? media = null)
    {
        media ??= PreparedContentMedia.Empty;
        var items = workspace.Content.Where(item => item.IsPublished).Select(item =>
        {
            var uploadedImages = media.ContentImages.TryGetValue(item.Id, out var resolvedImages)
                ? resolvedImages
                : [];
            var images = uploadedImages
                .Concat(SplitLines(item.Images))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var uploadedVideos = media.ContentVideos.TryGetValue(item.Id, out var resolvedVideos)
                ? resolvedVideos
                : [];
            var videos = uploadedVideos
                .Concat(SplitLines(item.Videos)
                    .Select(line => SplitPair(line, "Видео"))
                    .Select(pair => new ContentVideo(pair.Left, pair.Right)))
                .DistinctBy(video => video.Url, StringComparer.OrdinalIgnoreCase)
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
                    var resolvedUrl = media.BlockImages.TryGetValue(
                        ContentMediaPublisher.BlockKey(item.Id, block.Id),
                        out var uploadedImage)
                            ? uploadedImage
                            : string.IsNullOrWhiteSpace(block.Url) ? null : block.Url.Trim();
                    return new ContentBlock(
                        block.Id.Trim().ToLowerInvariant(),
                        block.Kind,
                        block.Title.Trim(),
                        block.Body.Trim(),
                        resolvedUrl,
                        blockTranslations);
                })
                .ToArray();
            var authorLinks = (item.AuthorLinks ?? [])
                .Where(link => link.IsVisible && !string.IsNullOrWhiteSpace(link.Url))
                .OrderBy(link => link.Order)
                .ThenBy(link => link.Id, StringComparer.OrdinalIgnoreCase)
                .Select(link => new SocialLink(
                    link.Id.Trim().ToLowerInvariant(),
                    link.Title.Trim(),
                    link.Subtitle.Trim(),
                    link.Url.Trim()))
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
                blocks,
                item.PublishedAt,
                authorLinks);
        }).ToArray();

        var socialLinks = (workspace.SocialLinks ?? [])
            .Where(link => link.IsVisible && !string.IsNullOrWhiteSpace(link.Url))
            .OrderBy(link => link.Order)
            .ThenBy(link => link.Id, StringComparer.OrdinalIgnoreCase)
            .Select(link => new SocialLink(
                link.Id.Trim().ToLowerInvariant(),
                link.Title.Trim(),
                link.Subtitle.Trim(),
                link.Url.Trim()))
            .ToArray();

        var projectPeople = (workspace.ProjectPeople ?? [])
            .Where(person => person.IsVisible && !string.IsNullOrWhiteSpace(person.Name))
            .OrderBy(person => person.Order)
            .ThenBy(person => person.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(person =>
            {
                var translations = new Dictionary<string, ProjectPersonTranslation>(StringComparer.OrdinalIgnoreCase);
                foreach (var (language, translation) in person.Translations ?? [])
                {
                    if (string.IsNullOrWhiteSpace(translation.Name)
                        && string.IsNullOrWhiteSpace(translation.Role)
                        && string.IsNullOrWhiteSpace(translation.Description))
                    {
                        continue;
                    }
                    translations[AnthologyLanguages.Normalize(language)] = new ProjectPersonTranslation(
                        translation.Name.Trim(),
                        translation.Role.Trim(),
                        translation.Description.Trim());
                }
                var links = (person.Links ?? [])
                    .Where(link => link.IsVisible && !string.IsNullOrWhiteSpace(link.Url))
                    .OrderBy(link => link.Order)
                    .Select(link => new SocialLink(
                        link.Id.Trim().ToLowerInvariant(),
                        link.Title.Trim(),
                        link.Subtitle.Trim(),
                        link.Url.Trim()))
                    .ToArray();
                var imageUrl = media.ProjectPersonImages.TryGetValue(person.Id, out var uploadedImage)
                    ? uploadedImage
                    : string.IsNullOrWhiteSpace(person.ImageUrl) ? null : person.ImageUrl.Trim();
                return new ProjectPerson(
                    person.Id.Trim().ToLowerInvariant(),
                    person.Name.Trim(),
                    person.Role.Trim(),
                    person.Description.Trim(),
                    imageUrl,
                    links,
                    person.Order,
                    translations);
            })
            .ToArray();

        var liveStreams = (workspace.LiveStreams ?? [])
            .Where(stream => stream.IsVisible
                             && !string.IsNullOrWhiteSpace(stream.Title)
                             && !string.IsNullOrWhiteSpace(stream.Url))
            .OrderBy(stream => stream.Order)
            .ThenBy(stream => stream.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(stream =>
            {
                var translations = new Dictionary<string, LiveStreamTranslation>(StringComparer.OrdinalIgnoreCase);
                foreach (var (language, translation) in stream.Translations ?? [])
                {
                    if (string.IsNullOrWhiteSpace(translation.Title)
                        && string.IsNullOrWhiteSpace(translation.Subtitle))
                    {
                        continue;
                    }
                    translations[AnthologyLanguages.Normalize(language)] = new LiveStreamTranslation(
                        translation.Title.Trim(),
                        translation.Subtitle.Trim());
                }
                return new LiveStream(
                    stream.Id.Trim().ToLowerInvariant(),
                    stream.Title.Trim(),
                    stream.Subtitle.Trim(),
                    stream.Url.Trim(),
                    stream.Order,
                    translations);
            })
            .ToArray();

        ReleaseChangelog? changelog = null;
        if (workspace.Changelog is not null
            && (!string.IsNullOrWhiteSpace(workspace.Changelog.Title)
                || !string.IsNullOrWhiteSpace(workspace.Changelog.Summary)
                || !string.IsNullOrWhiteSpace(workspace.Changelog.Body)
                || !string.IsNullOrWhiteSpace(workspace.Changelog.Warnings)))
        {
            var translations = new Dictionary<string, ReleaseChangelogTranslation>(StringComparer.OrdinalIgnoreCase);
            foreach (var (language, translation) in workspace.Changelog.Translations ?? [])
            {
                if (string.IsNullOrWhiteSpace(translation.Title)
                    && string.IsNullOrWhiteSpace(translation.Summary)
                    && string.IsNullOrWhiteSpace(translation.Body)
                    && string.IsNullOrWhiteSpace(translation.Warnings))
                {
                    continue;
                }
                translations[AnthologyLanguages.Normalize(language)] = new ReleaseChangelogTranslation(
                    translation.Title.Trim(),
                    translation.Summary.Trim(),
                    translation.Body.Trim(),
                    translation.Warnings.Trim());
            }
            changelog = new ReleaseChangelog(
                workspace.Changelog.Title.Trim(),
                workspace.Changelog.Summary.Trim(),
                workspace.Changelog.Body.Trim(),
                workspace.Changelog.Warnings.Trim(),
                translations);
        }

        return new ContentCatalog(4, workspace.Version, DateTimeOffset.UtcNow, items, socialLinks, projectPeople, liveStreams, changelog);
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
        var trimmed = value.Trim();
        var separator = trimmed.IndexOf('|');
        if (separator > 0 && separator < trimmed.Length - 1)
        {
            return (trimmed[..separator].Trim(), trimmed[(separator + 1)..].Trim());
        }

        // A plain URL may legally contain '=' in its query string, for example
        // youtube.com/watch?v=... or drive.google.com/...?usp=sharing. Treat the
        // whole value as the URL before supporting the legacy "name=url" form.
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out _))
        {
            return (defaultLeft, trimmed);
        }

        separator = trimmed.IndexOf('=');
        if (separator < 0)
        {
            return (defaultLeft, trimmed);
        }

        return separator > 0 && separator < trimmed.Length - 1
            ? (trimmed[..separator].Trim(), trimmed[(separator + 1)..].Trim())
            : (defaultLeft, trimmed);
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
        ArgumentNullException.ThrowIfNull(machine);
        _ = ReleaserMachinePathNormalizer.Normalize(machine);
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

        if (string.Equals(machine.KeyId.Trim(), ProductionSigningKeyPolicy.KeyId, StringComparison.Ordinal))
        {
            ProductionSigningKeyPolicy.Validate(machine);
        }
    }

    private static string ValidateFullMo2SourceRoot(string sourceRoot)
    {
        var fullRoot = Path.GetFullPath(sourceRoot);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Не найден полный корень MO2: {fullRoot}");
        }

        if (!File.Exists(Path.Combine(fullRoot, "ModOrganizer.exe")))
        {
            var selectedDirectory = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullRoot));
            var parentRoot = Directory.GetParent(fullRoot)?.FullName;
            if (selectedDirectory.Equals("mods", StringComparison.OrdinalIgnoreCase)
                && parentRoot is not null
                && File.Exists(Path.Combine(parentRoot, "ModOrganizer.exe")))
            {
                throw new InvalidDataException(
                    $"Выбрана папка MO2\\mods. Для полного выпуска выберите её родительский корень, содержащий ModOrganizer.exe: {parentRoot}");
            }

            throw new InvalidDataException(
                $"Полный корень MO2 должен содержать ModOrganizer.exe: {fullRoot}");
        }

        return fullRoot;
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
