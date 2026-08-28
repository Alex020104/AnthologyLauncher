using System.Text.Json;

namespace Anthology.Update.Core;

public sealed record InstallResult(string OperationId, int InstalledFiles, string JournalPath);

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

        var applied = new List<JournalEntry>();
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
                applied.Add(new JournalEntry(relativePath, targetExisted));
                await WriteJournalAsync(journalPath, operationId, "applying", applied, cancellationToken);
            }

            await WriteJournalAsync(journalPath, operationId, "completed", applied, cancellationToken);
            return new InstallResult(operationId, applied.Count, journalPath);
        }
        catch
        {
            // A cancelled update must still restore the last known-good installation.
            await RollbackAsync(targetRoot, backupRoot, applied, CancellationToken.None);
            await WriteJournalAsync(journalPath, operationId, "rolled-back", applied, CancellationToken.None);
            throw;
        }
    }

    private static Task WriteJournalAsync(
        string path,
        string operationId,
        string status,
        IReadOnlyList<JournalEntry> files,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(
            new Journal(operationId, status, DateTimeOffset.UtcNow, files.ToArray()),
            JournalJsonOptions);
        return File.WriteAllTextAsync(path, payload, cancellationToken);
    }

    private static Task RollbackAsync(
        string targetRoot,
        string backupRoot,
        IReadOnlyList<JournalEntry> applied,
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

    private sealed record Journal(
        string OperationId,
        string Status,
        DateTimeOffset UpdatedAt,
        IReadOnlyList<JournalEntry> Files);

    private sealed record JournalEntry(string RelativePath, bool TargetExisted);
}
