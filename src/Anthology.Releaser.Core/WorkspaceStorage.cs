using System.Security.Cryptography;
using System.Text.Json;
using System.Globalization;
using Anthology.Contracts;

namespace Anthology.Releaser.Core;

public static class WorkspaceStorage
{
    public static async Task<T> LoadAsync<T>(
        string path,
        Func<T> create,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return create();
        }

        try
        {
            return await DeserializeAsync(path, create, cancellationToken);
        }
        catch (JsonException)
        {
            // Cloud sync clients and interrupted writes can leave a correctly named file
            // filled with zero bytes. Keep the damaged bytes for diagnostics and recover
            // the last complete version instead of crashing the whole releaser at startup.
            PreserveCorruptedFile(path);
            var backupPath = path + ".bak";
            if (!File.Exists(backupPath))
            {
                return create();
            }

            try
            {
                var recovered = await DeserializeAsync(backupPath, create, cancellationToken);
                File.Copy(backupPath, path, true);
                return recovered;
            }
            catch (JsonException)
            {
                PreserveCorruptedFile(backupPath);
                return create();
            }
        }
    }

    public static async Task SaveAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + $".tmp-{Guid.NewGuid():N}";
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, value, ManifestJson.Options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        if (File.Exists(fullPath) && await ContainsValidJsonAsync(fullPath, cancellationToken))
        {
            File.Copy(fullPath, fullPath + ".bak", true);
        }
        File.Move(temporary, fullPath, true);
    }

    private static async Task<T> DeserializeAsync<T>(
        string path,
        Func<T> create,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<T>(stream, ManifestJson.Options, cancellationToken) ?? create();
    }

    private static async Task<bool> ContainsValidJsonAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                64 * 1024,
                FileOptions.Asynchronous);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.ValueKind is not JsonValueKind.Undefined;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void PreserveCorruptedFile(string path)
    {
        try
        {
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
            File.Copy(path, $"{path}.corrupt-{stamp}", false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Recovery must still continue if the directory is read-only or a sync client
            // briefly owns the source file.
        }
    }

    public static string ComputeHash(ReleaserWorkspace workspace)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(workspace, ManifestJson.Options);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}

public static class WorkspaceSyncService
{
    public const string SharedFileName = "anthology-release-workspace.json";

    public static async Task<WorkspaceSyncResult> SyncAsync(
        ReleaserWorkspace local,
        string sharedRoot,
        string? lastSyncedHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedRoot);
        var root = Path.GetFullPath(sharedRoot);
        Directory.CreateDirectory(root);
        var sharedPath = Path.Combine(root, SharedFileName);
        var localHash = WorkspaceStorage.ComputeHash(local);

        if (!File.Exists(sharedPath))
        {
            await WorkspaceStorage.SaveAsync(sharedPath, local, cancellationToken);
            return new WorkspaceSyncResult(WorkspaceSyncDirection.Published, local, localHash, "Рабочий проект впервые опубликован в общей папке.");
        }

        var shared = await WorkspaceStorage.LoadAsync(sharedPath, () => new ReleaserWorkspace(), cancellationToken);
        var sharedHash = WorkspaceStorage.ComputeHash(shared);
        if (string.Equals(localHash, sharedHash, StringComparison.OrdinalIgnoreCase))
        {
            return new WorkspaceSyncResult(WorkspaceSyncDirection.None, local, localHash, "Изменений для синхронизации нет.");
        }

        var hasBaseline = !string.IsNullOrWhiteSpace(lastSyncedHash);
        var localChanged = !hasBaseline || !string.Equals(localHash, lastSyncedHash, StringComparison.OrdinalIgnoreCase);
        var sharedChanged = !hasBaseline || !string.Equals(sharedHash, lastSyncedHash, StringComparison.OrdinalIgnoreCase);
        if (hasBaseline && localChanged && sharedChanged)
        {
            // Revision is the workspace's optimistic concurrency token. If the
            // revisions differ, the newer edit is authoritative and the older
            // variant is retained only as a recovery copy. Treating every stale
            // LastSyncedHash as an irresolvable conflict used to lock automatic
            // synchronization forever and could block an otherwise valid release.
            if (local.Revision != shared.Revision)
            {
                var conflicts = Path.Combine(root, "Conflicts");
                Directory.CreateDirectory(conflicts);
                var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                if (local.Revision > shared.Revision)
                {
                    await WorkspaceStorage.SaveAsync(Path.Combine(conflicts, $"superseded-shared-r{shared.Revision}-{stamp}.json"), shared, cancellationToken);
                    await WorkspaceStorage.SaveAsync(sharedPath, local, cancellationToken);
                    return new WorkspaceSyncResult(
                        WorkspaceSyncDirection.Published,
                        local,
                        localHash,
                        $"Опубликована более новая редакция {local.Revision}; старая общая редакция {shared.Revision} сохранена для восстановления.");
                }

                await WorkspaceStorage.SaveAsync(Path.Combine(conflicts, $"superseded-local-r{local.Revision}-{stamp}.json"), local, cancellationToken);
                return new WorkspaceSyncResult(
                    WorkspaceSyncDirection.Received,
                    shared,
                    sharedHash,
                    $"Получена более новая общая редакция {shared.Revision}; локальная редакция {local.Revision} сохранена для восстановления.");
            }

            var concurrentConflicts = Path.Combine(root, "Conflicts");
            Directory.CreateDirectory(concurrentConflicts);
            var concurrentStamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            await WorkspaceStorage.SaveAsync(Path.Combine(concurrentConflicts, $"local-r{local.Revision}-{concurrentStamp}.json"), local, cancellationToken);
            await WorkspaceStorage.SaveAsync(Path.Combine(concurrentConflicts, $"shared-r{shared.Revision}-{concurrentStamp}.json"), shared, cancellationToken);
            throw new InvalidOperationException($"Два разных варианта имеют одинаковую редакцию {local.Revision}. Копии сохранены в {concurrentConflicts}; выберите нужную версию.");
        }

        if (shared.Revision > local.Revision || sharedChanged && !localChanged)
        {
            return new WorkspaceSyncResult(WorkspaceSyncDirection.Received, shared, sharedHash, $"Получена редакция {shared.Revision} от {shared.UpdatedBy}.");
        }

        if (local.Revision > shared.Revision || localChanged && !sharedChanged)
        {
            await WorkspaceStorage.SaveAsync(sharedPath, local, cancellationToken);
            return new WorkspaceSyncResult(WorkspaceSyncDirection.Published, local, localHash, $"Редакция {local.Revision} отправлена второму разработчику.");
        }

        var equalRevisionConflicts = Path.Combine(root, "Conflicts");
        Directory.CreateDirectory(equalRevisionConflicts);
        var equalStamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        await WorkspaceStorage.SaveAsync(Path.Combine(equalRevisionConflicts, $"local-{equalStamp}.json"), local, cancellationToken);
        await WorkspaceStorage.SaveAsync(Path.Combine(equalRevisionConflicts, $"shared-{equalStamp}.json"), shared, cancellationToken);
        throw new InvalidOperationException("Обнаружены разные варианты одной редакции. Оба сохранены в папке Conflicts.");
    }
}
