using System.IO;

namespace Anthology.Launcher;

public sealed record EmbeddedMo2Request(string Root, string? Profile);

public sealed class EmbeddedMo2HostService
{
    internal Func<EmbeddedMo2Request, Task<LauncherActionResult>>? OpenHandler { get; set; }

    internal Action? HideHandler { get; set; }

    public Task<LauncherActionResult> OpenAsync(string? root, string? profile)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return Task.FromResult(new LauncherActionResult(false, "Сначала подключите папку Mod Organizer 2 в разделе «Установка»"));
        }

        return OpenHandler is null
            ? Task.FromResult(new LauncherActionResult(false, "Нативная панель MO2 ещё не готова"))
            : OpenHandler(new EmbeddedMo2Request(Path.GetFullPath(root), profile));
    }

    public void Hide() => HideHandler?.Invoke();
}
