# Anthology Update Protocol v2

Protocol v2 publishes one Anthology version (starting at `2.1.131`) as a single signed manifest. The releaser automatically creates the two physical artifacts required by the installed layout:

- `anthology-game-<version>.zip` targets the selected Anomaly root;
- `anthology-mo2-<version>.zip` targets the selected Mod Organizer 2 root.

These are not independent DB/MT/MO2 releases. They share one version, one signature, one apply batch and one rollback batch.

## Full managed synchronization

Releaser packages use `managedExact` and `pruneInstallRoot`:

1. every current file is listed in the signed manifest;
2. the launcher compares the new list with its previous managed index;
3. when root pruning is enabled, legacy files not present in the new release are also selected;
4. `preservedPaths` keeps personal and transient data such as Anomaly `appdata`, logs, screenshots, MO2 `overwrite`, caches and local INI files;
5. every replacement and deletion is backed up before the target changes.

The first v2 update can therefore take ownership of an existing legacy installation without deleting saves or local launcher/MO2 settings. Unknown files outside the explicitly preserved paths are considered part of the managed Anthology assembly and are removed if the release no longer contains them.

## Atomic release batch

All package transactions belong to one release batch. If either target fails:

- completed target operations are rolled back in reverse order;
- the previous version state and managed-file indexes are restored;
- the release is not recorded as installed.

The user's Rollback action also restores every target from the latest batch, not one archive at a time.

## Mirrors

Each artifact contains an ordered mirror list. Supported provider names are:

- `github`;
- `yandex-disk` (public link resolved through the Yandex Disk download API);
- `google-drive` (use a direct downloadable URL / Drive `webContentLink`);
- `http` (any HTTPS/CDN URL);
- `local-file` for private tests only.

The launcher tries the user's preferred provider first and then falls back to the remaining signed mirrors. Every completed artifact must match both signed size and SHA-256.

## Signed content catalog

The v2 manifest can include a `content` catalog containing mods, news and information articles. A document supports:

- full text;
- image URLs;
- embedded video URLs;
- an optional downloadable file with size, SHA-256 and its own mirror list.

The catalog is covered by the same ECDSA P-256 manifest signature. The launcher caches only a successfully verified manifest and verifies that cache again before displaying it after restart.
