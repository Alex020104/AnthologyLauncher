# Anthology Releaser Next

Releaser Next is a separate WPF/Blazor desktop application. Its default deployment root is `E:\AnthologyReleaserNext`; it does not live inside the player launcher and does not modify a game while editing a release.

## Local versus shared state

`App\Data\machine-settings.json` remains local and contains:

- developer name;
- game/MO2 source paths;
- output path;
- signing-key paths and key id;
- shared-folder location and automatic-sync settings.

`App\Data\release-workspace.json` contains the shareable release project:

- manually selected version;
- channel and artifact mirrors;
- library, news and information content;
- revision, author and modification timestamp.

Synchronization copies only the release workspace to `anthology-release-workspace.json` in a folder selected by the developers. That folder can be backed by Yandex Disk, Google Drive desktop sync, a network share or another file synchronization system. Local paths and private keys are never placed in the shared document.

When both local and shared copies changed after the last common hash, neither side is overwritten. Both JSON variants are saved under `Conflicts` for a deliberate merge.

## Release workflow

1. Select a prepared game root and prepared MO2 root.
2. Enter `2.1.131` or a later `2.1.N` version.
3. Add download URLs for each enabled provider.
4. Edit library/news/information documents and optional mod archives.
5. Save or synchronize the workspace.
6. Build the release.

Output is written to `<output>\<version>` and includes both ZIP artifacts, `manifest.json`, `content.json` and a workspace snapshot. The private signing key is never copied to output.

## Publish locally

```powershell
scripts\Publish-Releaser.ps1 -Destination E:\AnthologyReleaserNext
```

Launch with `E:\AnthologyReleaserNext\Launch Anthology Releaser Next.cmd`.
