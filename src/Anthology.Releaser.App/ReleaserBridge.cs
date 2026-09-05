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

    public IReadOnlyList<string> SelectFiles(string title, string filter, string? initialDirectory = null)
    {
        lock (_dialogGate)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = title,
                Filter = filter,
                CheckFileExists = true,
                Multiselect = true,
            };
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                dialog.InitialDirectory = initialDirectory;
            }

            return dialog.ShowDialog() == true ? dialog.FileNames : [];
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

    public void OpenUrl(string? url)
    {
        lock (_dialogGate)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("https" or "http"))
            {
                throw new ArgumentException("Укажите корректную ссылку HTTP или HTTPS.", nameof(url));
            }

            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }

    public async Task<int> ConfigureRcloneAsync(
        string? rclonePath,
        string? configPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rclonePath) || !Path.IsPathFullyQualified(rclonePath))
        {
            throw new ArgumentException("Укажите полный путь к rclone.exe.", nameof(rclonePath));
        }
        var executable = Path.GetFullPath(rclonePath.Trim());
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("Не найден rclone.exe.", executable);
        }
        if (string.IsNullOrWhiteSpace(configPath) || !Path.IsPathFullyQualified(configPath))
        {
            throw new ArgumentException("Укажите полный путь к локальному rclone.conf.", nameof(configPath));
        }
        var configuration = Path.GetFullPath(configPath.Trim());
        var configurationDirectory = Path.GetDirectoryName(configuration)
                                     ?? throw new ArgumentException("Некорректный путь к rclone.conf.", nameof(configPath));
        Directory.CreateDirectory(configurationDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal,
        };
        startInfo.ArgumentList.Add("config");
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(configuration);

        Process process;
        lock (_dialogGate)
        {
            process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("Не удалось открыть интерактивную настройку rclone.");
        }
        using (process)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                    // rclone успел закрыться между проверкой и отменой.
                }
                throw;
            }
            return process.ExitCode;
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
