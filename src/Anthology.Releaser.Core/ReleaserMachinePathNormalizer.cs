using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Anthology.Releaser.Core;

/// <summary>
/// Repairs local paths that were written after UTF-8 text had accidentally been
/// decoded as Windows-1251. A repaired value is accepted only when the target or
/// its immediate parent can be confirmed on this machine.
/// </summary>
public static class ReleaserMachinePathNormalizer
{
    private const int MaximumRepairPasses = 3;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Encoding Windows1251;

    private static readonly (string Broken, string Correct)[] KnownPunctuation =
    [
        ("вЂ”", "—"),
        ("вЂ“", "–"),
        ("вЂ¦", "…"),
        ("вЂ™", "’"),
        ("вЂњ", "“"),
        ("вЂќ", "”"),
        ("в„–", "№"),
        ("в†’", "→"),
    ];

    static ReleaserMachinePathNormalizer()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Windows1251 = Encoding.GetEncoding(
            1251,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }

    public static bool Normalize(ReleaserMachineSettings machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var changed = false;

        machine.DeveloperName = NormalizeTextValue(machine.DeveloperName, ref changed);
        machine.GameSourceRoot = NormalizeValue(machine.GameSourceRoot, ref changed);
        machine.Mo2SourceRoot = NormalizeValue(machine.Mo2SourceRoot, ref changed);
        machine.OutputRoot = NormalizeValue(machine.OutputRoot, ref changed);
        machine.SharedWorkspaceRoot = NormalizeValue(machine.SharedWorkspaceRoot, ref changed);
        machine.PrivateKeyPath = NormalizeValue(machine.PrivateKeyPath, ref changed);
        machine.PublicKeyPath = NormalizeValue(machine.PublicKeyPath, ref changed);

        machine.PublicationRoots ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        NormalizeDictionary(machine.PublicationRoots, ref changed);

        machine.ContentArchivePaths ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        NormalizeDictionary(machine.ContentArchivePaths, ref changed);

        machine.ContentImagePaths ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        NormalizePathLists(machine.ContentImagePaths, ref changed);

        machine.ContentVideoPaths ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        NormalizePathLists(machine.ContentVideoPaths, ref changed);

        machine.QuickReleaseFiles ??= [];
        foreach (var file in machine.QuickReleaseFiles)
        {
            if (file is not null)
            {
                file.SourcePath = NormalizeValue(file.SourcePath, ref changed);
            }
        }

        machine.QuickReleaseFolders ??= [];
        foreach (var folder in machine.QuickReleaseFolders)
        {
            if (folder is not null)
            {
                folder.SourcePath = NormalizeValue(folder.SourcePath, ref changed);
            }
        }

        return changed;
    }

    public static string RecoverText(string? value)
    {
        var original = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(original) || !LooksLikeMojibake(original))
        {
            return original;
        }

        var current = original;
        for (var pass = 0; pass < MaximumRepairPasses; pass++)
        {
            var repaired = RepairOnce(current);
            if (string.Equals(repaired, current, StringComparison.Ordinal)
                || CorruptionScore(repaired) >= CorruptionScore(current))
            {
                break;
            }

            current = repaired;
        }

