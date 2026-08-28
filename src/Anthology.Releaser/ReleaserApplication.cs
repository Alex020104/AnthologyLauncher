using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Anthology.Contracts;
using Anthology.Update.Core;

namespace Anthology.Releaser;

public static class ReleaserApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args is ["keys", "generate", .. var keyArgs])
            {
                GenerateKeys(Arguments.Parse(keyArgs));
                return 0;
            }

            if (args is ["package", "create", .. var packageArgs])
            {
                await CreatePackageAsync(Arguments.Parse(packageArgs));
                return 0;
            }

            PrintHelp();
            return args.Length == 0 ? 0 : 2;
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidDataException
                                           or IOException
                                           or UnauthorizedAccessException
                                           or CryptographicException)
        {
            Console.Error.WriteLine($"Ошибка: {exception.Message}");
            return 1;
        }
    }

    private static void GenerateKeys(Arguments arguments)
    {
        var privatePath = Path.GetFullPath(arguments.Required("private"));
        var publicPath = Path.GetFullPath(arguments.Required("public"));
        EnsureOutputAvailable(privatePath, arguments.HasFlag("force"));
        EnsureOutputAvailable(publicPath, arguments.HasFlag("force"));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        WriteTextAtomically(privatePath, key.ExportECPrivateKeyPem());
        WriteTextAtomically(publicPath, key.ExportSubjectPublicKeyInfoPem());
        Console.WriteLine($"Закрытый ключ: {privatePath}");
        Console.WriteLine($"Открытый ключ: {publicPath}");
        Console.WriteLine("Закрытый ключ нельзя коммитить или передавать пользователям.");
    }

    private static async Task CreatePackageAsync(Arguments arguments)
    {
        var input = Path.GetFullPath(arguments.Required("input"));
        if (!Directory.Exists(input))
        {
            throw new DirectoryNotFoundException($"Не найдена папка пакета: {input}");
        }

        var packageId = arguments.Required("id").Trim().ToLowerInvariant();
        var version = arguments.Required("version").Trim();
        var artifact = Path.GetFullPath(arguments.Required("artifact"));
        var manifestPath = Path.GetFullPath(arguments.Required("manifest"));
        var privateKeyPath = Path.GetFullPath(arguments.Required("private-key"));
        var keyId = arguments.Required("key-id");
        var force = arguments.HasFlag("force");
        if (IsInsideDirectory(input, artifact) || IsInsideDirectory(input, manifestPath))
        {
            throw new ArgumentException("Artifact and manifest outputs must be outside the package input directory.");
        }

        EnsureOutputAvailable(artifact, force);
        EnsureOutputAvailable(manifestPath, force);

        var files = Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(input, path).Replace('\\', '/'))
            .Select(PathSafety.NormalizeRelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
        {
            throw new InvalidDataException("Папка пакета пуста.");
        }

        CreateDeterministicZip(input, artifact, files);
        var hash = await ArtifactHash.ComputeSha256Async(artifact);
        var size = new FileInfo(artifact).Length;
        var mirrors = arguments.All("mirror")
            .Select(ParseMirror)
            .OrderBy(item => item.Priority)
            .ToArray();
        if (mirrors.Length == 0)
        {
            throw new ArgumentException("Нужно указать хотя бы одно зеркало --mirror provider=https://...");
        }

        var kind = Enum.Parse<PackageKind>(arguments.Optional("kind") ?? "mod", true);
        var package = new PackageManifest(
            packageId,
            arguments.Optional("name") ?? packageId,
            version,
            kind,
            arguments.Optional("install-root") ?? "modpack",
            "zip",
            size,
            hash,
            mirrors,
            files);
        var payload = new UpdateManifest(
            1,
            arguments.Optional("channel") ?? "next",
            version,
            DateTimeOffset.UtcNow,
            arguments.Optional("minimum-launcher-version"),
            [package]);

        using var privateKey = ECDsa.Create();
        privateKey.ImportFromPem(File.ReadAllText(privateKeyPath));
        var signed = ManifestSecurity.Sign(payload, privateKey, keyId);
        ManifestValidator.ValidateAndThrow(signed);
        WriteTextAtomically(manifestPath, JsonSerializer.Serialize(signed, ManifestJson.Options));

        Console.WriteLine($"Пакет: {artifact}");
        Console.WriteLine($"Манифест: {manifestPath}");
        Console.WriteLine($"Файлов: {files.Length}; байт: {size}; SHA-256: {hash}");
        Console.WriteLine($"Зеркал: {mirrors.Length}");
    }

    private static void CreateDeterministicZip(string input, string artifact, IReadOnlyList<string> files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        var temporary = artifact + $".tmp-{Guid.NewGuid():N}";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var relativePath in files)
                {
                    var source = PathSafety.ResolveUnderRoot(input, relativePath);
                    var entry = archive.CreateEntry(relativePath, CompressionLevel.SmallestSize);
                    entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var entryStream = entry.Open();
                    using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                    sourceStream.CopyTo(entryStream);
                }
            }

            File.Move(temporary, artifact, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static MirrorManifest ParseMirror(string value, int index)
    {
        var separator = value.IndexOf('=');
        if (separator <= 0 || separator == value.Length - 1)
        {
            throw new ArgumentException($"Неверное зеркало '{value}'. Формат: provider=https://...");
        }

        return new MirrorManifest(value[..separator].Trim(), value[(separator + 1)..].Trim(), index * 10 + 10);
    }

    private static void EnsureOutputAvailable(string path, bool force)
    {
        if (File.Exists(path) && !force)
        {
            throw new IOException($"Файл уже существует: {path}. Используйте --force для замены.");
        }
    }

    private static bool IsInsideDirectory(string directory, string path)
    {
        var root = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteTextAtomically(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, true);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Anthology Releaser Next");
        Console.WriteLine();
        Console.WriteLine("  keys generate --private key.pem --public key.pub.pem [--force]");
        Console.WriteLine("  package create --input DIR --artifact FILE.zip --manifest manifest.json");
        Console.WriteLine("    --id ID --name NAME --version VERSION --kind Mod --install-root modpack");
        Console.WriteLine("    --private-key key.pem --key-id production-01");
        Console.WriteLine("    --mirror github=https://... --mirror yandex-disk=https://disk.yandex.ru/d/...");
        Console.WriteLine("    [--mirror google-drive=https://...] [--mirror local-file=file:///E:/...]");
        Console.WriteLine("    [--channel next] [--force]");
    }

    private sealed class Arguments
    {
        private readonly Dictionary<string, List<string?>> _values;

        private Arguments(Dictionary<string, List<string?>> values) => _values = values;

        public static Arguments Parse(string[] args)
        {
            var values = new Dictionary<string, List<string?>>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < args.Length; index++)
            {
                var token = args[index];
                if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length == 2)
                {
                    throw new ArgumentException($"Неизвестный аргумент: {token}");
                }

                var key = token[2..];
                string? value = null;
                if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[++index];
                }

                if (!values.TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    values[key] = bucket;
                }
                bucket.Add(value);
            }

            return new Arguments(values);
        }

        public string Required(string key) =>
            Optional(key) ?? throw new ArgumentException($"Не указан обязательный параметр --{key}.");

        public string? Optional(string key) =>
            _values.TryGetValue(key, out var values) ? values.LastOrDefault() : null;

        public string[] All(string key) =>
            _values.TryGetValue(key, out var values)
                ? values.Where(value => value is not null).Cast<string>().ToArray()
                : [];

        public bool HasFlag(string key) => _values.ContainsKey(key);
    }
}
