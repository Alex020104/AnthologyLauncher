using Microsoft.Win32;
using System.IO;

namespace Anthology.Launcher;

public static class LauncherDialogService
{
    public static string? SelectFolder(string description, string? initialPath)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = description,
            ShowNewFolderButton = false,
            SelectedPath = Directory.Exists(initialPath) ? initialPath : string.Empty,
            UseDescriptionForTitle = true,
        };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
            ? dialog.SelectedPath
            : null;
    }

    public static string? SelectFile(string title, string filter, string? initialPath)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false,
        };
        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            dialog.InitialDirectory = File.Exists(initialPath)
                ? Path.GetDirectoryName(initialPath)
                : Directory.Exists(initialPath) ? initialPath : null;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
