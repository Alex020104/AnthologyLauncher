# Anthology Launcher

Официальный лаунчер Anthology 2.1 для S.T.A.L.K.E.R. Anomaly.

Лаунчер запускает Mod Organizer 2, хранит локальные настройки, обновляет сборку,
DB-архивы и движок, а также умеет самообновляться через GitHub Release.

## Что Умеет

- запускать `ModOrganizer.exe` из папки `SYS_A.N.T.H.O.L.O.G.Y_mo2_CBT`;
- автоматически чинить пути в `ModOrganizer.ini` под текущую папку установки;
- обновлять MO2-модпак из `Alex020104/anthology-mo2-modpack`;
- обновлять DB из `Alex020104/anthology-db`;
- обновлять движок MT `2026.5.8` из `Alex020104/anthology-mt-engine`;
- ставить все файлы из архивов движка, включая `.pdb` для отладки;
- проверять обновления при запуске и показывать статус в центре обновлений;
- очищать кэш шейдеров и открывать папку логов.

## Структура Установки

Лаунчер рассчитан на две соседние папки. Буква диска и родительский путь могут
быть любыми:

```text
<любая папка>\
  Anomaly-1.5.3-Anthology 2.1\
    AnomalyLauncher.exe
    bin\
    db\
    fsgame.ltx

  SYS_A.N.T.H.O.L.O.G.Y_mo2_CBT\
    ModOrganizer.exe
    ModOrganizer.ini
    mods\
```

Перед запуском MO2 лаунчер прописывает в `ModOrganizer.ini` правильные пути к
текущей папке игры и делает backup вида `ModOrganizer.ini.bak_YYYYMMDD_HHMMSS`.

## Публичные Обновления

Самообновление читает:

```text
launcher_version.json
```

После этого скачивается:

```text
https://github.com/Alex020104/AnthologyLauncher/releases/latest/download/AnomalyLauncher.exe
```

Пользователям Git не нужен.

## Сборка

```powershell
py -3 -m py_compile anthology_launcher.py
py -3 -m PyInstaller --noconfirm AnthologyLauncherModern.spec
```

Готовый файл:

```text
dist\AnomalyLauncher.exe
```

## Релиз

Публичный релиз лаунчера делается из рабочего репозитория
`F:\Editor_Stalker\Anthology-Work-Git` через release-helper:

```powershell
py -3 F:\Editor_Stalker\Anthology-Work-Git\skills\anthology-release-ops\scripts\anthology_release_ops.py launcher --version YYYY.MM.DD.N --notes "Описание обновления"
```

Скрипт:

1. обновляет `LAUNCHER_VERSION`;
2. обновляет `launcher_version.json`;
3. компилирует Python;
4. собирает `dist\AnomalyLauncher.exe`;
5. коммитит и пушит `main`;
6. заменяет `AnomalyLauncher.exe` в latest GitHub Release.

## Связанные Репозитории

- `Alex020104/AnthologyLauncher` - этот лаунчер;
- `Alex020104/anthology-mo2-modpack` - MO2-модпак;
- `Alex020104/anthology-db` - DB-манифест и release assets;
- `Alex020104/anthology-mt-engine` - закрепленный движок Anthology.

## Правила

- Не копировать собранный exe в папку игры руками при релизе, если это не
  отдельная локальная проверка.
- Не публиковать тестовые версии без bump `LAUNCHER_VERSION`.
- Не менять URL обновлений на upstream-репозитории без явного решения.
- Перед изменением логики обновлений проверять `anthology-release-ops`.
