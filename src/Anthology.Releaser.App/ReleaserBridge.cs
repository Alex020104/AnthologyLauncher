using System.Diagnostics;
using System.IO;

namespace Anthology.Releaser.App;

public sealed class ReleaserBridge
{
    private readonly object _dialogGate = new();

    public string? SelectFolder(string title, string? current, bool allowCreate = true)
    {
        lock (_dialogGate)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = title,
                UseDescriptionForTitle = true,
                ShowNewFolderButton = allowCreate,
                SelectedPath = Directory.Exists(current) ? current : string.Empty,
            };
            return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
        }
    }

    public string? SelectFile(string title, string filter, string? current)
    {
        lock (_dialogGate)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = title,
                Filter = filter,
                CheckFileExists = true,
            };
            if (!string.IsNullOrWhiteSpace(current))
            {
                dialog.InitialDirectory = File.Exists(current) ? Path.GetDirectoryName(current) : current;
            }

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }
    }

    public void OpenFolder(string? path)
    {
        lock (_dialogGate)
        {
            if (string.IsNullOrWhiteSpace(path)) { return; }
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    public bool Confirm(string message, string title)
    {
        lock (_dialogGate)
        {
            return System.Windows.Forms.MessageBox.Show(
                       message,
                       title,
                       System.Windows.Forms.MessageBoxButtons.YesNo,
                       System.Windows.Forms.MessageBoxIcon.Warning,
                       System.Windows.Forms.MessageBoxDefaultButton.Button2) == System.Windows.Forms.DialogResult.Yes;
        }
    }
}
