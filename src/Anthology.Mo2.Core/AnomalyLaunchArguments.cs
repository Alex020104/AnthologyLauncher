using System.Buffers;

namespace Anthology.Mo2.Core;

/// <summary>
/// Builds the raw X-Ray command line used to start an Anomaly save directly.
/// </summary>
public static class AnomalyLaunchArguments
{
    private static readonly SearchValues<char> InvalidSaveNameCharacters =
        SearchValues.Create("/\\:*?\"<>|^()[]%");

    public static string AppendStartSave(string? baseArguments, string saveName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveName);

        var normalizedSaveName = saveName.Trim();
        if (normalizedSaveName.AsSpan().IndexOfAny(InvalidSaveNameCharacters) >= 0)
        {
            throw new ArgumentException("Имя сохранения содержит недопустимые для X-Ray символы.", nameof(saveName));
        }

        // X-Ray executes -load before CApplication and ALife exist, so a cold
        // launch silently stays in the main menu. Its own load_last_save command
        // uses this start-server form when no ALife session exists. Keep it last:
        // the engine forwards the raw tail after "-start " to its console parser.
        // Quotes would become part of the save name; spaces are supported as-is.
        var startCommand = $"-start server({normalizedSaveName}/single/alife/load)";
        var prefix = baseArguments?.Trim();
        return string.IsNullOrEmpty(prefix)
            ? startCommand
            : $"{prefix} {startCommand}";
    }
}
