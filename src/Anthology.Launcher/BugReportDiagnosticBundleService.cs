using System.IO;
using Anthology.Mo2.Core;

namespace Anthology.Launcher;

public sealed record BugReportDiagnosticBundle(string Path, IReadOnlyList<string> IncludedFiles);

public sealed class BugReportDiagnosticBundleService(LauncherSettingsStore settingsStore)
{
    public BugReportDiagnosticBundle Create(string? gameRoot, string? mo2Root)
    {
        var destinationRoot = Path.Combine(settingsStore.DataRoot, "BugReports", "Automatic");
        var bundle = AnomalyDiagnosticBundleBuilder.Create(destinationRoot, gameRoot, mo2Root);
        return new BugReportDiagnosticBundle(bundle.Path, bundle.IncludedFiles);
    }
}
