# Anthology Releaser Next

Releaser Next is a separate WPF/Blazor desktop application. Its default deployment root is `E:\AnthologyReleaserNext`; it does not live inside the player launcher and does not modify a game while editing a release.

## Local versus shared state

`App\Data\machine-settings.json` remains local and contains:

- developer name;
- game/MO2 source paths;
- output path;
- signing-key paths and key id;
- selected local addon archives and per-source publication folders;
- selected PNG/JPG/WEBP sources and the pending quick-release add/delete lists;
- shared-folder location and automatic-sync settings.

`App\Data\release-workspace.json` contains the shareable release project:

- manually selected version;
- channel and artifact mirrors;
- library, news and information content with Russian, English, German, Polish, French, Spanish, Simplified Chinese and Japanese text;
- revision, author and modification timestamp.

Synchronization copies only the release workspace to `anthology-release-workspace.json` in a folder selected by the developers. That folder can be backed by Yandex Disk, Google Drive desktop sync, a network share or another file synchronization system. Local paths and private keys are never placed in the shared document.

When both local and shared copies changed after the last common hash, neither side is overwritten. Both JSON variants are saved under `Conflicts` for a deliberate merge.

## Release workflow

1. Enter `2.1.131` or a later `2.1.N` version.
2. Add direct download URL templates and a local publication folder for every provider. This can be a GitHub working tree, Yandex Disk/Google Drive synchronized folder, mounted server share, or HTTP server staging directory.
3. For a normal patch, use **Файлы в корень игры** and **Файлы в MO2** as many times as needed. Edit destination paths if required.
4. Add explicit relative paths under **Удаление у игроков** for files that must be removed. The launcher backs them up before deletion and includes them in rollback.
5. Use **Опубликовать выбранные файлы**. One signed release is copied to every configured publication folder.
6. Use the prepared game/MO2 roots and **Выпустить всю сборку** only when a complete exact snapshot is required.

Output is written to `<output>\<version>` and includes both ZIP artifacts, `manifest.json`, `content.json` and a workspace snapshot. The private signing key is never copied to output.

## Addons and removal

- **Опубликовать в библиотеке** copies only the selected mod archive, recalculates size and SHA-256, refreshes the signed catalog and uploads the changed files without rebuilding game/MO2 archives. The launcher downloads, verifies and installs it into the selected MO2 profile.
- **Снять мод** removes its archive and catalog entry from publication while retaining the editable card as a draft.
- **Снять версию** removes the complete version from every configured publication folder.
- **Удалить этот выпуск из источников** is the same safe operation in the quick-release panel and always creates a backup first.
- Removed data is first moved to `<output>\.releaser-trash`, so an accidental removal remains recoverable.
- For full game updates, packages use an exact managed snapshot. Files removed from the developer's prepared source are removed on the player's next update by the launcher's transactional updater; player saves and excluded personal paths stay intact.

Publication folders perform the physical upload through the corresponding Git client, desktop sync client or mounted server directory. Public URL fields must point to direct downloadable files; a normal share page is not a download API.

## Photos and videos

Library, news and information cards accept PNG, JPG, JPEG and WEBP through the system file picker. The releaser keeps those paths local, copies the selected files under `addons/<content-id>/media`, publishes them to every configured folder and writes the primary direct HTTPS URL into the signed catalog. The content URL template must therefore follow `<base>/{version}/addons/{id}/{file}`.

Videos are not uploaded. Enter `Title | HTTPS URL`; YouTube, VK, ModDB embeds and direct MP4/WEBM/OGV files are displayed inside the launcher.

## Automatic translation

The content editor supports LibreTranslate-compatible endpoints. The endpoint URL and API key are stored only in local `machine-settings.json`; they are not synchronized with the shared release workspace and are not written to player catalogs.

The source language can be detected automatically or selected explicitly. Automatic translation fills all eight locale tabs for the main document and its information blocks. Every generated field remains editable before signing and publication. A remote endpoint must use HTTPS; loopback HTTP is accepted for a self-hosted developer instance.

## Editorial ownership

News and information contain no permanent launcher-owned entries. On the first schema-3 migration, the previous built-in requirements, project descriptions, news and story cards are imported into the shared workspace as unpublished editable drafts. The migration is one-time, so a deliberately deleted entry is never recreated.

Both sections support create, edit, ordering, unpublish and full delete operations. Full deletion of a published item first updates the signed catalog and publication targets, then removes the workspace draft. Information also supports nested article cards for story catalogs; every card has editable localized copy and an optional uploaded background photograph.

## Publish locally

```powershell
scripts\Publish-Releaser.ps1 -Destination E:\AnthologyReleaserNext
```

Launch with `E:\AnthologyReleaserNext\Launch Anthology Releaser Next.cmd`.
