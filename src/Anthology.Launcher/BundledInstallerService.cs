using System.IO;
using System.Net.Http;
using Anthology.Update.Core;

namespace Anthology.Launcher;

public sealed record BundledMediaStatus(
    bool Available,
    string MediaRoot,
    string StatusText);

public sealed class BundledInstallerService(
    HttpClient httpClient,
    LauncherSettingsStore settingsStore)
{
    private const string InstallChannel = "install";
    private readonly string _mediaRoot = Path.Combine(AppContext.BaseDirectory, "InstallMedia");

    public BundledMediaStatus DetectMedia()
    {
        var manifest = Path.Combine(_mediaRoot, "manifest.json");
        var publicKey = Path.Combine(_mediaRoot, "install.public.pem");
        var available = File.Exists(manifest) && File.Exists(publicKey);
        return new BundledMediaStatus(
            available,
            _mediaRoot,
            available
                ? "Установочный комплект найден и готов к проверке подписи"
                : "Добавьте manifest.json, install.public.pem и пакеты в папку InstallMedia рядом с лаунчером");
    }

    public async Task<LauncherActionResult> SelectDestinationAsync(CancellationToken cancellationToken = default)
    {
        var selected = LauncherDialogService.SelectFolder(
            "Выберите пустую папку для установки Anthology",
            settingsStore.Current.InstallDestination,
            allowCreate: true);
        if (selected is null)
        {
            return new LauncherActionResult(false, "Выбор папки установки отменён");
        }

        var fullPath = Path.GetFullPath(selected);
        Directory.CreateDirectory(fullPath);
        var settings = settingsStore.Current.Copy();
        settings.InstallDestination = fullPath;
        await settingsStore.SaveAsync(settings, cancellationToken);
        return new LauncherActionResult(true, "Папка установки сохранена");
    }

    public async Task<UpdateApplyResult> InstallAsync(
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var media = DetectMedia();
        if (!media.Available)
        {
            throw new FileNotFoundException(media.StatusText, media.MediaRoot);
        }

        var destination = settingsStore.Current.InstallDestination;
        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new InvalidOperationException("Сначала выберите папку установки.");
        }

        Directory.CreateDirectory(destination);
        var coordinator = CreateCoordinator();
        var stateRoot = Path.Combine(settingsStore.DataRoot, "Installer");
        var check = await coordinator.CheckAsync(
            Path.Combine(_mediaRoot, "manifest.json"),
            Path.Combine(_mediaRoot, "install.public.pem"),
            InstallChannel,
            stateRoot,
            cancellationToken);
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["game"] = destination,
            ["engine"] = destination,
            ["database"] = destination,
            ["modpack"] = destination,
            ["mods"] = destination,
            ["tools"] = destination,
        };
        var result = await coordinator.ApplyAsync(check, roots, stateRoot, progress, null, cancellationToken);

        if (!File.Exists(Path.Combine(destination, "fsgame.ltx"))
            || !Directory.Exists(Path.Combine(destination, "bin")))
        {
            throw new InvalidDataException("Комплект установлен, но не содержит обязательные fsgame.ltx и bin.");
        }

        var settings = settingsStore.Current.Copy();
        settings.GameRoot = destination;
        await settingsStore.SaveAsync(settings, cancellationToken);
        return result;
    }

    private UpdateCoordinator CreateCoordinator() => new(
        httpClient,
        [
            new BundleFileMirrorResolver(_mediaRoot),
            new YandexDiskMirrorResolver(httpClient),
            new LocalFileMirrorResolver(),
            new DirectMirrorResolver(),
        ]);
}
