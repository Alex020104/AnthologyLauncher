ANTHOLOGY INSTALL MEDIA

This folder is reserved for the game files shipped together with the launcher.

Required files for an installable release:
  manifest.json
  install.public.pem
  packages\*.zip

The signed manifest must use channel "install" and stable mirrors such as:
  provider: bundle-file
  url:      bundle:///packages/anthology-base.zip

Package install roots may be game, engine, database, modpack, mods or tools.
Every archive is still checked against its declared size, SHA-256 and exact file list.
The private signing key must never be placed in this folder or distributed with the launcher.
