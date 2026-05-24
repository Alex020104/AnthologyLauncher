# Anthology Launcher

Modern Python/Tkinter launcher for Anthology 2.1.

## Build

```powershell
py -3 -m PyInstaller --noconfirm AnthologyLauncherModern.spec
```

The built executable is created at:

```text
dist\AnomalyLauncher.exe
```

For release packaging, copy it into the game folder.

## Launcher Updates

Self-update metadata lives in `launcher_version.json`.

For a new public launcher update:

1. Increase `LAUNCHER_VERSION` in `anthology_launcher.py`.
2. Build `dist\AnomalyLauncher.exe`.
3. Publish a GitHub release and upload `AnomalyLauncher.exe`.
4. Update `launcher_version.json` with the same version and push it to `main`.
