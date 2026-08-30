# Пути источников

## GitHub — ветка `addons-unified-library`

- Manifest: `https://raw.githubusercontent.com/Alex020104/AnthologyLauncher/addons-unified-library/manifest.json`
- Архив игры: `https://raw.githubusercontent.com/Alex020104/AnthologyLauncher/addons-unified-library/{version}/{file}`
- Архив MO2: `https://raw.githubusercontent.com/Alex020104/AnthologyLauncher/addons-unified-library/{version}/{file}`
- Аддоны и фотографии: `https://raw.githubusercontent.com/Alex020104/AnthologyLauncher/addons-unified-library/{version}/addons/{id}/{file}`
- Архив из единой библиотеки: `https://raw.githubusercontent.com/Alex020104/AnthologyLauncher/addons-unified-library/library/<категория>/<архив>.7z`

## Яндекс.Диск

Замените `ВАШ_PUBLIC_KEY` на публичный ключ одной общей папки Яндекс.Диска.

- Manifest: `https://disk.yandex.ru/d/ВАШ_PUBLIC_KEY?path=/manifest.json`
- Архив игры: `https://disk.yandex.ru/d/ВАШ_PUBLIC_KEY?path=/{version}/{file}`
- Архив MO2: `https://disk.yandex.ru/d/ВАШ_PUBLIC_KEY?path=/{version}/{file}`
- Аддоны и фотографии: `https://disk.yandex.ru/d/ВАШ_PUBLIC_KEY?path=/{version}/addons/{id}/{file}`

Launcher Next понимает параметр `path` и получает прямую ссылку через официальный API Яндекс.Диска.
