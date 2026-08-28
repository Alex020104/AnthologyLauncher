using System.Text.Json;

namespace Anthology.Update.Core;

public sealed record InstallResult(
    string OperationId,
    int InstalledFiles,
    string JournalPath,
    int DeletedFiles = 0);

public sealed record RollbackResult(string OperationId, int RestoredFiles, string JournalPath);

public sealed record FileTransactionEntry(
    string RelativePath,
    bool TargetExisted,
    string Action = "replace");

public sealed record FileTransactionJournal(
    string OperationId,
    string Status,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<FileTransactionEntry> Files);

public static class TransactionalFileInstaller
{
    private static readonly JsonSerializerOptions JournalJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<InstallResult> ApplyAsync(
        string stagedRoot,
        string targetRoot,
        string stateRoot,
        IEnumerable<string> relativePaths,
        IEnumerable<string>? obsoletePaths = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        ArgumentNullException.ThrowIfNull(relativePaths);

        var normalizedPaths = relativePaths
            .Select(PathSafety.NormalizeRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var obsolete = (obsoletePaths ?? [])
            .Select(PathSafety.NormalizeRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Except(normalizedPaths, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedPaths.Length == 0)
        {
            throw new ArgumentException("No files were selected for installation.", nameof(relativePaths));
        }

        foreach (var relativePath in normalizedPaths)
        {
            var source = PathSafety.ResolveUnderRoot(stagedRoot, relativePath);
            if (!File.Exists(source))
            {
                throw new FileNotFoundException("Staged file is missing.", source);
            }
        }

        var operationId = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}";
        var operationRoot = Path.Combine(Path.GetFullPath(stateRoot), "transactions", operationId);
        var backupRoot = Path.Combine(operationRoot, "backup");
        var journalPath = Path.Combine(operationRoot, "journal.json");
        Directory.CreateDirectory(backupRoot);

        var applied = new List<FileTransactionEntry>();
        try
        {
            foreach (var relativePath in normalizedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = PathSafety.ResolveUnderRoot(stagedRoot, relativePath);
                var target = PathSafety.ResolveUnderRoot(targetRoot, relativePath);
                var backup = PathSafety.ResolveUnderRoot(backupRoot, relativePath);
                var targetExisted = File.Exists(target);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (targetExisted)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup, true);
                }

                var temporaryTarget = target + $".anthology-new-{operationId}";
                File.Copy(source, temporaryTarget, true);
                File.Move(temporaryTarget, target, true);
                applied.Add(new FileTransactionEntry(relativePath, targetExisted, "replace"));
                await WriteJournalAsync(journalPath, operationId, "applying", applied, cancellationToken);
            }

            foreach (var relativePath in obsolete)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = PathSafety.ResolveUnderRoot(targetRoot, relativePath);
                if (!File.Exists(target))
                {
                    continue;
                }

                var backup = PathSafety.ResolveUnderRoot(backupRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(target, backup, true);
                File.Delete(target);
                applied.Add(new FileTransactionEntry(relativePath, true, "delete"));
                await WriteJournalAsync(journalPath, operationId, "applying", applied, cancellationToken);
            }

            await WriteJournalAsync(journalPath, operationId, "completed", applied, cancellationToken);
            return new InstallResult(
                operationId,
                applied.Count(item => string.Equals(item.Action, "replace", StringComparison.Ordinal)),
                journalPath,
                applied.Count(item => string.Equals(item.Action, "delete", StringComparison.Ordinal)));
        }
        catch
        {
            // A cancelled update must still restore the last known-good installation.
            await RestoreFilesAsync(targetRoot, backupRoot, applied, CancellationToken.None);
            await WriteJournalAsync(journalPath, operationId, "rolled-back", applied, CancellationToken.None);
            throw;
        }
    }

    public static async Task<RollbackResult> RollbackAsync(
        string targetRoot,
        string stateRoot,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        if (!string.Equals(Path.GetFileName(operationId), operationId, StringComparison.Ordinal)
            || operationId is "." or "..")
        {
            throw new ArgumentException("Invalid transaction operation id.", nameof(operationId));
        }

        var operationRoot = Path.Combine(Path.GetFullPath(stateRoot), "transactions", operationId);
        var journalPath = Path.Combine(operationRoot, "journal.json");
        if (!File.Exists(journalPath))
        {
            throw new FileNotFoundException("Update rollback journal was not found.", journalPath);
        }

        FileTransactionJournal journal;
        await using (var stream = new FileStream(
                         journalPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         32 * 1024,
                         FileOptions.Asynchronous))
        {
            journal = await JsonSerializer.DeserializeAsync<FileTransactionJournal>(
                stream,
                JournalJsonOptions,
                cancellationToken) ?? throw new InvalidDataException("Update rollback journal is invalid.");
        }
        if (!string.Equals(journal.OperationId, operationId, StringComparison.Ordinal)
            || !string.Equals(journal.Status, "completed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("This update transaction cannot be rolled back.");
        }

        var backupRoot = Path.Combine(operationRoot, "backup");
        await RestoreFilesAsync(targetRoot, backupRoot, journal.Files, cancellationToken);
        await WriteJournalAsync(
            journalPath,
            operationId,
            "rolled-back-by-user",
            journal.Files,
            cancellationToken);
        return new RollbackResult(operationId, journal.Files.Count, journalPath);
    }

    private static Task WriteJournalAsync(
        string path,
        string operationId,
        string status,
        IReadOnlyList<FileTransactionEntry> files,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(
            new FileTransactionJournal(operationId, status, DateTimeOffset.UtcNow, files.ToArray()),
            JournalJsonOptions);
        return File.WriteAllTextAsync(path, payload, cancellationToken);
    }

    private static Task RestoreFilesAsync(
        string targetRoot,
        string backupRoot,
        IReadOnlyList<FileTransactionEntry> applied,
        CancellationToken cancellationToken)
    {
        foreach (var entry in applied.Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = PathSafety.ResolveUnderRoot(targetRoot, entry.RelativePath);
            if (entry.TargetExisted)
            {
                var backup = PathSafety.ResolveUnderRoot(backupRoot, entry.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(backup, target, true);
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
            }
        }

        return Task.CompletedTask;
    }
}
