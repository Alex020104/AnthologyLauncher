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

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<T>(stream, ManifestJson.Options, cancellationToken) ?? create();
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

        File.Move(temporary, fullPath, true);
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
            var conflicts = Path.Combine(root, "Conflicts");
            Directory.CreateDirectory(conflicts);
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            await WorkspaceStorage.SaveAsync(Path.Combine(conflicts, $"local-{stamp}.json"), local, cancellationToken);
            await WorkspaceStorage.SaveAsync(Path.Combine(conflicts, $"shared-{stamp}.json"), shared, cancellationToken);
            throw new InvalidOperationException($"Оба разработчика изменили проект. Копии сохранены в {conflicts}; выберите нужную версию вручную.");
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