        return current;
    }

    public static string RecoverConfirmedPath(string? path)
    {
        var original = path?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(original)
            || !Path.IsPathFullyQualified(original)
            || !LooksLikeMojibake(original))
        {
            return original;
        }

        var candidates = new List<(string Path, int Corruption, int Pass)>();
        var current = original;
        for (var pass = 1; pass <= MaximumRepairPasses; pass++)
        {
            var repaired = RepairOnce(current);
            if (string.Equals(repaired, current, StringComparison.Ordinal))
            {
                break;
            }

            var currentCorruption = CorruptionScore(current);
            var repairedCorruption = CorruptionScore(repaired);
            if (repairedCorruption >= currentCorruption)
            {
                break;
            }

            candidates.Add((repaired, repairedCorruption, pass));
            current = repaired;
        }

        foreach (var candidate in candidates
                     .OrderBy(item => item.Corruption)
                     .ThenByDescending(item => item.Pass))
        {
            if (IsConfirmed(candidate.Path))
            {
                return candidate.Path;
            }
        }

        return original;
    }

    private static void NormalizeDictionary(Dictionary<string, string> paths, ref bool changed)
    {
        foreach (var key in paths.Keys.ToArray())
        {
            paths[key] = NormalizeValue(paths[key], ref changed);
        }
    }

    private static void NormalizePathLists(Dictionary<string, List<string>> pathLists, ref bool changed)
    {
        foreach (var key in pathLists.Keys.ToArray())
        {
            var original = pathLists[key] ?? [];
            var normalized = new List<string>(original.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in original)
            {
                var candidate = NormalizeValue(path, ref changed);
                if (string.IsNullOrWhiteSpace(candidate) || !seen.Add(candidate))
                {
                    changed = true;
                    continue;
                }
                normalized.Add(candidate);
            }
            pathLists[key] = normalized;
        }
    }

    private static string NormalizeValue(string? value, ref bool changed)
    {
        var normalized = RecoverConfirmedPath(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            changed = true;
        }
        return normalized;
    }

    private static string NormalizeTextValue(string? value, ref bool changed)
    {
        var normalized = RecoverText(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            changed = true;
        }
        return normalized;
    }

    private static string RepairOnce(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var position = 0; position < value.Length;)
        {
            if (value[position] <= 0x7f)
            {
                builder.Append(value[position++]);
                continue;
            }

            var start = position++;
            while (position < value.Length && value[position] > 0x7f)
            {
                position++;
            }

            var run = value[start..position];
            builder.Append(TryRepairRun(run, out var decoded) ? decoded : run);
        }

        var repaired = builder.ToString();
        foreach (var pair in KnownPunctuation)
        {
            repaired = repaired.Replace(pair.Broken, pair.Correct, StringComparison.Ordinal);
        }
        return repaired;
    }

    private static bool TryRepairRun(string run, out string repaired)
    {
        repaired = run;
        if (!LooksLikeMojibake(run))
        {
            return false;
        }

        try
        {
            var bytes = Windows1251.GetBytes(run);
            var candidate = StrictUtf8.GetString(bytes);
            if (string.Equals(candidate, run, StringComparison.Ordinal)
                || CorruptionScore(candidate) >= CorruptionScore(run))
            {
                return false;
            }

            repaired = candidate;
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool LooksLikeMojibake(string value) => CorruptionScore(value) > 0;

    private static int CorruptionScore(string value)
    {
        var score = 0;
        foreach (var pair in KnownPunctuation)
        {
            score += CountOccurrences(value, pair.Broken) * 4;
        }

        for (var index = 0; index + 1 < value.Length; index++)
        {
            if (value[index] is not ('Р' or 'С'))
            {
                continue;
            }

            try
            {
                var encoded = Windows1251.GetBytes(value[index + 1].ToString());
                if (encoded.Length == 1 && encoded[0] is >= 0x80 and <= 0xbf)
                {
                    score++;
                }
            }
            catch (EncoderFallbackException)
            {
                // Not a Windows-1251 continuation-byte artifact.
            }
        }

        return score;
    }

    private static int CountOccurrences(string value, string fragment)
    {
        var count = 0;
        var position = 0;
        while ((position = value.IndexOf(fragment, position, StringComparison.Ordinal)) >= 0)
        {
            count++;
            position += fragment.Length;
        }
        return count;
    }

    private static bool IsConfirmed(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return false;
            }
            if (File.Exists(path) || Directory.Exists(path))
            {
                return true;
            }

            var pathWithoutTrailingSeparator = Path.TrimEndingDirectorySeparator(path);
            var parent = Path.GetDirectoryName(pathWithoutTrailingSeparator);
            return !string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or IOException
                                           or NotSupportedException
                                           or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
