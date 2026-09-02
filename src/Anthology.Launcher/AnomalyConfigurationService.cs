using Anthology.Mo2.Core;

namespace Anthology.Launcher;

public sealed class AnomalyConfigurationService(
    LauncherSettingsStore settingsStore,
    Mo2IntegrationService mo2)
{
    public Task<AnomalyConfigurationSnapshot> LoadAsync() => Task.Run(() =>
        AnomalyConfigurationManager.Load(
            settingsStore.Current.GameRoot,
            settingsStore.Current.ModpackRoot));

    public Task<AnomalyConfigurationWriteResult> SaveAsync(AnomalyConfigurationSnapshot snapshot)
    {
        if (mo2.RuntimeBusy)
        {
            return Task.FromResult(new AnomalyConfigurationWriteResult(
                false,
                "Закройте Anomaly и Mod Organizer 2 перед изменением настроек"));
        }

        return Task.Run(() => AnomalyConfigurationManager.Save(snapshot));
    }

    public Task<AnomalyConfigurationWriteResult> RestoreLatestBackupAsync(
        AnomalyConfigurationSnapshot snapshot,
        AnomalyConfigurationKind kind)
    {
        if (mo2.RuntimeBusy)
        {
            return Task.FromResult(new AnomalyConfigurationWriteResult(
                false,
                "Закройте Anomaly и Mod Organizer 2 перед восстановлением настроек"));
        }

        var targets = (kind == AnomalyConfigurationKind.Mcm
                ? snapshot.McmSettings
                : snapshot.AnomalySettings)
            .Select(item => item.TargetPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Task.Run(() =>
        {
            if (targets.Length == 0)
            {
                return new AnomalyConfigurationWriteResult(false, "Файлы настроек не найдены");
            }

            var results = targets.Select(AnomalyConfigurationManager.RestoreLatestBackup).ToArray();
            var restored = results.Count(item => item.Success);
            var failed = results.Where(item => !item.Success).ToArray();
            return restored > 0
                ? new AnomalyConfigurationWriteResult(
                    true,
                    failed.Length == 0
                        ? $"Восстановлено файлов настроек: {restored}"
                        : $"Восстановлено файлов: {restored}. Без резервной копии: {failed.Length}")
                : new AnomalyConfigurationWriteResult(false, string.Join(" · ", failed.Select(item => item.Message)));
        });
    }
}
