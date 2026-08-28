# Anthology Releaser Next

Releaser Next is a separate WPF/Blazor desktop application. Its default deployment root is `E:\AnthologyReleaserNext`; it does not live inside the player launcher and does not modify a game while editing a release.

## Local versus shared state

`App\Data\machine-settings.json` remains local and contains:

- developer name;
- game/MO2 source paths;
- output path;
- signing-key paths and key id;
- selected local addon archives and per-source publication folders;
- shared-folder location and automatic-sync settings.

`App\Data\release-workspace.json` contains the shareable release project:

- manually selected version;
- channel and artifact mirrors;
- library, news and information content with Russian, English and German text;
- revision, author and modification timestamp.

Synchronization copies only the release workspace to `anthology-release-workspace.json` in a folder selected by the developers. That folder can be backed by Yandex Disk, Google Drive desktop sync, a network share or another file synchronization system. Local paths and private keys are never placed in the shared document.

When both local and shared copies changed after the last common hash, neither side is overwritten. Both JSON variants are saved under `Conflicts` for a deliberate merge.

## Release workflow

1. Select a prepared game root and prepared MO2 root.
2. Enter `2.1.131` or a later `2.1.N` version.
3. Add download URLs and, when automatic upload is needed, a local publication folder for each provider. This can be a Yandex Disk/Google Drive synchronized folder, a mounted server share, or an HTTP server's staging directory.
4. Edit Russian, English and German versions of library/news/information documents and optional MO2 mod archives.
5. Save or synchronize the workspace.
6. Use **Выпустить всю сборку**. The releaser creates the complete snapshot and copies it to every configured publication folder.

Output is written to `<output>\<version>` and includes both ZIP artifacts, `manifest.json`, `content.json` and a workspace snapshot. The private signing key is never copied to output.

## Addons and removal

- **Опубликовать в библиотеке** copies only the selected mod archive, recalculates size and SHA-256, refreshes the signed catalog and uploads the changed files without rebuilding game/MO2 archives. The launcher downloads, verifies and installs it into the selected MO2 profile.
- **Снять мод** removes its archive and catalog entry from publication while retaining the editable card as a draft.
- **Снять версию** removes the complete version from every configured publication folder.
- Removed data is first moved to `<output>\.releaser-trash`, so an accidental removal remains recoverable.
- For full game updates, packages use an exact managed snapshot. Files removed from the developer's prepared source are removed on the player's next update by the launcher's transactional updater; player saves and excluded personal paths stay intact.

Publication folders perform the physical upload through the corresponding desktop sync client or mounted server directory. Public URL fields must point to direct downloadable files; a normal share page is not a download API.

## Publish locally

```powershell
scripts\Publish-Releaser.ps1 -Destination E:\AnthologyReleaserNext
```

Launch with `E:\AnthologyReleaserNext\Launch Anthology Releaser Next.cmd`.
