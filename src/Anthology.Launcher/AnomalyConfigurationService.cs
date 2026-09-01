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

        var target = kind == AnomalyConfigurationKind.Mcm
            ? snapshot.McmPath
            : snapshot.UserLtxPath;
        return Task.Run(() => AnomalyConfigurationManager.RestoreLatestBackup(target));
    }
}
