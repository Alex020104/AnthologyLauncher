import os
import hashlib
import json
import re
import shutil
import subprocess
import stat
import sys
import tempfile
import threading
import time
import webbrowser
import zipfile
from io import BytesIO
from pathlib import Path
from urllib.error import URLError
from urllib.parse import quote
from urllib.request import Request, urlopen
import tkinter as tk
import tkinter.font as tkfont
from tkinter import messagebox
from PIL import Image, ImageEnhance, ImageTk


def bundled_asset_dir():
    if getattr(sys, "frozen", False):
        return Path(getattr(sys, "_MEIPASS")) / "assets"
    return Path(__file__).resolve().parent / "assets"


def read_update_rules():
    path = bundled_asset_dir() / "update_rules.json"
    if not path.exists():
        return {}
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return {}


UPDATE_RULES = read_update_rules()
MO2_RULES = UPDATE_RULES.get("mo2", {})
DB_RULES = UPDATE_RULES.get("db", {})


WIDTH = 1180
HEIGHT = 720
SCREEN_PADDING = 24
MIN_UI_SCALE = 0.72
TOP_BAR = 96
MARGIN = 72
RENDERERS = ["DX11", "DX10", "DX9", "DX8"]
RENDER_LABELS = {
    "DX11": "DirectX 11 / R4",
    "DX10": "DirectX 10 / R2",
    "DX9": "DirectX 9 / R1",
    "DX8": "DirectX 8 / R0",
}
SHADOWS = [1536, 2048, 2560, 3072, 4096]
LAUNCHER_VERSION = "2026.08.25.2"
LAUNCHER_VERSION_URL = "https://api.github.com/repos/Alex020104/AnthologyLauncher/contents/launcher_version.json?ref=main"
LAUNCHER_VERSION_RAW_URL = "https://raw.githubusercontent.com/Alex020104/AnthologyLauncher/main/launcher_version.json"
LAUNCHER_EXE_URL = "https://github.com/Alex020104/AnthologyLauncher/releases/latest/download/AnomalyLauncher.exe"
LAUNCHER_EXE_NAME = "AnomalyLauncher.exe"
MOD_ORGANIZER_EXE_NAME = "ModOrganizer.exe"
ENGINE_RELEASE_VERSION = "2026.06.03.1"
ENGINE_MT_URL = "https://github.com/Alex020104/anthology-mt-engine/releases/download/2026.06.03.1/STALKER-Anomaly-modded-exes-MT-TEST_2026.06.03.1.zip"
ENGINE_VERSION_URL = "https://api.github.com/repos/Alex020104/anthology-mt-engine/contents/engine_version.json?ref=main"
ENGINE_VERSION_RAW_URL = "https://raw.githubusercontent.com/Alex020104/anthology-mt-engine/main/engine_version.json"
ENGINE_ALLOWED_PARTS = {"bin", "db"}
MODPACK_FOLDER = "SYS_A.N.T.H.O.L.O.G.Y_mo2_CBT"
MODPACK_REPO = "https://github.com/Alex020104/anthology-mo2-modpack"
UPDATE_VERSION_URL = "https://raw.githubusercontent.com/Alex020104/anthology-mo2-modpack/main/version.json"
UPDATE_VERSION_API_URL = "https://api.github.com/repos/Alex020104/anthology-mo2-modpack/contents/version.json?ref=main"
UPDATE_ZIP_URL = "https://github.com/Alex020104/anthology-mo2-modpack/archive/refs/heads/main.zip"
UPDATE_ALLOWED_PARTS = set(MO2_RULES.get("allowed_parts", ["configs", "scripts", "textures"]))
UPDATE_MANAGED_STANDARD_FOLDERS = {name.casefold() for name in MO2_RULES.get("managed_standard_folders", [])}
UPDATE_MANAGED_FULL_FOLDERS = {name.casefold() for name in MO2_RULES.get("managed_full_folders", [
    "[WPN][100][SPL][R.A.K. Weapon Pack Adaptation Global Simple Patch]",
])}
UPDATE_MANAGED_FOLDERS = UPDATE_MANAGED_STANDARD_FOLDERS | UPDATE_MANAGED_FULL_FOLDERS
UPDATE_PRESERVE_PATH_MARKERS = (
    "r.a.k weapon pack adaptation",
)
UPDATE_LEGACY_REMOVE_PATHS = {
    "plugins/SetAnomalyCPUAffinity.py",
}
DB_REPO = "https://github.com/Alex020104/anthology-db"
DB_UPDATE_VERSION_URL = "https://api.github.com/repos/Alex020104/anthology-db/contents/db_version.json?ref=main"
DB_UPDATE_VERSION_RAW_URL = "https://raw.githubusercontent.com/Alex020104/anthology-db/main/db_version.json"
GAME_PAYLOAD_REPO = "https://github.com/Alex020104/anthology-game-files"
GAME_PAYLOAD_VERSION_URL = "https://api.github.com/repos/Alex020104/anthology-game-files/contents/version.json?ref=main"
GAME_PAYLOAD_VERSION_RAW_URL = "https://raw.githubusercontent.com/Alex020104/anthology-game-files/main/version.json"
DB_ALLOWED_PARTS = {"configs", "mods"}
DB_ROOT_FILES = {
    "shaders_anthology.xdb0",
}
DB_ALLOWED_FILES = {
    "db/textures/textures_trees.xdb0",
    "db/textures/textures_trees.xdb1",
    "db/textures/textures_trees.xdb3",
}
DB_ALLOWED_FILES.update(path.casefold() for path in DB_RULES.get("source_files", {}))
DB_PRESERVE_PATHS = {
    "db/mods/00_modded_exes_gamedata.db0",
}

COLORS = {
    "bg": "#050807",
    "glass": "#07100f",
    "glass_soft": "#0a1412",
    "glass_lift": "#0e1d1a",
    "line": "#ffffff",
    "line_soft": "#78d8c7",
    "accent": "#8beedb",
    "accent_2": "#d4b76a",
    "text": "#edf7f1",
    "muted": "#b7c6bf",
    "faint": "#82948c",
    "danger": "#f0b7a8",
}


TEXT = {
    "ru": {
        "play_online": "Играть онлайн",
        "play": "Играть с модпаком",
        "play_original": "Играть в оригинал",
        "settings": "Настройки",
        "back": "Назад",
        "save": "Сохранить",
        "cache": "Очистить кэш",
        "logs": "Открыть логи",
        "about": "О проекте",
        "projects": "Библиотека новых проектов и решений",
        "projects_title": "Библиотека",
        "projects_intro": "Здесь будут собраны ссылки на скачивание и дополнительные материалы Anthology.",
        "projects_hint": "Раздел подготовлен под кнопки скачивания, сборки и внешние страницы.",
        "projects_dev_title": "Личные проекты разработчиков",
        "projects_dev_desc": "Здесь лежат отдельные механики, которые находятся в активном тестировании или имеют сложное техническое исполнение",
        "projects_modmakers_title": "Новые проекты от модмейкеров",
        "projects_modmakers_desc": "Здесь лежат отдельные проекты модмейкеров, которые являются дополнением к игре в виде разнообразия.",
        "projects_solutions_title": "Решения спорных механик разработчиков",
        "projects_solutions_desc": "Здесь лежат аддоны, которые отменяют некоторые наши решения в целях упростить жизнь игроку.",
        "projects_empty": "Раздел подготовлен. Ссылки и файлы можно будет добавить сюда следующим шагом.",
        "projects_download": "Скачать",
        "projects_discord": "Discord",
        "projects_more": "Подробнее",
        "projects_image_missing": "Изображение не задано",
        "info": "Информация",
        "info_title": "Информация",
        "info_intro": "Справочная информация по Anthology, оригинальной игре и системным требованиям.",
        "info_requirements_title": "Системные требования",
        "info_requirements_desc": "Минимальные и рекомендуемые параметры для комфортной игры.",
        "info_original_title": "Информация об Оригинале",
        "info_original_desc": "Краткая справка об Anomaly и базовой игре.",
        "info_modpack_title": "Информация о Модпаке",
        "info_modpack_desc": "Что меняет Anthology и как устроена сборка.",
        "support": "Поддержать проект",
        "relay_chat": "Реальный чат",
        "relay_chat_missing": "Файл Relay Chat не найден",
        "relay_chat_update_hint": "Нажмите «Обновить», чтобы скачать Relay Chat.",
        "quit": "Выход",
        "news": "Новости проекта",
        "update": "Центр обновлений",
        "ready": "Готово к запуску",
        "build": "ANTHOLOGY 2.1",
        "channel": "Open Beta",
        "server": "Сервер обновлений будет подключен позже",
        "update_button": "Обновить",
        "engine_button": "Движок",
        "update_ready": "Готово к проверке обновлений",
        "update_checking": "Проверка версии...",
        "update_downloading": "Скачивание обновления...",
        "update_available": "Есть обновление",
        "update_available_downloading": "Доступно обновление, скачивание...",
        "update_none": "Обновлений нет",
        "update_check_failed": "Не удалось проверить обновления",
        "update_applying": "Установка обновления...",
        "update_preparing": "Подготовка файлов...",
        "update_done": "Модпак обновлен.",
        "update_latest": "Уже установлена последняя версия.",
        "update_repair": "Восстановление модпака: скачивание недостающих файлов...",
        "update_missing": "Не найдена папка модпака",
        "update_expected": "Папка модпака должна лежать рядом с папкой игры",
        "update_failed": "Не удалось обновить модпак",
        "update_blocked_mo2": "Лаунчер запущен через Mod Organizer 2 или Mod Organizer 2 сейчас открыт.\n\nЧтобы избежать повреждения файлов и ошибки \"процесс занят\", обновление отключено.\n\nЗакройте Mod Organizer 2 и запустите лаунчер напрямую для обновления.",
        "db_checking_process": "DB: проверка запущенной игры...",
        "db_close_game": "Закройте игру перед обновлением DB",
        "db_missing": "Папка DB не найдена",
        "db_failed": "Не удалось обновить DB",
        "db_no_version": "db_version.json не содержит версию",
        "db_no_files": "db_version.json не содержит файлов",
        "db_removing_extra": "DB: удаление лишних архивов...",
        "db_latest": "DB уже обновлена.",
        "db_done": "DB обновлена.",
        "db_checking_hashes": "DB: проверка хэшей",
        "db_downloading": "DB: скачивание",
        "label_version": "Версия",
        "label_removed_files": "Удалено лишних файлов",
        "label_removed_old_files": "Удалено старых файлов",
        "label_removed_empty_dirs": "Удалено пустых папок",
        "label_downloaded_files": "Скачано файлов",
        "news_1": "Добавление аддонов",
        "news_1_body": "1.  [GFX] Atmospherics 2.68.93_hotfix3 + Weather Expansion v3.7 (ADDS 21 WEATHER PRESETS) — Дополнительные погодные циклы + Атмосферикс от 𝙍𝙚𝙑𝙤_𝙊𝖓𝖑𝟏𝖓𝖊 \n2.  [GFX] Weather Expansion - OTHER PSI-STORMS — Переработанная погода + Пси-шторм от 𝙍𝙚𝙑𝙤_𝙊𝖓𝖑𝟏𝖓𝖊 \n3.  [GFX] S.T.A.L.K.E.R. 2 - Legacy LUT 2021 — Цветокорекция от 𝙍𝙚𝙑𝙤_𝙊𝖓𝖑𝟏𝖓𝖊 \n4. [HARD] SYS Cold System — Система холода + New MCM functions \nНужно выключить старый [HARD] SYS Cold System, если вам нужна эта версия\n5.  [HUD] SquareDOV Minimap Universal — Универсальная миникарта от 𝙍𝙚𝙑𝙤_𝙊𝖓𝖑𝟏𝖓𝖊 \n6. [HUD] High Quality MAPS — 4K карты для КПК от 𝙍𝙚𝙑𝙤_𝙊𝖓𝖑𝟏𝖓𝖊 \nДобавлен в новые проекты через вкладку в лаунчере, слишком большой объём для быстрой скачки.\n7. [GFX] G2X Tactical Light Presets + MCM — Регулируемые Фонари через MCM меню от 𝙍𝙚𝙑𝙤_𝙊𝖓𝖑𝟏𝖓𝖊 \nНастраиваемые пресеты фонаря через MCM: Цвет, радиус и освещение",
        "news_2": "New Addons and Changes",
        "news_2_body": "Всем привет!!!\nСегодня я загрузил аддоны/исправления от авторов @❄Kristiano❄| Никита и @YuriVernadsky , также от меня обновлён [100]ый патч\nФиксы от @❄Kristiano❄| Никита  лежат в аддоне [DBG] Kristiano Fixes ALL IN ONE:\n21.Anthology ST2 Mutant Footstep Sound Fix v1.0.0 - исправляет повреждённые и неверные пути звуков шагов мутантов из S.T.A.L.K.E.R. 2.\n\n22.Catspaw Runtime Cleanup Fix v1.0.0 BETA - убирает обращения Catspaw/PAW к удалённым объектам и исправляет устаревшие пути настроек Milspec PDA.\n\n23.Anthology Context Menu Integrated Helmet Repair Fix v1.0.0 BETA - восстанавливает ремонт шлемовой части комбинезонов с интегрированным шлемом через контекстное меню.\n\n24.Anthology_Sorting_Plus_Categories_Fix_v1.0.0_BETA.zip\nИсправлены категории цифровых вкладок: еда, медицина, ремонт, документы и детали больше не смешиваются.\n\n25.Seamless_Inventory_Sort_Anthology_2.1_v1.1.0_BETA.zip\nОптимизирована сортировка инвентаря: массовые переносы и покупки объединяются в одну пересортировку, снижены просадки FPS.\n\n26.Context_Menu_Overhaul_1.4.2_Anthology_2.1_v1.1.0_BETA.zip\nОбновлена адаптация CMO до 1.4.2, добавлена совместимость с OPO и Exo System, исправлены действия ремонта и модулей питания.\n\n27.Anthology_LTTZ_Noosphere_Voice_X18_Fix_v1.0.1_BETA.zip\nИсправлено зависание задания «Голос Ноосферы» в X18 и добавлено восстановление уже сломанных сохранений.\n\n28.Anthology_Charon_Red_Forest_Travel_Fix_v1.0.1_BETA.zip\nИсправлен вылет при выборе у Харона перехода в Рыжий лес.\n\n29.Anthology_Ashot_Army_Warehouses_Travel_Fix_v1.0.5_BETA.zip\nИсправлен неправильный маршрут Ашота с Юпитера на Армейские склады, который мог отправлять игрока в X18.\nАддоны от @YuriVernadsky :\n[TMA] CMS — Новые анимации аптечек \nУ вас теперь более красивые анимации обычной, армейской и научной аптечек\n[GAM] Campfires_placeable_ANTHOLOGY_CreditsBVCX \nТеперь есть переносной костёр\nТакже обновлён Осенний пак, вы можете найти его в библиотеке модов.\nВ [100] патче изменения лично от меня:\n1. Теперь торговцы Оружейник Дэн и Наёмник в Припяти из аддона на западные товары имеют существенную прибавку в ассортименте товаров на оружие.Также увеличено количество продаваемых патронов у всех торговцев в 4 раза\n2. Звуки у семейства UZI и MAC-10 были исправлены, также многие мелкие ПП ставятся в пистолетный слот",
        "news_3": "Эпический Карч",
        "news_3_body": "1. Установлены фиксы от Kristiano одним аддоном [DBG] Kristiano Fixes ALL IN ONE для удобства пользователей(временно), пока мы их не зальём в DB архивы\n2. Переделан Toxic Air. Теперь уровни фильтров и балонов имеют значение не только в защите, но в скорости расхода воздуха\n3. Патроны теперь продаются в большем объёме(x4) и теперь оружейник Дэн имеет более распространённый арсенал.Также исправлен ТОЗ-34 Bull.\n4. Адаптирован аддон дистанционной награды за определённые квесты(которые не требуют что-либо обратно принести), кроме сюжетных.Аддон [GAM] Autocomplete Tasks\n5. Добавлены батареи для экзоскелетов. Теперь вам нужны батареи, иначе вы не сможете адекватно пользоваться экзоскелетом",
        "debug": "Режим отладки",
        "sound_fix": "Обход проблем со звуком",
        "prefetch": "Предзагрузка звуков",
        "chat_relay_always": "Реальный Чат всегда",
        "reset": "Стандартный user.ltx",
        "avx": "Поддержка AVX",
        "shadow": "Карта теней",
        "renderer": "Рендер",
        "saved": "Настройки сохранены.",
        "cache_missing": "Папка кэша шейдеров не найдена.",
        "cache_done": "Кэш шейдеров удален.",
        "launch_error": "Не удалось запустить игру",
        "mo2_missing": "Не найден Mod Organizer 2",
        "mo2_expected": "Mod Organizer 2 должен лежать рядом с папкой игры",
    },
    "en": {
        "play_online": "Play online",
        "play": "Play with modpack",
        "play_original": "Play original",
        "settings": "Settings",
        "back": "Back",
        "save": "Save",
        "cache": "Clear cache",
        "logs": "Open logs",
        "about": "About",
        "projects": "Library of new projects and solutions",
        "projects_title": "Library",
        "projects_intro": "Download links and additional Anthology materials will be collected here.",
        "projects_hint": "This section is prepared for download buttons, builds, and external pages.",
        "projects_dev_title": "Developers' personal projects",
        "projects_dev_desc": "Separate mechanics that are in active testing or have complex technical implementation are stored here.",
        "projects_modmakers_title": "New projects from modmakers",
        "projects_modmakers_desc": "Separate modmaker projects that add more variety to the game are stored here.",
        "projects_solutions_title": "Developers' controversial-mechanics solutions",
        "projects_solutions_desc": "Addons that revert some of our decisions to make the player's life easier are stored here.",
        "projects_empty": "This section is ready. Links and files can be added here in the next step.",
        "projects_download": "Download",
        "projects_discord": "Discord",
        "projects_more": "Details",
        "projects_image_missing": "No image set",
        "info": "Information",
        "info_title": "Information",
        "info_intro": "Reference information about Anthology, the original game, and system requirements.",
        "info_requirements_title": "System requirements",
        "info_requirements_desc": "Minimum and recommended specs for comfortable play.",
        "info_original_title": "Original information",
        "info_original_desc": "Brief reference about Anomaly and the base game.",
        "info_modpack_title": "Modpack information",
        "info_modpack_desc": "What Anthology changes and how the build is structured.",
        "support": "Support project",
        "relay_chat": "Relay Chat",
        "relay_chat_missing": "Relay Chat file was not found",
        "relay_chat_update_hint": "Click Sync to download Relay Chat.",
        "quit": "Exit",
        "news": "Project news",
        "update": "Update center",
        "ready": "Ready to launch",
        "build": "ANTHOLOGY 2.1",
        "channel": "Open Beta",
        "server": "Update server will be connected later",
        "update_button": "Sync",
        "engine_button": "Engine",
        "update_ready": "Ready to check updates",
        "update_checking": "Checking version...",
        "update_downloading": "Downloading update...",
        "update_available": "Update available",
        "update_available_downloading": "Update available, downloading...",
        "update_none": "No updates",
        "update_check_failed": "Failed to check updates",
        "update_applying": "Applying update...",
        "update_preparing": "Preparing files...",
        "update_done": "Modpack updated.",
        "update_latest": "The latest version is already installed.",
        "update_repair": "Modpack repair: downloading missing files...",
        "update_missing": "Modpack folder was not found",
        "update_expected": "The modpack folder must be next to the game folder",
        "update_failed": "Failed to update modpack",
        "update_blocked_mo2": "The launcher is running through Mod Organizer 2, or Mod Organizer 2 is currently open.\n\nTo avoid file damage and \"process is busy\" errors, updates are disabled.\n\nClose Mod Organizer 2 and run the launcher directly to update.",
        "db_checking_process": "DB: checking game process...",
        "db_close_game": "Close the game before DB update",
        "db_missing": "DB folder was not found",
        "db_failed": "DB update failed",
        "db_no_version": "db_version.json has no version",
        "db_no_files": "db_version.json has no files",
        "db_removing_extra": "DB: removing extra archives...",
        "db_latest": "DB is already up to date.",
        "db_done": "DB updated.",
        "db_checking_hashes": "DB: checking hashes",
        "db_downloading": "DB: downloading",
        "label_version": "Version",
        "label_removed_files": "Removed extra files",
        "label_removed_old_files": "Removed old files",
        "label_removed_empty_dirs": "Removed empty folders",
        "label_downloaded_files": "Downloaded files",
        "news_1": "Adding add-ons",
        "news_1_body": "1. [GFX] Atmospherics 2.68.93_hotfix3 + Weather Expansion v3.7 (ADDS 21 WEATHER PRESETS) — Additional weather cycles + Atmospherics by 𝙍𝙚𝙑𝙤_𝙊𝖓𝖑𝟏𝖓𝖊\n2. [GFX] Weather Expansion - OTHER PSI-STORMS — Reworked weather + Psi-storm by 𝙍𝙚𝙑𝙤_𝙊𝖓𝖑𝟏𝖓𝖊\n3. [GFX] S.T.A.L.K.E.R. 2 - Legacy LUT 2021 — Color Correction by 𝙍𝙚𝙑𝙤_𝙊𝖓𝖑𝟏𝖓𝖊\n4. [HARD] SYS Cold System — Cold System + New MCM functions\nYou need to disable the old [HARD] SYS Cold System if you want this version.\n5. [HUD] SquareDOV Minimap Universal — Universal minimap by 𝙍𝙚𝙑𝙤_𝙊𝖓𝖑𝟏𝖓𝖊\n6. [HUD] High Quality MAPS — 4K maps for PDAs by 𝙍𝙚𝙑𝙤_𝙊𝖓𝖑𝟏𝖓𝖊\nAdded to new projects via tab in the launcher, too large for fast download.\n7. [GFX] G2X Tactical Light Presets + MCM — Adjustable Flashlights via MCM Menu by 𝙍𝙚𝙑𝙤_𝙊𝖓𝖑𝟏𝖓𝖊\nCustomizable flashlight presets via MCM: Color, radius, and illumination",
        "news_2": "Фиксы и правки",
        "news_2_body": "Hello everyone!!!\nToday I uploaded addons/fixes from @❄Kristiano❄| Nikita and @YuriVernadsky , and I also updated the [100th] patch.\nFixes from @❄Kristiano❄| Nikita are in the [DBG] addon. Kristiano Fixes ALL IN ONE:\n21. Anthology ST2 Mutant Footstep Sound Fix v1.0.0 - Fixes corrupted and incorrect footstep sound paths for mutants in S.T.A.L.K.E.R. 2.\n\n22. Catspaw Runtime Cleanup Fix v1.0.0 BETA - Removes Catspaw/PAW calls to remote objects and fixes outdated Milspec PDA settings paths.\n\n23. Anthology Context Menu Integrated Helmet Repair Fix v1.0.0 BETA - Restores helmet repairs for suits with an integrated helmet via the context menu.\n\n24. Anthology_Sorting_Plus_Categories_Fix_v1.0.0_BETA.zip\nCategories in the digital tabs have been fixed: food, medicine, repairs, documents, and parts are no longer mixed together.\n\n25. Seamless_Inventory_Sort_Anthology_2.1_v1.1.0_BETA.zip\nInventory sorting has been optimized: mass transfers and purchases are combined into a single re-sort, reducing FPS drops.\n\n26.Context_Menu_Overhaul_1.4.2_Anthology_2.1_v1.1.0_BETA.zip\nUpdated CMO adaptation to 1.4.2, added compatibility with OPO and Exo Systems, and fixed repair and power module actions.\n\n27.Anthology_LTTZ_Noosphere_Voice_X18_Fix_v1.0.1_BETA.zip\nFixed a freeze in the \"Voice of the Noosphere\" quest in X18 and added a save recovery feature.\n\n28.Anthology_Charon_Red_Forest_Travel_Fix_v1.0.1_BETA.zip\nFixed a crash when choosing Charon to travel to the Red Forest.\n\n29.Anthology_Ashot_Army_Warehouses_Travel_Fix_v1.0.5_BETA.zip\nFixed an incorrect route for Ashot from Jupiter to the Army Warehouses, which could send the player to X18.\nAddons by @YuriVernadsky:\n[TMA] CMS - New First Aid Kit Animations\nYou now have more beautiful animations for regular, army, and scientific First Aid Kits.\n[GAM] Campfires_placeable_ANTHOLOGY_CreditsBVCX\nNow there's a portable campfire.\nThe Autumn Pack has also been updated; you can find it in the mod library.\nIn patch [100], changes from me personally:\n1. Now the Gunsmith Dan and Mercenary merchants in Pripyat from the Western goods add-on have a significant increase in their selection of weapons. Also, the amount of ammunition sold by all merchants has been quadrupled.\n2. The sounds for the UZI and MAC-10 series have been corrected, and many small SMGs can now be used in the pistol slot.",
        "news_3": "Эпический Карч",
        "news_3_body": "1. Установлены фиксы от Kristiano одним аддоном [DBG] Kristiano Fixes ALL IN ONE для удобства пользователей(временно), пока мы их не зальём в DB архивы\n2. Переделан Toxic Air. Теперь уровни фильтров и балонов имеют значение не только в защите, но в скорости расхода воздуха\n3. Патроны теперь продаются",
        "debug": "Debug mode",
        "sound_fix": "Sound workaround",
        "prefetch": "Prefetch sounds",
        "chat_relay_always": "Chat Relay Always",
        "reset": "Default user.ltx",
        "avx": "AVX support",
        "shadow": "Shadow map",
        "renderer": "Renderer",
        "saved": "Settings saved.",
        "cache_missing": "Shader cache folder was not found.",
        "cache_done": "Shader cache deleted.",
        "launch_error": "Failed to launch the game",
        "mo2_missing": "Mod Organizer 2 was not found",
        "mo2_expected": "Mod Organizer 2 must be next to the game folder",
    },
}


LIBRARY_LINKS = {
    "dev": [
        {
            "title_ru": "PiP(Picture in Picture) для 3DSS в Anomaly",
            "summary_ru": "Данное дополнение отвечает за качественную и более правдивую картинку в режиме прицеливания в плане масштабирования и удобности использования.",
            "body_ru": "Автор: Шура\nКатегория: Оружейка \nВ этом дополнении вы получаете качественную картинку, но ценой этого является падение FPS в 2 раза во время использования режима прицеливания, так как игру рендерит дважды.У вас в игре теперь будет правильная кратность и соответствовать описанию.\nОсновано дополнение на 3DSS шейдерах, просто добавлен SVP(Second View Port) для улучшения качества и увеличения масштабирования, используя второй полноценный Рендер.\nУстановка довольно простая, можете перейти по кнопке Discord. Тут вы можете писать по этой теме, предлагать решения или же писать об ошибках",
            "title_en": "PiP(Picture in Picture) for 3DSS in Anomaly",
            "summary_en": "This add-on ensures a higher quality and more realistic image in the aiming mode in terms of scaling and ease of use.",
            "body_en": "Author: Shura\nCategory: Weapons\nThis add-on offers high-quality graphics, but at the cost of a 50% FPS drop, as the game is rendered twice. Your game will now have the correct frame rate and match the description.\nThe add-on is based on 3DSS shaders, but adds SVP (Second View Port) to improve quality and increase scaling using a second full renderer.",
            "url": "https://disk.yandex.ru/d/_dZrM6H53fZfRw",
            "discord_url": "https://discordapp.com/channels/1239150205153312769/1521641194164060321",
            "image_url": "library_dev_image_bed2cd8911.png"
        },
        {
            "title_ru": "Тестовый Движок",
            "summary_ru": "Данный движок является тестовым решением для повышения производительности и более быстрых загрузок",
            "body_ru": "Автор: Шура\nКатегория: Движок\n\nТЕСТИРУЕТСЯ ПОКА ЧТО ТОЛЬКО DX11AVX!!!!\n\nДанная тема открыта для тестирования нового движка под АНТОЛОГИЮ.\nВ данном движке решаются проблемы:\n \n1.Загрузки\nПользователи жалуются на долгие загрузки. Я решил отреагировать на это и использую лучшие решения из DEVELOPER ветки IX-Ray, некоторой концепции AOE, собирая на движке MT (от ThemrDemonized). Сейчас загрузки зафиксированы на разных системах и получается на 1/3 быстрее, стремление идёт к загрузкам ещё в два раза быстрее\n2. Производительность\nРанее была проблема с плохой производительностью, также предпринимаются меры по этому вопросу и сейчас производительность в среднем также выросла на несколько пунктов, также если у вас низкий ФПС сглаживание движка также должно компенсировать этот момент.\n3.\"Пропёрживание\"\nУ тестеров оно пропало и при загрузке сохранений этого эффекта нет, так как Многопоток стал более стабильным и только на слабых процессорах(примерно 6-8 летней давности) прослеживаются такие проблемы, но также менее характерно(проверено на себе).\n4. Совместим полностью с аддном на PiP прицелы, так как это тот же движок, на нём и ставлю эксперименты.\n5. Установка:\nПерейдите по ссылке и скачайте bin\nПерейдите по пути игры ANTHOLOGY\\Anomaly-1.5.3-Anthology 2.1 и переименуйте папку bin в bin1(а лучше запакуйте на всякий случай) перед установкой\nПоставьте новую разархивированную версию bin по этому же пути\nПочистите кэш шейдеров либо из appdata руками, либо через нажать играть в лаунчере.\nПРОТИВОПОКАЗАНО:\n1. НИ В КОЕМ СЛУЧАЕ НЕ ПОДКЛЮЧАЙТЕ КАК АДДОН В MO2!!!\n2. НИ В КОЕМ СЛУЧАЕ НЕ МЕНЯЙТЕ НА ДВИЖОК, КОТОРЫЙ МЫ ВАМ НЕ ДАЁМ, ЛУЧШЕ ОТКАТИТЕСЬ НА СТАРЫЙ, ИНАЧЕ ВСЯ СЕССИЯ И ДРУГИЕ ФИТЧИ ТУПО ИСЧЕЗНУТ, В ПРОТИВНОМ СЛУЧАЕ В ТЕХПОДДЕРЖКЕ ОТКАЗАНО\n3. НЕ ПОДКЛЮЧАТЬ МОДЫ, КОТОРЫЕ СВЯЗАНЫ С ДВИЖКОМ НЕ ИЗ НАШЕЙ БАЗЫ АДДНОВ БЕЗ НАШЕГО ВЕДОМА(ТОТ ЖЕ САМЫЙ A-LIFE PLUS), В ПРОТИВНОМ СЛУЧАЕ В ПОДДЕРЖКЕ ОТКАЗАНО",
            "title_en": "Test Engine",
            "summary_en": "This engine is a test solution for improving performance and faster loading.",
            "body_en": "Author: Shura\nCategory: Engine\n\nThis thread is open for testing a new engine for ANTHOLOGY.\nThis engine addresses the following issues:\n\nONLY DX11AVX IS BEING TESTED SO FAR!!!!\n1. Loading\nUsers are complaining about long loading times. I decided to respond to this and am using the best solutions from the IX-Ray DEVELOPER branch, some AOE concepts, and building on the MT engine (by ThemrDemonized). Currently, loading times are fixed on various systems and are 1/3 faster, with the goal of loading twice as fast.\n2. Performance\nPreviously, there was an issue with poor performance. We are also taking steps to address this, and now average performance has also improved by several points. If you have low FPS, the engine's anti-aliasing should also compensate. 3. \"Faring\"\nTesters have experienced this issue, and it doesn't occur when loading saves, as Multithreading has become more stable. Only weaker processors (approximately 6-8 years old) experience these issues, but they're also less common (I've tested this myself).\n4. Fully compatible with the PiP crosshair add-on, as it's the same engine, and that's what I'm experimenting with.\n5. Installation:\nFollow the link and download the bin file\nGo to the game path ANTHOLOGY\\Anomaly-1.5.3-Anthology 2.1 and rename the bin folder to bin1 (or better yet, zip it up just in case) before installing.\nInstall the new unzipped bin file in the same path.\nClear the shader cache either manually from appdata or by pressing play in the launcher. CONTRAINDICATED:\n1. DO NOT USE THIS AS AN ADD-ON IN MO2 UNDER ANY CIRCUMSTANCES!!!\n2. DO NOT UNDER ANY CIRCUMSTANCES SWITCH TO AN ENGINE THAT WE DO NOT PROVIDE YOU WITH. BETTER ROLLBACK TO THE OLD ONE. OTHERWISE, THE ENTIRE SESSION AND OTHER FEATURES WILL DISAPPEAR. OTHERWISE, TECHNICAL SUPPORT WILL BE DENIED.\n3. DO NOT USE MODS THAT RELATED TO AN ENGINE NOT IN OUR ADD-ON DATABASE WITHOUT OUR ADVICE (SUCH AS A-LIFE PLUS). OTHERWISE, SUPPORT WILL BE DENIED.",
            "url": "https://disk.yandex.ru/d/3SxJwT6ZR5mzmA",
            "discord_url": "https://discordapp.com/channels/1239150205153312769/1533636608761528540/1533636608761528540",
            "image_url": "library_dev_rak_weapon_pack_meme_clean_no_text_7453e41dee.png"
        }
    ],
    "modmakers": [
        {
            "title_ru": "Крутое дополнение от !ArtemkaKalinka, добавляет в ПДА несколько плейлистов по вайбу.",
            "summary_ru": "Добавлены новые треки и песни в ПДА для разнообразия, теперь вы можете расширить количество частот, на которых играют разные треки.",
            "body_ru": "Автор: !ArtemkaKalinka\n1. Вайбовые сталкерские песни д\nкоторые давно уже ставшие частью сталкерской культуры\n2.Пост панк\nЧто бы словить лютый депресняк\n3.Армейские песни\nЯ солдат и у меня нет башкии еее...\n4.Саундтрек из Метро 2033\n(скомуниздил из другого мода, не осуждайте :3)",
            "title_en": "A cool add-on from !ArtemkaKalinka, it adds several Vibe-based playlists to your PDA.",
            "summary_en": "New tracks and songs have been added to the PDA for variety, and you can now expand the number of frequencies on which different tracks play.",
            "body_en": "Author: !ArtemkaKalinka\n1. Vibey stalker songs\nthat have long since become part of stalker culture\n2. Post-punk\nTo get really depressed\n3. Army songs\nI'm a soldier and I have no brains...\n4. Soundtrack from Metro 2033\n(stole from another mod, don't judge :3)",
            "url": "https://drive.google.com/file/d/1YCyXmLV1O61ZMqrAbbI6G0E9ysZW7yYO/view?usp=sharing",
            "discord_url": "https://discordapp.com/channels/1239150205153312769/1526073185462653038/1526073185462653038",
            "image_url": ""
        },
        {
            "title_ru": "Мод - SquareDOV Minimap (16-10 Aspect Ratio Fix) — миникарта для мониторов 16-10",
            "summary_ru": "Данное дополнение является фиксом миникарты для мониторов 16x10",
            "body_ru": "Данный мод \"SquareDOV Minimap (16-10 Aspect Ratio Fix) — миникарта для мониторов 16-10\" имеет мной адаптированную версию оригинального мода squareDov - прямоугольной миникарты. Оригинальный мод не отображает корректно карту для мониторов формата 16:10.\nЯ это исправил. \n\nРаботает чисто как опциональный мод\n\nПриложил фото до и после.\n\nУстановка\n1.Скачать архив, распаковать.\n2.Поместить распакованную папку SquareDOV Minimap (16-10 Aspect Ratio Fix) — миникарта для мониторов 16-10 по пути: ANTHOLOGY\\SYS_A.N.T.H.O.L.O.G.Y_mo2_CBT\\mods\n3.Включить в МО2 внизу списка мод SquareDOV Minimap (16-10 Aspect Ratio Fix) — миникарта для мониторов 16-10\n\nПосле этого у вас заработает адаптированная карта и будет корректно отображаться.",
            "title_en": "Mod - SquareDOV Minimap (16-10 Aspect Ratio Fix) — minimap for 16-10 monitors",
            "summary_en": "This add-on is a minimap fix for 16x10 monitors.",
            "body_en": "This mod, \"SquareDOV Minimap (16:10 Aspect Ratio Fix) — minimap for 16:10 monitors,\" is an adapted version of the original squareDov mod, a rectangular minimap. The original mod doesn't display the map correctly on 16:10 monitors.\nI fixed this.\n\nIt works purely as an optional mod.\n\nI've attached before and after photos.\n\nInstallation\n1. Download the archive and unzip it.\n2. Place the unzipped SquareDOV Minimap (16-10 Aspect Ratio Fix) folder — a minimap for 16-10 monitors — in the following path: ANTHOLOGY\\SYS_A.N.T.H.O.L.O.G.Y_mo2_CBT\\mods\n3. Enable SquareDOV Minimap (16-10 Aspect Ratio Fix) — a minimap for 16-10 monitors at the bottom of the mod list in MO2.\n\nAfter this, the adapted map will work and display correctly.",
            "url": "https://disk.yandex.ru/d/eK6EF6bzmIwgqg",
            "discord_url": "https://discordapp.com/channels/1239150205153312769/1525963994417070231/1525966381034639452",
            "image_url": "library_dev_image_76ad689656.png"
        },
        {
            "title_ru": "Реальный КПК на вашем смартфоне",
            "summary_ru": "Данное дополнение подключает ваше мобильное устройство к мосту на ПК, который связан с игрой.\nАнглийской локализации пока ещё нет.Дополнение создано в рамках тестирования интересных механик с игрой за пределами устройств",
            "body_ru": "Автор: MUZZBAD\nКатегория: Интересные вещи\nЧто это такое?\nДанное дополнение подключает ваше мобильное устройство к мосту на ПК, который связан с игрой.\nВам больше не нужно открывать внутриигровой КПК. Используя смартфон или планшет, вы можете переключать задания, управлять радио и отслеживать метку игрока на карте с минимальной задержкой. Весь игровой интерфейс перенесён в приложение, даже вторая страница вкладок с полным функционалом.\nДля оптимизации в реальном времени синхронизируются только метка игрока, активные задания и сообщения. Все остальные вкладки обновляются с разной периодичностью, поэтому при первом запуске может понадобиться время для подгрузки.\nЕсли игра крашнулась или вы просто вышли из неё - коннект с мостом никуда не денется, а в интерфейсе приложения останется слепок последней сессии. При перезапуске игры всё восстановится.\n\nУправление картой:\nНажатие на задание - сделать его активным.\nУдержание метки - показать подробную информацию.\nДвойное нажатие на метку - открыть быстрое перемещение.\nДвойное нажатие на иконку игрока (внизу справа) - переключить карту в режим GPS. Повторное нажатие - вернуть обычный режим.\n\nЧтобы открыть настройки приложения, нажмите на часы в правом верхнем углу.\n\nОтображаемые на карте метки зависят от модели КПК, которую в данный момент использует ваш персонаж.\n\nУстановка:\n1. Распакуйте архив.\n2. Установите приложение на смартфон.\n3. Запустите MUZZPAD.exe.\n4. Выберите путь к модпаку, то есть основной каталог, внутри которого лежит папка mods. По умолчанию это c:\\Games\\ANTHOLOGY\\SYS_A.N.T.H.O.L.O.G.Y_mo2_CBT\\\nНажмите Установить.\n5. Откройте Mod Organaizer 2 и включите аддон, поставив на нём галочку.\nНажмите Запустить внутри моста.\n6. Приложение подключится к ПК автоматически.\n7. Программа должна быть запущена фоном во время игры.\n\nМост соединяется со смартфоном в домашней сети. То есть он будет работать, даже когда нет интернета на ПК, но главное, чтобы он был в одной домашней сети и доступен для подключения. Приложение, в свою очередь, не требует никаких разрешений к файлам/камере и прочему.\n\nВАЖНО: перед тестированием сделайте ручное сохранение на случай, если что-то пойдёт не так в игре с дополнением. Хотя и не должно, я уже пару недель уже тестирую в околофинальной версии. Но всякое бывает, это же сталкерочек.\n\nВсе ссылки на канал разработчика, разные источники скачивания вы можете увидеть в Discord публикации",
            "title_en": "A real PDA on your smartphone",
            "summary_en": "This add-on connects your mobile device to a PC bridge that is linked to the game.\nThere is no English localization yet. This add-on was created as part of testing interesting mechanics with off-device play.",
            "body_en": "Author: MUZZBAD\nCategory: Interesting Stuff\nWhat is it?\nThis add-on connects your mobile device to a PC bridge that's linked to the game.\nYou no longer need to open the in-game PDA. Using your smartphone or tablet, you can switch missions, control the radio, and track the player's blip on the map with minimal lag. The entire game interface has been transferred to the app, even the second page of tabs with full functionality.\nFor optimization, only the player's blip, active missions, and messages are synced in real time. All other tabs are updated at different intervals, so loading time may be required the first time you launch.\nIf the game crashes or you simply exit, the connection to the bridge will remain, and a snapshot of your last session will remain in the app interface. Restarting the game will restore everything.\n\nMap controls:\nTap a mission to make it active.\nHold the blip to display detailed information.\nDouble-tap the blip to open fast travel. Double-tap the player icon (bottom right) to switch the map to GPS mode. Tap again to return to normal mode.\n\nTo open the app settings, tap the clock in the upper right corner.\n\nThe markers displayed on the map depend on the PDA model your character is currently using.\n\nInstallation:\n1. Unzip the archive.\n2. Install the app on your smartphone.\n3. Run MUZZPAD.exe.\n4. Select the path to the modpack, i.e., the main directory containing the mods folder. By default, this is c:\\Games\\ANTHOLOGY\\SYS_A.N.T.H.O.L.O.G.Y_mo2_CBT\\\nClick Install.\n5. Open Mod Organizer 2 and enable the addon by checking its box.\nClick Run within Bridge.\n6. The app will connect to your PC automatically.\n7. The program must be running in the background during gameplay.\n\nThe bridge connects to the smartphone on your home network. This means it will work even if your PC doesn't have internet access, but the main thing is that it's on the same home network and available for connection. The app, in turn, doesn't require any file/camera permissions, etc.\n\nIMPORTANT: Before testing, make a manual save in case something goes wrong with the add-on. Although it shouldn't, I've been testing it for a couple of weeks in the near-final version. But anything can happen, it's S.T.A.L.K.E.R.\n\nAll links to the developer's channel and various download sources can be found in the Discord posts.",
            "url": "https://disk.yandex.ru/d/RHWEK_dmUxWhmg",
            "discord_url": "https://discordapp.com/channels/1239150205153312769/1527605145226186834/1527605145226186834",
            "image_url": "library_modmakers_real_pda_2_0dd781fc28.jpg"
        },
        {
            "title_ru": "Z.H.O.P.A. ALIFE 2.0",
            "summary_ru": "Z.H.O.P.A. делает жизнь Зоны более связной: отряды получают осмысленные задачи, преследуют движущиеся цели, собирают добычу и артефакты, торгуют, занимают базы и реагируют на сюжетные события.",
            "body_ru": "Автор: Костян Феникс\nКатегория: A-Life\nЧто меняется:\n1. Задачи отрядов\tСталкеры исследуют Зону, заселяют smart terrains, патрулируют, отдыхают, охотятся, мстят, ищут артефакты и ходят торговать. Мутанты используют отдельный набор задач.\n2. Охота и месть\tЦель отслеживается по фактическому положению отряда, включая переходы между локациями. Месть против игрока делает враждебным только назначенный отряд, а не всю группировку.\n3. Лутинг\tОнлайн-контур дополняет ванильный подбор и защищен от циклов на неподбираемых предметах. Оффлайн-добыча хранится как ограниченный виртуальный груз без расходования object IDs.\n4. Экономика.\tЛидер торгует за весь отряд, продает реальные и виртуальные товары, объединяет деньги участников и закупает базовые припасы.\nАртефакты\tПоддерживаются реальные и виртуальные оффлайн-артефакты, привязка к подходящим smart terrains и онлайн-подбор выбранным NPC с анимацией детектора.\n5. Базы и сервисы\tМод отслеживает владельцев баз и при необходимости восполняет подходящие сервисные роли после выброса.\nСюжетные события\tВ сюжетном режиме работают пси-зомбирование отрядов и северная миграция после отключения Выжигателя. Во фриплее эти системы не запускаются.\n\nТорговля и экономика\n\n1. Онлайн-сделка проходит через ванильную trade customer job и SISKI-derived axr_trade_manager.script.\nФактического продавца выбирает сама smart job через npc_info.job.seller_id; ZHOPA не подменяет его заранее выбранным NPC.\n2. Торговыми провайдерами считаются только торговцы и бармены. Медики, техники и лидеры группировок не используются как обычные продавцы.\n3. Поход к технику может запустить только настоящий предмет категории i_upgrade, находящийся у NPC. Обычная торговая закупка не создает искусственный tech intent.\n4. Оффлайн-сделки продают сериализуемый виртуальный груз и используют виртуальный баланс отряда.\n5. Множитель дохода NPC от продажи задается параметром npc_sell_price_multiplier; значение по умолчанию - 0.2.\n\nЛутинг\n\n1. Онлайн-лутинг использует ванильные схемы подбора там, где они способны завершить действие. ZHOPA добавляет целевой подбор, анти-stall и очистку памяти после отклоненного или завершенного лута, чтобы NPC не возвращались к одному трупу или предмету бесконечно.\n2. После оффлайн-боя добыча записывается в ограниченный виртуальный ledger. Она продается через экономику или материализуется только в контролируемом сценарии, например при смерти NPC в онлайне. Это предотвращает переполнение пула engine object IDs на длинных прохождениях.\n\nАртефакты\n\nРеальные артефакты регистрируются в runtime-индексе и принадлежат ровно одному подходящему smart bucket. Если доступен реальный артефакт, он имеет приоритет перед виртуальным. Виртуальные артефакты создаются по настройкам аномальных зон только для оффлайн-экономики и не уменьшают реальный спавн.\n\nУстановка\n\n1. Mod Organizer 2\n2. Отключите или удалите старые версии REZNYA, SISKI и ZHOPA.\n3. В MO2 выберите Файл -> Установить мод....\n4. Выберите архив Z.H.O.P.A. ALIFE 2.0 и подтвердите установку.\n5. Убедитесь, что мод расположен ниже конфликтующих модов, если нужно использовать поставляемый axr_trade_manager.script.\n\nВручную\n\nСкопируйте каталог gamedata в корень Anomaly и проверьте наличие файлов в gamedata/scripts, gamedata/configs, gamedata/configs/text и gamedata/textures.\n\nНастройки\n\nНастройки находятся в MCM -> ZHOPA ALIFE 2 и дублируются в gamedata/configs/zhopa2_settings.ltx.\n\nОсновные разделы MCM:\n\n1. Основные системы;\n2. Экономика и вспомогательные системы;\n3. Сюжетные события;\n4. Симуляция сталкеров и мутантов;\n5. Веса и длительность задач;\n6. Бой, маршруты и сопровождение целей;\n7. Отладка.\n\nОграничители NPC-лутинга, включая NPC Loot Claim, NPC Stop Looting Dead Bodies и опцию Useful Idiots «только компаньоны могут обыскивать тела», уменьшают объем работающей экономики. Вместо удаления ZHOPA можно отдельно отключить ее лутинг или экономику в MCM.\nxcvb's Guards Spawner не блокирует работу мода, но может писать в лог сообщения о своих отрядах после того, как их начинает обслуживать ZHOPA.\n\nБоевые AI-моды обычно безопаснее, пока не заменяют SIMBOARD, smart terrain и основные lifecycle callbacks.\n\nСохранения и удаление\n\nАвтоматической очистки сохранений от SISKI/ZHOPA1 и подготовки сейва к удалению ZHOPA2 нет. Такой механизм оказался небезопасным и был удален после случаев BusyHands/runtime corruption.\n\nДля удаления мода вернитесь к сохранению, сделанному до его установки. После сообщения BusyHands не продолжайте текущую сессию: перезагрузите сохранение или выйдите в главное меню.\n\nПроверка и отладка\nВключите debug_hud_enabled в MCM. На карте КПК появятся маркеры управляемых отрядов; подсказка показывает задачу, цель, smart, причину и последний результат. Этот режим предназначен для диагностики и может раскрывать скрытую работу симуляции.\n\nДополнительные диагностические скрипты находятся в debugscripts. Они не входят в обычную пользовательскую установку и запускаются только для проверки конкретных контуров.",
            "title_en": "Z.H.O.P.A. ALIFE 2.0",
            "summary_en": "Z.H.O.P.A. makes life in the Zone more connected: squads receive purposeful tasks, pursue moving targets, collect loot and artefacts, trade, occupy bases, and react to story events",
            "body_en": "Author: Kostyan Feniks\nCategory: A-Life\nWhat's changing:\n1. Squad Tasks: Stalkers explore the Zone, populate smart terrains, patrol, rest, hunt, seek revenge, search for artifacts, and trade. Mutants use a separate set of tasks.\n2. Hunting and Revenge: The target is tracked based on the squad's actual location, including transitions between locations. Revenge against a player makes only the assigned squad hostile, not the entire group.\n3. Looting: The online loop complements vanilla loot and is protected from loops on unlootable items. Offline loot is stored as limited virtual cargo without expending object IDs.\n4. Economy: The leader trades for the entire squad, sells real and virtual goods, pools members' money, and purchases basic supplies.\nArtifacts: Real and virtual offline artifacts are supported, along with linking to suitable smart terrains and online matching with selected NPCs with detector animation.\n5. Bases and Services: The mod tracks base owners and, if necessary, replenishes suitable service roles after an ejection.\nStory Events: In story mode, psi-zombification of squads and northern migration are enabled after the Scorcher is disabled. These systems do not run in freeplay.\n\nTrade and Economy\n\n1. Online transactions are processed through the vanilla trade customer job and the SISKI-derived axr_trade_manager.script.\nThe actual seller is selected by the smart job via npc_info.job.seller_id; ZHOPA does not replace it with a pre-selected NPC.\n2. Only merchants and bartenders are considered trade providers. Medics, technicians, and faction leaders are not used as regular sellers.\n3. Visiting a technician can only trigger a genuine i_upgrade item held by an NPC. A standard trade purchase does not create an artificial tech intent.\n4. Offline trades sell serializable virtual cargo and use the squad's virtual balance.\n5. The NPC's income multiplier from sales is set by the npc_sell_price_multiplier parameter; the default value is 0.2.\n\nLooting\n\n1. Online looting uses vanilla pickup schemes where they can complete the action. ZHOPA adds targeted pickup, anti-stall, and memory cleanup after rejected or completed loot to prevent NPCs from endlessly returning to the same corpse or item.\n2. After an offline battle, loot is recorded in a limited virtual ledger. It can be sold through the economy or materializes only in a controlled scenario, such as when an NPC dies online. This prevents the engine object ID pool from overflowing during long playthroughs.\n\nArtifacts\n\nReal artifacts are registered in the runtime index and belong to exactly one suitable smart bucket. If a real artifact is available, it takes precedence over virtual artifacts. Virtual artifacts are created based on anomalous zone settings for the offline economy only and do not reduce real spawn rates.\n\nInstallation\n\n1. Mod Organizer 2\n2. Disable or uninstall old versions of REZNYA, SISKI, and ZHOPA.\n3. In MO2, select File -> Install Mod...\n4. Select the Z.H.O.P.A. ALIFE 2.0 archive and confirm the installation.\n5. Ensure that the mod is located below any conflicting mods if you need to use the supplied axr_trade_manager.script.\n\nManually\n\nCopy the gamedata directory to the root of Anomaly and check for files in gamedata/scripts, gamedata/configs, gamedata/configs/text, and gamedata/textures.\n\nSettings\n\nThe settings are located in MCM -> ZHOPA ALIFE 2 and are duplicated in gamedata/configs/zhopa2_settings.ltx.\n\nMain MCM sections:\n\n1. Main Systems;\n2. Economy and Support Systems;\n3. Story Events;\n4. Stalker and Mutant Simulation;\n5. Task Weights and Duration;\n6. Combat, Routes, and Target Tracking;\n7. Debugging.\n\nNPC looting restrictions, including NPC Loot Claim, NPC Stop Looting Dead Bodies, and the Useful Idiots option \"only companions can loot bodies,\" reduce the size of the working economy. Instead of uninstalling ZHOPA, you can disable its loot or economy separately in the MCM.\nxcvb's Guards Spawner doesn't block the mod, but it can log messages about its squads after ZHOPA starts servicing them.\n\nCombat AI mods are generally safer until they replace SIMBOARD, smart terrain, and core lifecycle callbacks.\n\nSaves and Uninstallation\n\nThere is no automatic cleanup of SISKI/ZHOPA1 saves or preparation for ZHOPA2 uninstallation. This mechanism proved unsafe and was removed after instances of BusyHands/runtime corruption.\n\nTo uninstall the mod, revert to a save created before installing it. After the BusyHands message appears, do not continue the current session: reload the save or exit to the main menu.\n\nTesting and Debugging\nEnable debug_hud_enabled in the MCM. Markers for controlled squads will appear on the PDA map; The tooltip displays the task, target, smart, reason, and last result. This mode is intended for diagnostics and can reveal hidden simulation work.\n\nAdditional diagnostic scripts are located in debugscripts. They are not included in the standard user installation and are run only to check specific circuits.",
            "url": "https://github.com/qkff99/zhopa",
            "discord_url": "https://discordapp.com/channels/1239150205153312769/1528408274410672158/1528408274410672158",
            "image_url": "library_modmakers_5e2d59c7-8023-4a91-b41e-8ff122b97e6a_4eafb32d04.png"
        },
        {
            "title_ru": "GoTZ (Осенний набор текстур)",
            "summary_ru": "Новая версия GoTZ для ANTHOLOGY: изменены насыщенность травы и текстуры.",
            "body_ru": "Автор: YuriVernadsky\nКатегория: Текстуры \nНовая версия GoTZ для ANTHOLOGY: изменены насыщенность травы и текстуры.\nУСТАНОВКА\nПоместите файлы в папку mods игры ANTHOLOGY; это кумулятивный патч, поэтому просто скопируйте их с заменой.",
            "title_en": "GoTZ (Autumn Texture Pack)",
            "summary_en": "New GoTZ version for ANTHOLOGY: grass saturation and textures have been updated.",
            "body_en": "Author: YuriVernadsky\nCategory: Textures\nNew GoTZ version for ANTHOLOGY: grass saturation and textures have been updated.\nINSTALLATION\nPlace the files in the ANTHOLOGY mods folder; this is a cumulative patch, so simply copy and paste them.",
            "url": "https://drive.google.com/file/d/1AzhA8_18f5Aqb3apYup0dwiU2XRY_E4M/view",
            "discord_url": "https://discordapp.com/channels/1239150205153312769/1533573706025013490/1533573706025013490",
            "image_url": "library_modmakers_ss_yuri_08-02-26_01-18-57_puzir_7254b42b5e.jpg"
        },
        {
            "title_ru": "High Quality MAPS — 4K карты для КПК от 𝙍𝙚𝙑𝙤_𝙊𝖓𝖑𝟏𝖓𝖊",
            "summary_ru": "Графический аддон, направленный на полное переосмысление и повышение качества для ВСЕХ КАРТ ЛОКАЦИЙ которые видны в КПК и на мини-карте для сборки S.T.A.L.K.E.R. ANTHOLOGY.",
            "body_ru": "Автор: ReVo_Onl1ne\nКатегория: HUD\n\nОригинальные текстуры карт были обработаны с помощью нейросетей: разрешение увеличено в 4 раза (4x Upscale), устранено чрезмерное размытие и существенно повышена четкость мелких деталей (построек, аномалий, рельефа и объектов местности). Теперь изучать КПК и ориентироваться на местности с помощью мини-карты стало намного приятнее!\n\n✨ Основные особенности:\nHigh-Res детализация: Увеличение исходного разрешения текстур в 4 раза.\n\nИИ-улучшение: Сохранена оригинальная стилистика и цветокоррекция карт при максимальной прорисовке контуров и мелочей.\n\nПолная совместимость: Мод создавался специально под сборку ANTHOLOGY.\n\n🛠️ Инструкция по установке:\nСкачать и распаковать архив с модификацией.\nПеренести распакованную папку High Quality MAPS - ANTHOLOGY в папку mods вашего Mod Organizer 2 (MO2). Путь: \nANTHOLOGY\\SYS_A.N.T.H.O.L.O.G.Y_mo2_CBT\\mods\nЗапустить MO2, прокрутить список модов в самом левом окне в самый низ и поставить галочку напротив High Quality MAPS - ANTHOLOGY.\n\nЗапустить игру и наслаждаться чёткими картами! ☢️ \n\n💡 Рекомендуется ставить мод в самом низу списка загрузки (priority), чтобы его текстуры не перекрывались другими графическими аддонами(которые заменяют текстуры карт локаций).",
            "title_en": "High Quality MAPS — 4K maps for PDAs from 𝙍𝙚𝙑𝙤_𝙊𝖓𝖑𝟏𝖓𝖊",
            "summary_en": "A graphical addon designed to completely redesign and improve the quality of ALL location maps visible in the PDA and on the minimap for the S.T.A.L.K.E.R. ANTHOLOGY build.",
            "body_en": "Author: ReVo_Onl1ne\nCategory: HUD\nThe original map textures have been processed using neural networks: the resolution has been increased by 4x (4x Upscale), excessive blurring has been eliminated, and the clarity of small details (buildings, anomalies, terrain, and terrain features) has been significantly improved. Now exploring the PDA and navigating the terrain using the minimap has become much more enjoyable!\n\n✨ Key Features:\nHigh-Res Detail: The original texture resolution has been increased by 4x.\n\nAI Enhancement: The original style and color correction of the maps have been preserved while maximizing the rendering of contours and details.\n\nFull Compatibility: The mod was created specifically for the ANTHOLOGY build.\n\n🛠️ Installation Instructions:\nDownload and unzip the archive containing the modification.\nMove the unzipped High Quality MAPS - ANTHOLOGY folder to the mods folder in your Mod Organizer 2 (MO2). Path:\nANTHOLOGY\\SYS_A.N.T.H.O.L.O.G.Y_mo2_CBT\\mods\nLaunch MO2, scroll to the bottom of the mod list in the leftmost window, and check the box next to High Quality MAPS - ANTHOLOGY.\n\nLaunch the game and enjoy crisp maps! ☢️\n\n💡 It is recommended to install the mod at the very bottom of the load order (priority) so that its textures are not overlapped by other graphic add-ons (which replace location map textures).",
            "url": "https://www.moddb.com/mods/high-quality-maps-stalker-anthology/downloads/high-quality-maps-stalker-anthology",
            "discord_url": "https://discordapp.com/channels/1239150205153312769/1525963994417070231/1529232779777146912",
            "image_url": "library_modmakers_1___2_c3b30dc43b.png"
        }
    ],
    "solutions": [
        {
            "title_ru": "Адаптация под 21:9 (Hard Режим)",
            "summary_ru": "Адаптирован интерфейс под 21:9",
            "body_ru": "Автор: Melisov(Ден)\nКатегория: UI \nАдаптировал интерфейс под 21:9. Установить через МО2, расположить в самом низу списка, поставите галочку.",
            "title_en": "Adaptation for 21:9 (Hard Mode)",
            "summary_en": "The interface has been adapted for 21:9",
            "body_en": "Author: Melisov(Den)\nCategory: UI\nAdapted the interface for 21:9. Install via MO2, place it at the very bottom of the list, and check the box.",
            "url": "https://disk.yandex.ru/d/k3Bp-DugUIkSjg",
            "discord_url": "https://discordapp.com/channels/1239150205153312769/1511400517526356111/1511400517526356111",
            "image_url": "library_solutions_Anomaly-1_5_3-anthology_2_1_Screenshot_2026_06_02_-_18_38_06_55_2323826511.png"
        },
        {
            "title_ru": "Неограниченный вес тайников для модпака",
            "summary_ru": "Установка неограниченного веса у тайников",
            "body_ru": "Автор: LaFa\nКатегория: Геймплей\nЕсли вам надоело ставить 1000 ящиков на своей базе и вы в своём роде минималист, то рекомендую установить эту правку. Данная правка убирает ограничение веса у любых ящиков, и теперь можно поставить один и складывать в него 10 тонн хлама.\n\nУстановка:\n\nСкачайте архив.\nАктивируйте его и оставьте в конце списка Mod Organizer.\nГотово.",
            "title_en": "Unlimited stash weight for modpack",
            "summary_en": "Setting unlimited weight for caches",
            "body_en": "Author: LaFa\nCategory: Gameplay\nIf you're tired of placing 1,000 crates in your base and you're something of a minimalist, I recommend installing this tweak. This tweak removes the weight limit for all crates, allowing you to place one and store 10 tons of junk in it.",
            "url": "https://disk.yandex.ru/d/8mPP3p-t_C4dag",
            "discord_url": "https://discordapp.com/channels/1239150205153312769/1512794535489568889/1512794535489568889",
            "image_url": "library_solutions_ss_nikit_06-06-26_15-02-00_l01_escape_b067c0645d.jpg"
        },
        {
            "title_ru": "Видимые кровососы",
            "summary_ru": "Невидимые кровососы становятся видимыми",
            "body_ru": "Надоели стаи кровососов которые долбят везде и всегда тогда тебе сюда. Возвращает прозрачность кровососов как в ТЧ\n\nУстановка:\n\nСкачайте архив.\nАктивируйте его и оставьте в конце списка Mod Organizer.\nГотово.",
            "title_en": "Visible bloodsuckers",
            "summary_en": "Invisible bloodsuckers become visible",
            "body_en": "Tired of swarms of bloodsuckers harassing you everywhere, then this is the place for you. Brings back the bloodsuckers' transparency like in TC.\n\nInstallation:\n\nDownload the archive.\nActivate it and leave it at the bottom of the Mod Organizer list.\nDone.",
            "url": "https://disk.yandex.ru/d/RUwyjmyYd1J0_Q",
            "discord_url": "https://discordapp.com/channels/1239150205153312769/1518979301163401227/1518979301163401227",
            "image_url": "library_solutions_4tmxiuiu567b1_22b26eb68c.jpg"
        }
    ]
}


INFO_LINKS = {
    "requirements": {
        "title_ru": "Системные требования",
        "body_ru": "Тут вопрос довольно дискуссионный, всё зависит от версии в которую вы играете: \n\nОригинал: \nДолжен запускаться на любой 64x битной системе от DX8(кроме DX10, он по умолчанию не работает)\n\nОригинал+Модпак:\nМинимальные: \n- Видеокарты GTX 1660 6 ГБ / RX 580 8 ГБ (Зависит от разрешения монитора)\n- Процессор 4х ядерный любой\n- Оперативная память 16 ГБ + Файл подкачки 40-50 ГБ\n- Твердотельный накопитель(SSD) для быстрой загрузки\n\nРекомендуемые требования:\n- Видеокарты RTX 3060 8 ГБ / RX 6600XT 8 ГБ (Зависит от разрешения монитора)\n- Современный 6-ядерный процессор\n- Оперативная память 32 ГБ (Файл подкачки рекомендуется, но необязателен)\n- SSD M2 для быстрой загрузки\n\nТочные требования зависят от настроек графики, выбранного рендера и установленных дополнительных проектов.В основном всё тестируется как 1920x1080 и 2560x1440 разрешениях",
        "title_en": "System requirements",
        "body_en": "This question is quite debatable, it all depends on the version you're playing:\n\nOriginal:\nShould run on any 64-bit system from DX8 (except DX10, which doesn't work by default)\n\nOriginal + Modpack:\nMinimum:\n- RTX 1660 6GB / RX 580 8GB graphics card (Depends on monitor resolution)\n- Any quad-core processor\n- 16GB RAM + 40-50GB paging file\n- Solid-state drive (SSD) for fast loading\n\nRecommended:\n- RTX 3060 8GB / RX 6600XT 8GB graphics card (Depends on monitor resolution)\n- Modern 6-core processor\n- 32GB RAM (Paging file recommended, but not required)\n- M2 SSD for fast loading Downloads\n\nThe exact requirements depend on the graphics settings, the selected renderer, and the additional projects installed. Generally, everything is tested at both 1920x1080 and 2560x1440 resolutions."
    },
    "original": {
        "title_ru": "Информация об Оригинале",
        "body_ru": "Оригинальная версия подразумевает основу S.T.A.L.K.E.R Anomaly 1.5.3 с сюжетами от Главного разработчика Максима Ратного под названием A.N.T.H.O.L.O.G.Y\n\nВы можете проходить несколько сюжетных линий как оригинальных: \n1. Тень Чернобыля\n2. Зов Припяти\n3. Чистое небо(в активной разработке)\nТакже сюда относятся сюжетные линии из модификаций: \n1. Путь во Мгле\n2. Пространственная Аномалия\n3. Забытый отряд\n4. Смерти вопреки.В паутине лжи\n5. Долина Шорохов\n6. Атрибут\nНу и стандартные Фриплейные сюжеты от Anomaly(CoC): Легенда Зоны, Смертный Грех и Послесвечение",
        "title_en": "Original information",
        "body_en": "The original version is based on S.T.A.L.K.E.R. Anomaly 1.5.3 with stories from Lead Developer Maxim Ratny called A.N.T.H.O.L.O.G.Y.\n\nYou can play through several storylines as originals:\n1. Shadow of Chernobyl\n2. Call of Pripyat\n3. Clear Sky (in active development)\nThis also includes storylines from mods:\n1. Path in the Fog\n2. Spatial Anomaly\n3. Forgotten Squad\n4. Defying Death. In the Web of Lies\n5. Valley of Whispers\n6. Attributes\nAnd the standard Freeplay stories from Anomaly (CoC): Legend of the Zone, Mortal Sin, and Afterglow"
    },
    "modpack": {
        "title_ru": "Информация о Модпаке",
        "body_ru": "ПОДДЕРЖИВАЕТСЯ ТОЛЬКО DX11!!!!\n\nМодпак для A.N.T.H.O.L.O.G.Y является ОПЦИОНАЛЬНЫМ ДОПОЛНЕНИЕМ и НИКАК не влияет на сюжет в прямом смысле.\n\nЭто адаптация МОДОВ для ОРИГИНАЛА.Идут они конечно не параллельно, идёт адаптация под оригинал.\n\nДанный модпак имеет 2 профиля Standart и Hard, которые меняются через MO2(Mod Organiser 2)\nПрофиль Standart подразумевает довольно классическое прохождение, имея обычные механики Anomaly + некоторые интересные фитчи для обычного игрока в экосистеме сборок S.T.A.L.K.E.R Anomaly\nПрофиль Hard подразумевает довольно сложное прохождение(особенно сначала) и подходит далеко не для всех, потому что многие механики являются спорными и разработчики пытаются сделать определённый баланс, который предоставляет горение жопы через адамантиевый стул.\n\nОружейный пак R.A.K со своими модулями является ПОЛНОСТЬЮ модульным.Вы можете создать новый профиль MO2 и спокойно его включить для оригинала(да даже в ванильную Anomaly стырить) и играть спокойно.Так делают те, у кого проблемы с производительностью, но хотят новое оружие.",
        "title_en": "Modpack information",
        "body_en": "ONLY DX11 IS SUPPORTED!!!!\n\nThis modpack for A.N.T.H.O.L.O.G.Y. is an OPTIONAL ADD-ON and does NOT directly affect the storyline.\n\nThis is an adaptation of the original mods. They don't run in parallel, of course; they are adapted to the original.\n\nThis modpack has two profiles: Standard and Hard, which can be changed via MO2 (Mod Organizer 2).\nThe Standard profile offers a fairly classic playthrough, featuring the usual Anomaly mechanics and some interesting features for the average player in the S.T.A.L.K.E.R. Anomaly ecosystem.\nThe Hard profile offers a rather challenging playthrough (especially at first) and is not suitable for everyone, as many mechanics are controversial, and the developers are trying to achieve a certain balance that ensures you'll burn your ass through an adamantium chair.\n\nThe R.A.K. weapon pack, with its modules, is FULLY modular. You can create a new MO2 profile and easily enable it for the original (or even steal it from vanilla Anomaly) and play without any problems. This is what those who have performance issues but want new weapons do."
    }
}


def app_dir():
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent.parent


def asset_dir():
    if getattr(sys, "frozen", False):
        return Path(getattr(sys, "_MEIPASS")) / "assets"
    return Path(__file__).resolve().parent / "assets"


class LauncherApp(tk.Tk):
    def __init__(self):
        super().__init__(className="ANTHOLOGYLauncher")
        self.title("ANTHOLOGY Launcher")
        self.root_dir = app_dir()
        self.assets = asset_dir()
        self.lang = "ru"
        self.renderer = "DX11"
        self.shadow = 0
        self.debug = True
        self.sound_fix = False
        self.prefetch = False
        self.chat_relay_always = True
        self.reset_user = False
        self.avx = False
        self.drag_x = 0
        self.drag_y = 0
        self.drag_window_x = 0
        self.drag_window_y = 0
        self.view = "home"
        self.updating = False
        self.update_probe_running = False
        self.engine_update_manifest = None
        self.worker_threads = []
        self.update_status_item = None
        self.update_progress_bg = None
        self.update_progress_fill = None
        self.update_progress_text = None
        self.engine_status_item = None
        self.engine_progress_bg = None
        self.engine_progress_fill = None
        self.engine_progress_text = None
        self.items = []
        self.library_images = []
        self.view_widgets = []
        self.buttons = {}
        self.render_buttons = {}
        self.toggle_items = {}
        self.ui_scale = self._calculate_ui_scale()
        self.window_width = max(1, int(WIDTH * self.ui_scale))
        self.window_height = max(1, int(HEIGHT * self.ui_scale))
        self._apply_tk_scale()

        self.overrideredirect(True)
        self._register_windows_app_identity()
        self._center_window()
        self.resizable(False, False)
        self.configure(bg=COLORS["bg"])

        icon = self.assets / "launcher_radioactive_icon_round.ico"
        if icon.exists():
            self.iconbitmap(str(icon))

        self._load_config()
        self._load_background()
        self._build_base()
        self.show_home()
        self.after(300, self._show_on_taskbar)
        self.after(800, self.ensure_desktop_shortcut)
        self.after(1500, self.check_launcher_update_async)
        self.after(1800, self.check_content_updates_async)

    def _load_background(self):
        bg = Image.open(self.assets / "Launcher.png").resize((self.window_width, self.window_height), Image.Resampling.LANCZOS)
        bg = ImageEnhance.Brightness(bg).enhance(0.72)
        bg = ImageEnhance.Contrast(bg).enhance(0.96)
        self.bg_img = ImageTk.PhotoImage(bg)

    def _calculate_ui_scale(self):
        self.update_idletasks()
        screen_w = max(1, self.winfo_screenwidth())
        screen_h = max(1, self.winfo_screenheight())
        available_w = max(1, screen_w - SCREEN_PADDING)
        available_h = max(1, screen_h - SCREEN_PADDING)
        scale = min(1.0, available_w / WIDTH, available_h / HEIGHT)
        return max(MIN_UI_SCALE, scale)

    def _apply_tk_scale(self):
        if self.ui_scale >= 0.999:
            return
        try:
            current = float(self.tk.call("tk", "scaling"))
            self.tk.call("tk", "scaling", current * self.ui_scale)
        except Exception:
            pass

    def _scale_item(self, item):
        if self.ui_scale >= 0.999:
            return item
        self.canvas.scale(item, 0, 0, self.ui_scale, self.ui_scale)
        for option in ("width", "height"):
            try:
                value = self.canvas.itemcget(item, option)
                if value and float(value) > 0:
                    self.canvas.itemconfig(item, **{option: max(1, int(float(value) * self.ui_scale))})
            except Exception:
                pass
        return item

    def _scale_canvas(self):
        if self.ui_scale >= 0.999:
            return
        for item in self.canvas.find_all():
            self._scale_item(item)

    def _sx(self, value):
        return value * self.ui_scale

    def _sy(self, value):
        return value * self.ui_scale

    def _register_windows_app_identity(self):
        if sys.platform != "win32":
            return
        try:
            import ctypes
            ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID("SYS.Anthology.Launcher")
        except Exception:
            pass

    def _show_on_taskbar(self):
        if sys.platform != "win32":
            return
        try:
            import ctypes
            user32 = ctypes.windll.user32
            hwnd = self.winfo_id()
            parent = user32.GetParent(hwnd)
            if parent:
                hwnd = parent
            user32.SetWindowTextW(hwnd, "ANTHOLOGY Launcher")
            gwl_exstyle = -20
            ws_ex_appwindow = 0x00040000
            ws_ex_toolwindow = 0x00000080
            style = user32.GetWindowLongW(hwnd, gwl_exstyle)
            style = (style | ws_ex_appwindow) & ~ws_ex_toolwindow
            user32.SetWindowLongW(hwnd, gwl_exstyle, style)
            self.withdraw()
            self.after(10, self.deiconify)
        except Exception as exc:
            self._debug_log(f"taskbar icon failed: {type(exc).__name__}: {exc}")

    def _build_base(self):
        self.canvas = tk.Canvas(self, width=self.window_width, height=self.window_height, highlightthickness=0, bd=0, bg=COLORS["bg"])
        self.canvas.pack(fill="both", expand=True)
        self.canvas.create_image(0, 0, image=self.bg_img, anchor="nw")
        self.canvas.create_rectangle(0, 0, WIDTH, HEIGHT, fill="#020504", stipple="gray50", outline="")
        self.canvas.create_rectangle(0, 0, WIDTH, TOP_BAR, fill=COLORS["glass"], stipple="gray50", outline="")
        self.canvas.create_line(67, TOP_BAR - 1, 1103, TOP_BAR - 1, fill="#9ee9dc")
        self.canvas.create_text(68, 55, text="ANTHOLOGY", anchor="w", fill=COLORS["text"], font=("Segoe UI Semibold", 14, "bold"))
        self.canvas.create_text(195, 56, text="LAUNCHER", anchor="w", fill=COLORS["muted"], font=("Segoe UI", 9))
        self.settings_hit = self.canvas.create_rectangle(WIDTH - 128, 38, WIDTH - 96, 72, fill=COLORS["glass"], stipple="gray50", outline="")
        self.min_hit = self.canvas.create_rectangle(WIDTH - 92, 38, WIDTH - 60, 72, fill=COLORS["glass"], stipple="gray50", outline="")
        self.close_hit = self.canvas.create_rectangle(WIDTH - 56, 38, WIDTH - 22, 72, fill=COLORS["glass"], stipple="gray50", outline="")
        self.settings_btn = self.canvas.create_text(WIDTH - 112, 55, text="⚙", fill=COLORS["muted"], font=("Segoe UI Symbol", 13, "bold"))
        self.min_btn = self.canvas.create_text(WIDTH - 74, 55, text="-", fill=COLORS["muted"], font=("Segoe UI", 18))
        self.close_btn = self.canvas.create_text(WIDTH - 38, 55, text="x", fill=COLORS["muted"], font=("Segoe UI", 13, "bold"))
        for item in (self.settings_hit, self.settings_btn):
            self.canvas.tag_bind(item, "<Button-1>", lambda _e: self.show_settings())
            self.canvas.tag_bind(item, "<Enter>", lambda _e, r=self.settings_hit: self.canvas.itemconfig(r, fill=COLORS["glass_lift"]))
            self.canvas.tag_bind(item, "<Leave>", lambda _e, r=self.settings_hit: self.canvas.itemconfig(r, fill=COLORS["glass"]))
        for item in (self.min_hit, self.min_btn):
            self.canvas.tag_bind(item, "<Button-1>", self._minimize_window)
            self.canvas.tag_bind(item, "<Enter>", lambda _e, r=self.min_hit: self.canvas.itemconfig(r, fill=COLORS["glass_lift"]))
            self.canvas.tag_bind(item, "<Leave>", lambda _e, r=self.min_hit: self.canvas.itemconfig(r, fill=COLORS["glass"]))
        for item in (self.close_hit, self.close_btn):
            self.canvas.tag_bind(item, "<Button-1>", self._close_window)
            self.canvas.tag_bind(item, "<Enter>", lambda _e, r=self.close_hit: self.canvas.itemconfig(r, fill=COLORS["glass_lift"]))
            self.canvas.tag_bind(item, "<Leave>", lambda _e, r=self.close_hit: self.canvas.itemconfig(r, fill=COLORS["glass"]))
        self.canvas.tag_bind("all", "<ButtonPress-1>", self._start_drag)
        self.canvas.tag_bind("all", "<B1-Motion>", self._drag)
        self.canvas.tag_bind("all", "<ButtonRelease-1>", self._stop_drag)

        self.flag_ru = ImageTk.PhotoImage(Image.open(self.assets / "flag_ru.png").resize((30, 21), Image.Resampling.LANCZOS))
        self.flag_us = ImageTk.PhotoImage(Image.open(self.assets / "flag_us.png").resize((30, 21), Image.Resampling.LANCZOS))
        self._scale_canvas()

    def _clear_view(self):
        for widget in self.view_widgets:
            widget.destroy()
        self.view_widgets = []
        for item in self.items:
            self.canvas.delete(item)
        self.items = []
        self.library_images = []
        self.buttons = {}
        self.render_buttons = {}
        self.toggle_items = {}

    def _add(self, item):
        self._scale_item(item)
        self.items.append(item)
        return item

    def _add_widget(self, widget, x, y, w, h):
        self.view_widgets.append(widget)
        return self._add(self.canvas.create_window(x, y, width=w, height=h, anchor="nw", window=widget))

    def show_home(self):
        self.view = "home"
        self._clear_view()
        t = TEXT[self.lang]

        self.buttons["youtube"] = self._button(710, 118, 118, 38, "YouTube", lambda: webbrowser.open("https://www.youtube.com/@Samael-w3p"))
        self.buttons["vk"] = self._button(836, 118, 118, 38, "VK", lambda: webbrowser.open("https://vk.com/club219667646"))
        self.buttons["discord"] = self._button(961, 118, 118, 38, "Discord", lambda: webbrowser.open("https://discord.gg/pZYeVxEwGc"))
        self.buttons["support"] = self._button(713, 174, 365, 38, t["support"], self.show_support)
        self.buttons["relay_chat"] = self._button(713, 222, 118, 38, t["relay_chat"], self.open_relay_chat)
        self.buttons["projects"] = self._button(713, 270, 365, 40, t["projects"].upper(), self.show_projects, font_size=11)
        self.buttons["info"] = self._button(713, 320, 176, 38, t["info"].upper(), self.show_info, font_size=11)

        self._section_label(108, 206, t["news"])
        self._news_feed(108, 254, 540, 296, t)

        self._add(self.canvas.create_line(67, 573, 1103, 573, fill=COLORS["accent"], stipple="gray50", width=2))
        self.buttons["logs"] = self._button(904, 604, 176, 38, t["logs"], self.open_logs_folder)
        self.buttons["update"] = self._button(904, 654, 176, 38, t["update_button"], self.sync_modpack_update)
        self._bottom_update_bar(t)
        self.flag_id = self._add(self.canvas.create_image(668, 126, anchor="nw", image=self.flag_us if self.lang == "ru" else self.flag_ru))
        self.canvas.tag_bind(self.flag_id, "<Button-1>", lambda _e: self.toggle_language())

    def show_settings(self):
        self.view = "settings"
        self._clear_view()
        t = TEXT[self.lang]

        self._panel(56, 92, 1056, 536, alpha="surface")
        self._section_label(96, 132, t["settings"])
        self.buttons["back"] = self._button(973, 113, 112, 37, t["back"], self.show_home)

        self._add(self.canvas.create_text(96, 188, text=t["renderer"], anchor="w", fill=COLORS["muted"], font=("Segoe UI", 10)))
        x = 96
        for renderer in RENDERERS:
            self.render_buttons[renderer] = self._button(x, 220, 188, 43, RENDER_LABELS[renderer], lambda r=renderer: self._set_renderer(r))
            x += 206

        self.toggle_items = {
            "debug": self._toggle(96, 330, t["debug"], lambda: self._flip("debug")),
            "sound_fix": self._toggle(96, 384, t["sound_fix"], lambda: self._flip("sound_fix")),
            "prefetch": self._toggle(96, 438, t["prefetch"], lambda: self._flip("prefetch")),
            "chat_relay_always": self._toggle(96, 492, t["chat_relay_always"], lambda: self._flip("chat_relay_always")),
            "reset": self._toggle(455, 330, t["reset"], lambda: self._flip("reset_user")),
            "avx": self._toggle(455, 384, t["avx"], lambda: self._flip("avx")),
        }

        self._add(self.canvas.create_text(805, 334, text=t["shadow"], anchor="w", fill=COLORS["muted"], font=("Segoe UI", 10)))
        self.shadow_value = self._add(self.canvas.create_text(919, 334, text=str(SHADOWS[self.shadow]), anchor="w", fill=COLORS["text"], font=("Segoe UI Semibold", 17, "bold")))
        self.buttons["shadow_minus"] = self._button(805, 357, 78, 25, "<", self._shadow_prev)
        self.buttons["shadow_plus"] = self._button(896, 357, 78, 25, ">", self._shadow_next)
        self.buttons["save"] = self._button(96, 556, 194, 48, t["save"], self.save_settings, primary=True)
        self.buttons["about"] = self._button(805, 556, 130, 42, t["about"], self.about)
        self.buttons["engine"] = self._button(956, 556, 130, 42, t["engine_button"], self.sync_engine_update)
        self.engine_status_item = self._add(self.canvas.create_text(
            805,
            504,
            text=self._engine_status_text(),
            anchor="w",
            fill=COLORS["muted"],
            font=("Segoe UI", 9),
            width=280,
        ))
        self.engine_progress_bg = self._add(self.canvas.create_rectangle(805, 535, 1085, 544, fill="#091211", outline="#476760"))
        self.engine_progress_fill = self._add(self.canvas.create_rectangle(805, 535, 805, 544, fill=COLORS["accent"], outline=""))
        self.engine_progress_text = self._add(self.canvas.create_text(945, 548, text="", anchor="center", fill=COLORS["muted"], font=("Segoe UI", 7)))
        self._set_engine_progress(0, "")

        self._refresh_all()

    def show_support(self):
        self.view = "support"
        self._clear_view()
        t = TEXT[self.lang]

        self._panel(64, 92, 1052, 536, alpha="surface")
        self._section_label(104, 134, t["support"])
        self.buttons["back"] = self._button(966, 116, 110, 36, t["back"], self.show_home)

        self._add(self.canvas.create_text(
            104,
            180,
            text="Нажмите на ссылку, чтобы открыть ее в браузере.",
            anchor="w",
            fill=COLORS["muted"],
            font=("Segoe UI", 10),
        ))

        frame = tk.Frame(self.canvas, bg="#5f827a", padx=1, pady=1)
        body = tk.Text(
            frame,
            bg="#08110f",
            fg=COLORS["text"],
            insertbackground=COLORS["text"],
            selectbackground="#24534c",
            bd=0,
            highlightthickness=0,
            padx=22,
            pady=18,
            wrap="word",
            font=("Segoe UI", 10),
            cursor="arrow",
        )
        body.pack(side="left", fill="both", expand=True)
        body.bind("<MouseWheel>", lambda event: body.yview_scroll(int(-1 * (event.delta / 120)), "units"))
        self._fill_donation_body(body)
        body.configure(state="disabled")
        self._add_widget(frame, 104, 214, 972, 366)

    def show_projects(self):
        self.view = "projects"
        self._clear_view()
        t = TEXT[self.lang]

        self._panel(64, 92, 1052, 536, alpha="surface")
        self._section_label(104, 134, t["projects_title"])
        self.buttons["back"] = self._button(966, 116, 110, 36, t["back"], self.show_home)

        self._add(self.canvas.create_text(
            104,
            184,
            text=t["projects_intro"],
            anchor="w",
            fill=COLORS["muted"],
            font=("Segoe UI", 10),
            width=760,
        ))

        cards = [
            ("dev", t["projects_dev_title"], t["projects_dev_desc"]),
            ("modmakers", t["projects_modmakers_title"], t["projects_modmakers_desc"]),
            ("solutions", t["projects_solutions_title"], t["projects_solutions_desc"]),
        ]
        x = 104
        for key, title, subtitle in cards:
            self._project_card(x, 248, 286, 172, title, subtitle, lambda k=key: self.show_project_category(k))
            x += 318

    def show_info(self):
        self.view = "info"
        self._clear_view()
        t = TEXT[self.lang]

        self._panel(64, 92, 1052, 536, alpha="surface")
        self._section_label(104, 134, t["info_title"])
        self.buttons["back"] = self._button(966, 116, 110, 36, t["back"], self.show_home)

        self._add(self.canvas.create_text(
            104,
            184,
            text=t["info_intro"],
            anchor="w",
            fill=COLORS["muted"],
            font=("Segoe UI", 10),
            width=760,
        ))

        cards = [
            ("requirements", t["info_requirements_title"], t["info_requirements_desc"]),
            ("original", t["info_original_title"], t["info_original_desc"]),
            ("modpack", t["info_modpack_title"], t["info_modpack_desc"]),
        ]
        x = 104
        for key, title, subtitle in cards:
            self._project_card(x, 248, 286, 172, title, subtitle, lambda k=key: self.show_info_section(k))
            x += 318

    def show_info_section(self, section):
        self.view = "info_section"
        self.current_info_section = section
        self._clear_view()
        t = TEXT[self.lang]
        entry = INFO_LINKS.get(section, INFO_LINKS["requirements"])
        title = str(entry.get(f"title_{self.lang}") or entry.get("title_ru") or "").strip()
        body = str(entry.get(f"body_{self.lang}") or entry.get("body_ru") or "").strip()

        self._panel(64, 92, 1052, 536, alpha="surface")
        self._section_label(104, 134, title)
        self.buttons["back"] = self._button(966, 116, 110, 36, t["back"], self.show_info)

        frame = tk.Frame(self.canvas, bg="#5f827a", padx=1, pady=1)
        text = tk.Text(
            frame,
            bg="#08110f",
            fg=COLORS["text"],
            insertbackground=COLORS["text"],
            selectbackground="#24534c",
            bd=0,
            highlightthickness=0,
            padx=22,
            pady=18,
            wrap="word",
            font=("Segoe UI", 11),
            cursor="arrow",
        )
        text.pack(side="left", fill="both", expand=True)
        scroll = tk.Scrollbar(frame, orient="vertical", command=text.yview)
        scroll.pack(side="right", fill="y")
        text.configure(yscrollcommand=scroll.set)
        text.insert("1.0", body)
        text.configure(state="disabled")
        self._add_widget(frame, 104, 184, 972, 410)

    def show_project_category(self, category):
        self.view = "project_category"
        self.current_project_category = category
        self._clear_view()
        t = TEXT[self.lang]

        category_map = {
            "dev": ("projects_dev_title", "projects_dev_desc"),
            "modmakers": ("projects_modmakers_title", "projects_modmakers_desc"),
            "solutions": ("projects_solutions_title", "projects_solutions_desc"),
        }
        title_key, desc_key = category_map.get(category, category_map["dev"])

        self._panel(64, 92, 1052, 536, alpha="surface")
        self._section_label(104, 134, t[title_key])
        self.buttons["back"] = self._button(966, 116, 110, 36, t["back"], self.show_projects)

        self._add(self.canvas.create_text(
            104,
            184,
            text=t[desc_key],
            anchor="nw",
            fill=COLORS["text"],
            font=("Segoe UI", 11),
            width=820,
        ))
        entries = LIBRARY_LINKS.get(category, [])
        if not entries:
            self._add(self.canvas.create_rectangle(104, 262, 1076, 440, fill=COLORS["glass_lift"], stipple="gray50", outline=COLORS["accent"], width=1))
            self._add(self.canvas.create_line(105, 263, 1075, 263, fill="#ffffff", stipple="gray50", width=1))
            self._add(self.canvas.create_text(
                128,
                292,
                text=t["projects_empty"],
                anchor="nw",
                fill=COLORS["muted"],
                font=("Segoe UI", 10),
                width=900,
            ))
            return

        list_frame = tk.Frame(self.canvas, bg="#5f827a", padx=1, pady=1)
        list_canvas = tk.Canvas(
            list_frame,
            bg="#06100e",
            highlightthickness=0,
            bd=0,
            yscrollincrement=18,
        )
        list_canvas.pack(side="left", fill="both", expand=True)
        scroll_bar = tk.Canvas(list_frame, width=12, bg="#06100e", highlightthickness=0, bd=0)
        scroll_bar.pack(side="right", fill="y")

        content_height = max(376, len(entries) * 130)
        list_canvas.configure(scrollregion=(0, 0, 952, content_height))

        def _redraw_scrollbar(*_args):
            scroll_bar.delete("all")
            view_top, view_bottom = list_canvas.yview()
            bar_height = 376
            scroll_bar.create_rectangle(0, 0, 12, bar_height, fill="#07100e", outline=COLORS["accent"], width=1)
            scroll_bar.create_line(1, 1, 1, bar_height - 1, fill="#ffffff", stipple="gray75")
            thumb_top = max(3, int(view_top * bar_height))
            thumb_bottom = min(bar_height - 3, int(view_bottom * bar_height))
            if thumb_bottom - thumb_top < 34:
                thumb_bottom = min(bar_height - 3, thumb_top + 34)
            scroll_bar.create_rectangle(3, thumb_top, 9, thumb_bottom, fill=COLORS["accent"], outline="")
            scroll_bar.create_rectangle(4, thumb_top + 1, 8, thumb_bottom - 1, fill="#6ff4e2", stipple="gray50", outline="")

        def _set_scroll_from_bar(event):
            y_fraction = min(1.0, max(0.0, event.y / 376))
            list_canvas.yview_moveto(y_fraction)
            _redraw_scrollbar()
            return "break"

        scroll_bar.bind("<Button-1>", _set_scroll_from_bar)
        scroll_bar.bind("<B1-Motion>", _set_scroll_from_bar)
        list_canvas.configure(yscrollcommand=_redraw_scrollbar)

        def _scroll_entries(event):
            delta = int(-1 * (event.delta / 120)) if event.delta else 0
            if delta:
                list_canvas.yview_scroll(delta, "units")
            return "break"

        list_canvas.bind("<MouseWheel>", _scroll_entries)
        list_frame.bind("<MouseWheel>", _scroll_entries)
        scroll_bar.bind("<MouseWheel>", _scroll_entries)

        y = 0
        for index, entry in enumerate(entries, start=1):
            self._library_entry_card(0, y, 952, 112, category, index, entry, target_canvas=list_canvas)
            y += 130

        self._add_widget(list_frame, 104, 248, 972, 376)
        self.after_idle(_redraw_scrollbar)

    def _library_entry_card(self, x, y, w, h, category, index, entry, target_canvas=None):
        target = target_canvas or self.canvas

        def add_item(item):
            if target is self.canvas:
                return self._add(item)
            return item

        title = str(entry.get(f"title_{self.lang}") or entry.get("title_ru") or entry.get("title") or "").strip()
        summary = str(entry.get(f"summary_{self.lang}") or entry.get("summary_ru") or entry.get("summary") or "").strip()
        if not title:
            title = f"#{index}"

        image_url = str(entry.get("image_url") or entry.get("image") or "").strip()

        add_item(target.create_rectangle(x + 8, y + 8, x + w + 8, y + h + 8, fill="#010302", stipple="gray50", outline=""))
        add_item(target.create_rectangle(x, y, x + w, y + h, fill=COLORS["glass_lift"], stipple="gray50", outline=COLORS["accent"], width=1))
        add_item(target.create_line(x + 1, y + 1, x + w - 1, y + 1, fill="#ffffff", stipple="gray50", width=1))
        preview = self._load_library_image(image_url, 126, 74)
        add_item(target.create_rectangle(x + 18, y + 19, x + 144, y + 93, fill="#07100e", outline="#5e8d84", width=1))
        if preview:
            add_item(target.create_image(x + 18, y + 19, image=preview, anchor="nw"))
        title_width = w - 370
        add_item(target.create_text(
            x + 162,
            y + 16,
            text=title,
            anchor="nw",
            fill=COLORS["text"],
            font=("Segoe UI Semibold", 11, "bold"),
            width=title_width,
        ))
        if summary:
            add_item(target.create_text(
                x + 162,
                y + 60,
                text=summary,
                anchor="nw",
                fill=COLORS["muted"],
                font=("Segoe UI", 8),
                width=title_width,
            ))
        if target is self.canvas:
            self._button(x + w - 154, y + 38, 124, 36, TEXT[self.lang]["projects_more"], lambda c=category, i=index - 1: self.show_library_entry_detail(c, i), font_size=10)
        else:
            bx, by, bw, bh = x + w - 154, y + 38, 124, 36
            rect = target.create_rectangle(bx, by, bx + bw, by + bh, fill="#0b1b19", outline=COLORS["accent"], width=1)
            label = target.create_text(
                bx + bw / 2,
                by + bh / 2,
                text=TEXT[self.lang]["projects_more"],
                anchor="center",
                fill=COLORS["text"],
                font=("Segoe UI Semibold", 10, "bold"),
            )

            def open_detail(_event=None, c=category, i=index - 1):
                self.show_library_entry_detail(c, i)

            def hover_on(_event=None):
                target.itemconfig(rect, fill=COLORS["glass_lift"])
                target.configure(cursor="hand2")

            def hover_off(_event=None):
                target.itemconfig(rect, fill="#0b1b19")
                target.configure(cursor="")

            for item in (rect, label):
                target.tag_bind(item, "<Button-1>", open_detail)
                target.tag_bind(item, "<Enter>", hover_on)
                target.tag_bind(item, "<Leave>", hover_off)

    def show_library_entry_detail(self, category, entry_index):
        self.view = "library_entry"
        self.current_project_category = category
        self.current_library_entry_index = entry_index
        self._clear_view()
        t = TEXT[self.lang]

        entries = LIBRARY_LINKS.get(category, [])
        if entry_index < 0 or entry_index >= len(entries):
            self.show_project_category(category)
            return
        entry = entries[entry_index]
        title = str(entry.get(f"title_{self.lang}") or entry.get("title_ru") or entry.get("title") or "").strip()
        body = str(entry.get(f"body_{self.lang}") or entry.get("body_ru") or entry.get("body") or "").strip()
        url = str(entry.get("url") or "").strip()
        discord_url = str(entry.get("discord_url") or "").strip()
        image_url = str(entry.get("image_url") or entry.get("image") or "").strip()
        if not title:
            title = f"#{entry_index + 1}"

        self._panel(64, 92, 1052, 536, alpha="surface")
        self._section_label(104, 134, title)
        self.buttons["back"] = self._button(966, 116, 110, 36, t["back"], lambda c=category: self.show_project_category(c))

        img_x, img_y, img_w, img_h = 104, 194, 360, 202
        self._add(self.canvas.create_rectangle(img_x + 8, img_y + 8, img_x + img_w + 8, img_y + img_h + 8, fill="#010302", stipple="gray50", outline=""))
        self._add(self.canvas.create_rectangle(img_x, img_y, img_x + img_w, img_y + img_h, fill=COLORS["glass_lift"], stipple="gray50", outline=COLORS["accent"], width=1))
        image = self._load_library_image(image_url, img_w - 2, img_h - 2)
        if image:
            self._add(self.canvas.create_image(img_x + 1, img_y + 1, image=image, anchor="nw"))
        else:
            self._add(self.canvas.create_text(
                img_x + img_w / 2,
                img_y + img_h / 2,
                text=t["projects_image_missing"],
                anchor="center",
                fill=COLORS["muted"],
                font=("Segoe UI", 10),
            ))

        action_y = img_y + img_h + 24
        action_w = 172
        action_h = 42
        if url:
            self._button(img_x, action_y, action_w, action_h, t["projects_download"], lambda link=url: webbrowser.open(link), primary=True, font_size=11)
        if discord_url:
            discord_x = img_x + action_w + 16 if url else img_x
            self._button(discord_x, action_y, action_w, action_h, t["projects_discord"], lambda link=discord_url: webbrowser.open(link), font_size=11)

        body_x = 492
        body_y = 190
        body_width = 584
        body_height = 404
        frame = tk.Frame(self.canvas, bg=COLORS["accent"], padx=1, pady=1)
        text = tk.Text(
            frame,
            bg="#06100e",
            fg=COLORS["text"],
            insertbackground=COLORS["text"],
            selectbackground="#24534c",
            bd=0,
            highlightthickness=0,
            padx=0,
            pady=0,
            wrap="word",
            font=("Segoe UI", 11),
            cursor="arrow",
        )
        text.pack(side="left", fill="both", expand=True)
        scroll = tk.Scrollbar(
            frame,
            orient="vertical",
            command=text.yview,
            bg="#071311",
            activebackground=COLORS["glass_lift"],
            troughcolor="#020706",
            highlightthickness=0,
            bd=0,
            width=12,
        )
        scroll.pack(side="right", fill="y")
        text.configure(yscrollcommand=scroll.set)
        text.bind("<MouseWheel>", lambda event: text.yview_scroll(int(-1 * (event.delta / 120)), "units"))
        text.insert("1.0", body)

        text.configure(state="disabled")
        self._add_widget(frame, body_x, body_y, body_width, body_height)

    def _library_detail_buttons_y(self, text, font_spec, width, text_y):
        font = tkfont.Font(family=font_spec[0], size=font_spec[1])
        line_height = font.metrics("linespace")
        total_lines = 0
        for paragraph in str(text).splitlines() or [""]:
            total_lines += self._wrapped_line_count(paragraph, font, width) if paragraph.strip() else 1
        y = text_y + total_lines * line_height + 14
        return min(max(y, 424), 574)

    def _library_text_button(self, parent, text, command, primary=False):
        bg = "#1c554d" if primary else "#0b1b19"
        active_bg = COLORS["accent"] if primary else COLORS["glass_lift"]
        fg = "#ffffff"
        return tk.Button(
            parent,
            text=text,
            command=command,
            bg=bg,
            fg=fg,
            activebackground=active_bg,
            activeforeground="#ffffff",
            relief="flat",
            bd=0,
            highlightthickness=1,
            highlightbackground=COLORS["accent"],
            highlightcolor=COLORS["accent"],
            font=("Segoe UI Semibold", 11, "bold"),
            width=16,
            height=2,
            cursor="hand2",
        )

    def _load_library_image(self, image_url, width, height):
        if not image_url:
            return None
        try:
            if image_url.startswith(("http://", "https://")):
                request = Request(image_url, headers={"User-Agent": "AnthologyLauncher"})
                with urlopen(request, timeout=6) as response:
                    raw = response.read(6 * 1024 * 1024)
                image = Image.open(BytesIO(raw)).convert("RGB")
            else:
                path = Path(image_url)
                if not path.is_absolute():
                    path = self.assets / image_url
                image = Image.open(path).convert("RGB")
            image.thumbnail((width, height), Image.Resampling.LANCZOS)
            canvas = Image.new("RGB", (width, height), "#07100e")
            canvas.paste(image, ((width - image.width) // 2, (height - image.height) // 2))
            photo = ImageTk.PhotoImage(canvas)
            self.library_images.append(photo)
            return photo
        except Exception as exc:
            self._debug_log(f"library image failed: {type(exc).__name__}: {exc}")
            return None

    def _project_card(self, x, y, w, h, title, subtitle, command):
        self._add(self.canvas.create_rectangle(x + 8, y + 10, x + w + 8, y + h + 10, fill="#010302", stipple="gray50", outline=""))
        rect = self._add(self.canvas.create_rectangle(x, y, x + w, y + h, fill=COLORS["glass_lift"], stipple="gray50", outline=COLORS["accent"], width=1))
        top = self._add(self.canvas.create_line(x + 1, y + 1, x + w - 1, y + 1, fill="#ffffff", stipple="gray50", width=1))
        title_item = self._add(self.canvas.create_text(x + 22, y + 24, text=title, anchor="nw", fill=COLORS["text"], font=("Segoe UI Semibold", 12, "bold"), width=w - 44))
        divider = self._add(self.canvas.create_line(x + 22, y + 78, x + w - 22, y + 78, fill=COLORS["accent"], stipple="gray50"))
        subtitle_item = self._add(self.canvas.create_text(x + 22, y + 100, text=subtitle, anchor="nw", fill=COLORS["muted"], font=("Segoe UI", 9), width=w - 44))
        for item in (rect, top, title_item, divider, subtitle_item):
            self.canvas.tag_bind(item, "<Button-1>", lambda _e, cmd=command: cmd())
            self.canvas.tag_bind(item, "<Enter>", lambda _e, r=rect: (self.canvas.itemconfig(r, fill="#162c28"), self.canvas.configure(cursor="hand2")))
            self.canvas.tag_bind(item, "<Leave>", lambda _e, r=rect: (self.canvas.itemconfig(r, fill=COLORS["glass_lift"]), self.canvas.configure(cursor="")))

    def _bottom_update_bar(self, t):
        play_x = 87
        play_w = 260
        play_h = 36
        play_gap = 6
        play_y = 582
        self.buttons["play_online"] = self._button(play_x, play_y, play_w, play_h, t["play_online"], self.play_online, primary=True, font_size=13)
        self.buttons["play"] = self._button(play_x, play_y + play_h + play_gap, play_w, play_h, t["play"], self.play, primary=True, font_size=13)
        self.buttons["play_original"] = self._button(play_x, play_y + (play_h + play_gap) * 2, play_w, play_h, t["play_original"], self.play_original, primary=True, font_size=13)
        update_x = 382
        update_w = 509
        update_bar_y = 663
        self._add(self.canvas.create_text(update_x, 604, text=t["update"].upper(), anchor="w", fill=COLORS["accent"], font=("Segoe UI Semibold", 10, "bold")))
        self.update_status_item = self._add(self.canvas.create_text(update_x, 629, text=t["update_ready"], anchor="w", fill=COLORS["muted"], font=("Segoe UI", 10), width=update_w))
        self.update_progress_bg = self._add(self.canvas.create_rectangle(update_x, update_bar_y, update_x + update_w, update_bar_y + 14, fill="#091211", outline="#5e8d84", width=1))
        self.update_progress_fill = self._add(self.canvas.create_rectangle(update_x, update_bar_y, update_x, update_bar_y + 9, fill=COLORS["accent"], outline=""))
        self.update_progress_text = self._add(self.canvas.create_text(update_x + update_w / 2, update_bar_y + 26, text="", anchor="center", fill=COLORS["muted"], font=("Segoe UI", 7)))
        self._set_update_progress(0, "")

    def _panel(self, x, y, w, h, alpha="solid"):
        if alpha == "frameless":
            return
        if alpha == "surface":
            self._add(self.canvas.create_rectangle(x, y, x + w, y + h, fill="#020706", stipple="gray75", outline=""))
            return
        fill = {"solid": COLORS["glass"], "light": COLORS["glass_soft"], "bar": COLORS["glass"]}[alpha]
        outline = {"solid": "#8ed6c9", "light": "#7ab6aa", "bar": "#63847d"}[alpha]
        if alpha != "bar":
            self._add(self.canvas.create_rectangle(x + 10, y + 12, x + w + 10, y + h + 12, fill="#010302", stipple="gray50", outline=""))
        stipple = {"solid": "gray50", "light": "gray25", "bar": "gray50"}[alpha]
        kwargs = {"fill": fill, "outline": outline, "width": 1}
        if stipple:
            kwargs["stipple"] = stipple
        self._add(self.canvas.create_rectangle(x, y, x + w, y + h, **kwargs))
        if alpha != "bar":
            self._add(self.canvas.create_line(x + 1, y + 1, x + w - 1, y + 1, fill="#ffffff", stipple="gray50", width=1))

    def _section_label(self, x, y, text):
        self._add(self.canvas.create_text(x, y, text=text.upper(), anchor="w", fill=COLORS["accent"], font=("Segoe UI Semibold", 10, "bold")))
        self._add(self.canvas.create_line(x, y + 22, x + 132, y + 22, fill=COLORS["accent"], stipple="gray50"))

    def _news_item(self, x, y, title, body, width=336):
        self._add(self.canvas.create_text(x, y, text=title, anchor="w", fill=COLORS["text"], font=("Segoe UI Semibold", 13, "bold")))
        self._add(self.canvas.create_text(x, y + 30, text=body, anchor="nw", fill=COLORS["muted"], font=("Segoe UI", 10), width=width))

    def _wrapped_line_count(self, text, font, width):
        lines = 1
        current = ""
        for word in text.split():
            candidate = word if not current else f"{current} {word}"
            if font.measure(candidate) <= width:
                current = candidate
            else:
                lines += 1
                current = word
        return lines

    def _news_feed(self, x, y, w, h, t):
        title_font = tkfont.Font(family="Segoe UI Semibold", size=13, weight="bold")
        body_font = tkfont.Font(family="Segoe UI", size=10)
        title_line = title_font.metrics("linespace")
        body_line = body_font.metrics("linespace")
        current_y = y
        for index in range(1, 8):
            title = t.get(f"news_{index}")
            text = t.get(f"news_{index}_body")
            if not title or not text:
                continue
            title_lines = self._wrapped_line_count(title, title_font, w)
            body_lines = self._wrapped_line_count(text, body_font, w)
            item_height = title_lines * title_line + 10 + body_lines * body_line + 22
            if current_y + item_height > y + h:
                break
            self._add(self.canvas.create_text(x, current_y, text=title, anchor="nw", fill=COLORS["text"], font=title_font, width=w))
            current_y += title_lines * title_line + 12
            self._add(self.canvas.create_text(x, current_y, text=text, anchor="nw", fill=COLORS["muted"], font=body_font, width=w))
            current_y += body_lines * body_line + 24

    def _button(self, x, y, w, h, text, command, primary=False, font_size=None):
        fill = "#173a35" if primary else COLORS["glass_lift"]
        hover = "#23544d" if primary else "#162c28"
        outline = COLORS["accent"] if primary else "#829d96"
        rect = self._add(self.canvas.create_rectangle(x, y, x + w, y + h, fill=fill, stipple="gray50", outline=outline, width=1))
        self._add(self.canvas.create_line(x + 1, y + 1, x + w - 1, y + 1, fill="#ffffff", stipple="gray50"))
        if font_size is None:
            font_size = 13 if primary and (h <= 36 or len(text) > 22) else 15 if primary else 10
        label = self._add(self.canvas.create_text(x + w / 2, y + h / 2, text=text, fill=COLORS["text"], font=("Segoe UI Semibold", font_size, "bold"), width=max(20, w - 16)))
        for item in (rect, label):
            self.canvas.tag_bind(item, "<Button-1>", lambda _e, cmd=command: cmd())
            self.canvas.tag_bind(item, "<Enter>", lambda _e, r=rect, c=hover: self.canvas.itemconfig(r, fill=c))
            self.canvas.tag_bind(item, "<Leave>", lambda _e, r=rect, c=fill: self.canvas.itemconfig(r, fill=c))
        return {"rect": rect, "label": label}

    def _toggle(self, x, y, label, command):
        box = self._add(self.canvas.create_rectangle(x, y, x + 48, y + 24, fill=COLORS["glass_lift"], stipple="gray50", outline="#829d96", width=1))
        knob = self._add(self.canvas.create_rectangle(x + 5, y + 5, x + 19, y + 19, fill=COLORS["faint"], outline=""))
        text = self._add(self.canvas.create_text(x + 64, y + 12, text=label, anchor="w", fill=COLORS["text"], font=("Segoe UI", 11)))
        for item in (box, knob, text):
            self.canvas.tag_bind(item, "<Button-1>", lambda _e, cmd=command: cmd())
        return {"box": box, "knob": knob, "label": text, "x": x, "y": y}

    def _start_drag(self, event):
        if event.y <= self._sy(TOP_BAR) and event.x < self._sx(WIDTH - 112):
            self.drag_x = event.x_root
            self.drag_y = event.y_root
            self.drag_window_x = self.winfo_x()
            self.drag_window_y = self.winfo_y()
        else:
            self._stop_drag(event)

    def _drag(self, event):
        if self.drag_y:
            x = self.drag_window_x + event.x_root - self.drag_x
            y = self.drag_window_y + event.y_root - self.drag_y
            x, y = self._clamp_window_position(x, y)
            self.geometry(f"+{x}+{y}")

    def _stop_drag(self, _event=None):
        self.drag_x = 0
        self.drag_y = 0

    def _close_window(self, _event=None):
        self._stop_drag()
        self.destroy()
        return "break"

    def _minimize_window(self, _event=None):
        self._stop_drag()
        if sys.platform == "win32":
            try:
                import ctypes
                user32 = ctypes.windll.user32
                hwnd = self.winfo_id()
                parent = user32.GetParent(hwnd)
                if parent:
                    hwnd = parent
                user32.ShowWindow(hwnd, 6)
                return "break"
            except Exception as exc:
                self._debug_log(f"minimize failed: {type(exc).__name__}: {exc}")
        self.iconify()
        return "break"

    def _set_renderer(self, value):
        self.renderer = value
        self._refresh_all()

    def _flip(self, name):
        setattr(self, name, not getattr(self, name))
        self._refresh_all()

    def _shadow_prev(self):
        self.shadow = (self.shadow - 1) % len(SHADOWS)
        self._refresh_all()

    def _shadow_next(self):
        self.shadow = (self.shadow + 1) % len(SHADOWS)
        self._refresh_all()

    def _screen_bounds(self):
        max_x = max(0, self.winfo_screenwidth() - self.window_width)
        max_y = max(0, self.winfo_screenheight() - self.window_height)
        return max_x, max_y

    def _clamp_window_position(self, x, y):
        max_x, max_y = self._screen_bounds()
        return max(0, min(int(x), max_x)), max(0, min(int(y), max_y))

    def _center_window(self):
        self.update_idletasks()
        x = (self.winfo_screenwidth() - self.window_width) // 2
        y = (self.winfo_screenheight() - self.window_height) // 2
        x, y = self._clamp_window_position(x, y)
        self.geometry(f"{self.window_width}x{self.window_height}+{x}+{y}")

    def _refresh_all(self):
        for name, btn in self.render_buttons.items():
            active = name == self.renderer
            self.canvas.itemconfig(btn["rect"], fill="#1c554d" if active else COLORS["glass_lift"], outline=COLORS["accent"] if active else "#829d96")

        values = {
            "debug": self.debug,
            "sound_fix": self.sound_fix,
            "prefetch": self.prefetch,
            "chat_relay_always": self.chat_relay_always,
            "reset": self.reset_user,
            "avx": self.avx,
        }
        for key, item in self.toggle_items.items():
            active = values[key]
            x, y = item["x"], item["y"]
            self.canvas.itemconfig(item["box"], fill="#1c554d" if active else COLORS["glass_lift"], outline=COLORS["accent"] if active else "#829d96")
            self.canvas.coords(
                item["knob"],
                self._sx(x + (29 if active else 5)),
                self._sy(y + 5),
                self._sx(x + (43 if active else 19)),
                self._sy(y + 19),
            )
            self.canvas.itemconfig(item["knob"], fill="#e8fff7" if active else COLORS["faint"])
        if hasattr(self, "shadow_value"):
            self.canvas.itemconfig(self.shadow_value, text=str(SHADOWS[self.shadow]))

    def toggle_language(self):
        self.lang = "en" if self.lang == "ru" else "ru"
        if self.view == "settings":
            self.show_settings()
        elif self.view == "projects":
            self.show_projects()
        elif self.view == "info":
            self.show_info()
        elif self.view == "info_section":
            self.show_info_section(getattr(self, "current_info_section", "requirements"))
        elif self.view == "project_category":
            self.show_project_category(getattr(self, "current_project_category", "dev"))
        elif self.view == "library_entry":
            self.show_library_entry_detail(
                getattr(self, "current_project_category", "dev"),
                getattr(self, "current_library_entry_index", 0),
            )
        else:
            self.show_home()

    def _load_config(self):
        cfg = self.root_dir / "AnomalyLauncher.cfg"
        if not cfg.exists():
            return
        lines = cfg.read_text(encoding="utf-8", errors="ignore").splitlines()
        if len(lines) > 0 and lines[0] in RENDERERS:
            self.renderer = lines[0]
        if len(lines) > 1:
            self.debug = lines[1] == "DBG"
        if len(lines) > 2:
            try:
                self.shadow = max(0, min(4, int(lines[2])))
            except ValueError:
                self.shadow = 0
        if len(lines) > 3:
            self.sound_fix = lines[3] == "SNDFIX"
        if len(lines) > 4:
            self.prefetch = lines[4] == "SNDPREFETCH"
        if len(lines) > 5:
            self.lang = "en" if lines[5] == "EN" else "ru"
        if len(lines) > 6:
            self.avx = lines[6] == "AVX"
        if len(lines) > 7:
            self.chat_relay_always = lines[7] == "CHATRELAYALWAYS"

    def write_config(self):
        lines = [
            self.renderer,
            "DBG" if self.debug else "NODBG",
            str(self.shadow),
            "SNDFIX" if self.sound_fix else "NOSNDFIX",
            "SNDPREFETCH" if self.prefetch else "NOSNDPREFETCH",
            "EN" if self.lang == "en" else "RU",
            "AVX" if self.avx else "NOAVX",
            "CHATRELAYALWAYS" if self.chat_relay_always else "NOCHATRELAYALWAYS",
        ]
        (self.root_dir / "AnomalyLauncher.cfg").write_text("\n".join(lines) + "\n", encoding="utf-8")

    def write_commandline(self):
        args = [f"-smap{SHADOWS[self.shadow]}"]
        if self.debug:
            args.append("-dbg")
        if self.prefetch:
            args.append("-prefetch_sounds")
        (self.root_dir / "commandline.txt").write_text("\n".join(args) + "\n", encoding="utf-8")

    def apply_sound_fix(self):
        alsof = self.root_dir / "bin" / "alsoft.ini"
        bak = self.root_dir / "bin" / "alsoft.ini.bak"
        if self.sound_fix:
            if alsof.exists():
                if bak.exists():
                    bak.unlink()
                alsof.rename(bak)
        else:
            if not alsof.exists() and bak.exists():
                bak.rename(alsof)

    def reset_user_ltx_file(self):
        appdata = self.root_dir / "appdata"
        appdata.mkdir(exist_ok=True)
        user = appdata / "user.ltx"
        old = appdata / "user.ltx.old"
        if user.exists():
            if old.exists():
                old.unlink()
            user.rename(old)
        shutil.copyfile(self.assets / "default_user_ltx.txt", user)

    def _shader_cache_path(self):
        return self.root_dir / "appdata" / "shaders_cache"

    def _delete_shader_cache(self, show_message=False):
        cache = self._shader_cache_path()
        if not cache.exists():
            if show_message:
                messagebox.showinfo("Anthology Launcher", TEXT[self.lang]["cache_missing"])
            return False

        try:
            if cache.is_symlink() or not cache.is_dir():
                cache.unlink()
            else:
                shutil.rmtree(cache)
            self._debug_log(f"shader cache deleted: {cache}")
            if show_message:
                messagebox.showinfo("Anthology Launcher", TEXT[self.lang]["cache_done"])
            return True
        except OSError as exc:
            self._debug_log(f"shader cache delete failed: {type(exc).__name__}: {exc}")
            if show_message:
                messagebox.showerror("Anthology Launcher", f"Failed to delete shader cache:\n{exc}")
            return False

    def delete_shader_cache(self):
        self._delete_shader_cache(show_message=True)

    def open_logs_folder(self):
        logs = self.root_dir / "appdata" / "logs"
        logs.mkdir(parents=True, exist_ok=True)
        if sys.platform == "win32":
            os.startfile(logs)
        else:
            webbrowser.open(logs.as_uri())

    def _is_relay_chat_running(self):
        if sys.platform != "win32":
            return False
        try:
            output = subprocess.check_output(
                ["tasklist", "/FI", "IMAGENAME eq Chernobyl Relay Chat.exe", "/NH"],
                stderr=subprocess.DEVNULL,
                text=True,
                encoding="utf-8",
                errors="ignore",
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
            return "Chernobyl Relay Chat.exe" in output
        except Exception as exc:
            self._debug_log(f"relay chat process check failed: {type(exc).__name__}: {exc}")
            return False

    def open_relay_chat(self, show_errors=True):
        if self._is_relay_chat_running():
            self._debug_log("relay chat launch skipped: already running")
            return True
        chat_exe = self.root_dir / "Chernobyl Relay Chat.exe"
        if not chat_exe.exists():
            if show_errors:
                messagebox.showerror(
                    "Anthology Launcher",
                    f"{TEXT[self.lang]['relay_chat_missing']}:\n{chat_exe}\n\n{TEXT[self.lang]['relay_chat_update_hint']}",
                )
            else:
                self._debug_log(f"relay chat autostart skipped: missing {chat_exe}")
            return False
        try:
            subprocess.Popen(
                [str(chat_exe)],
                cwd=str(self.root_dir),
                env=self._prepare_external_launch(),
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
            self._debug_log("relay chat launched")
            return True
        except Exception as exc:
            if show_errors:
                messagebox.showerror("Anthology Launcher", f"{TEXT[self.lang]['launch_error']}:\n{chat_exe}\n\n{exc}")
            else:
                self._debug_log(f"relay chat autostart failed: {type(exc).__name__}: {exc}")
            return False

    def _start_relay_chat_if_enabled(self):
        if self.chat_relay_always:
            self.open_relay_chat(show_errors=False)

    def save_settings(self):
        self.write_config()
        messagebox.showinfo("Anthology Launcher", TEXT[self.lang]["saved"])

    def about(self):
        messagebox.showinfo("Anthology Launcher", "Anthology Launcher\nModern Python UI\nA.N.T.H.O.L.O.G.Y 2.1")

    def support_project(self):
        self.show_support()

    def _prepare_external_launch(self):
        env = os.environ.copy()
        for key in list(env):
            if key.startswith("_PYI_") or key.startswith("_MEI"):
                env.pop(key, None)
        env["PYINSTALLER_RESET_ENVIRONMENT"] = "1"
        if sys.platform == "win32" and getattr(sys, "frozen", False):
            try:
                import ctypes
                ctypes.windll.kernel32.SetDllDirectoryW(None)
            except Exception:
                pass
        return env

    def _modpack_mods_dir(self):
        exact = self.root_dir.parent / MODPACK_FOLDER / "mods"
        if exact.exists():
            return exact

        search_roots = [self.root_dir.parent, self.root_dir.parent.parent, self.root_dir]
        seen = set()
        for root in search_roots:
            try:
                root = root.resolve()
            except OSError:
                continue
            if root in seen or not root.exists():
                continue
            seen.add(root)
            for child in root.iterdir():
                if not child.is_dir():
                    continue
                name = child.name.lower()
                if "mo2_cbt" not in name and "anthology" not in name:
                    continue
                candidate = child / "mods"
                if candidate.exists():
                    return candidate
        return exact

    def _mod_organizer_exe(self):
        exact = self.root_dir.parent / MODPACK_FOLDER / MOD_ORGANIZER_EXE_NAME
        if exact.exists():
            return exact
        mods_dir = self._modpack_mods_dir()
        candidate = mods_dir.parent / MOD_ORGANIZER_EXE_NAME
        if candidate.exists():
            return candidate
        search_roots = [self.root_dir.parent, self.root_dir.parent.parent, self.root_dir]
        seen = set()
        for root in search_roots:
            try:
                root = root.resolve()
            except OSError:
                continue
            if root in seen or not root.exists():
                continue
            seen.add(root)
            for child in root.iterdir():
                if not child.is_dir():
                    continue
                candidate = child / MOD_ORGANIZER_EXE_NAME
                if candidate.exists():
                    return candidate
        return exact

    def _modpack_available(self):
        return self._modpack_mods_dir().exists()

    def _selected_game_exe(self):
        suffix = f"{self.renderer}{'AVX' if self.avx else ''}"
        return self.root_dir / "bin" / f"Anomaly{suffix}.exe"

    def _path_for_mo2(self, path):
        return str(Path(path)).replace("\\", "/")

    def _path_for_qt_bytearray(self, path):
        return str(Path(path)).replace("\\", "\\\\")

    def _path_for_qt_quoted_argument(self, path):
        return f'\\"{self._path_for_qt_bytearray(path)}\\"'

    def _sync_mod_organizer_paths(self, mo2_path):
        if self._running_mod_organizer_processes():
            self._debug_log("MO2 path sync skipped: Mod Organizer is already running")
            return

        game_dir = self.root_dir.resolve()
        mo2_dir = mo2_path.parent.resolve()
        ini_path = mo2_dir / "ModOrganizer.ini"
        if not ini_path.exists():
            self._debug_log(f"MO2 path sync skipped: missing {ini_path}")
            return

        bin_dir = game_dir / "bin"
        explorer_dir = mo2_dir / "explorer++"
        executables = {
            "Anomaly (DX11-AVX)": (bin_dir / "AnomalyDX11AVX.exe", bin_dir, None),
            "Anomaly (DX11)": (bin_dir / "AnomalyDX11.exe", bin_dir, None),
            "Anomaly (DX10-AVX)": (bin_dir / "AnomalyDX10AVX.exe", bin_dir, None),
            "Anomaly (DX10)": (bin_dir / "AnomalyDX10.exe", bin_dir, None),
            "Anomaly (DX9-AVX)": (bin_dir / "AnomalyDX9AVX.exe", bin_dir, None),
            "Anomaly (DX9)": (bin_dir / "AnomalyDX9.exe", bin_dir, None),
            "Anomaly (DX8-AVX)": (bin_dir / "AnomalyDX8AVX.exe", bin_dir, None),
            "Anomaly (DX8)": (bin_dir / "AnomalyDX8.exe", bin_dir, None),
            "Anomaly Launcher": (game_dir / LAUNCHER_EXE_NAME, game_dir, None),
            "Explore Virtual Folder": (
                explorer_dir / "Explorer++.exe",
                explorer_dir,
                self._path_for_qt_quoted_argument(game_dir),
            ),
        }

        lines = ini_path.read_text(encoding="utf-8-sig").splitlines(keepends=True)
        title_to_index = {}
        for line in lines:
            body = line.rstrip("\r\n")
            if "\\title=" not in body:
                continue
            prefix, title = body.split("\\title=", 1)
            if prefix.isdigit():
                title_to_index[title] = prefix

        updates = {}
        for title, (binary, working_dir, arguments) in executables.items():
            index = title_to_index.get(title)
            if not index:
                continue
            updates[(index, "binary")] = self._path_for_mo2(binary)
            updates[(index, "workingDirectory")] = self._path_for_mo2(working_dir)
            if arguments is not None:
                updates[(index, "arguments")] = arguments

        changed = False
        new_lines = []
        for line in lines:
            ending = "\r\n" if line.endswith("\r\n") else "\n" if line.endswith("\n") else ""
            body = line[: len(line) - len(ending)] if ending else line
            replacement = None
            if body.startswith("gamePath="):
                replacement = f"gamePath=@ByteArray({self._path_for_qt_bytearray(game_dir)})"
            elif "=" in body:
                left, _value = body.split("=", 1)
                if "\\" in left:
                    index, key = left.split("\\", 1)
                    replacement_value = updates.get((index, key))
                    if replacement_value is not None:
                        replacement = f"{left}={replacement_value}"

            if replacement is not None and replacement != body:
                new_lines.append(replacement + ending)
                changed = True
            else:
                new_lines.append(line)

        if not changed:
            self._debug_log("MO2 path sync: paths already correct")
            return

        backup = ini_path.with_suffix(f".ini.bak_{time.strftime('%Y%m%d_%H%M%S')}")
        shutil.copy2(ini_path, backup)
        ini_path.write_text("".join(new_lines), encoding="utf-8")
        self._debug_log(f"MO2 path sync updated {ini_path}; backup={backup}")

    def _desktop_path(self):
        if sys.platform == "win32":
            try:
                import ctypes
                buffer = ctypes.create_unicode_buffer(260)
                if ctypes.windll.shell32.SHGetFolderPathW(None, 0, None, 0, buffer) == 0:
                    return Path(buffer.value)
            except Exception:
                pass
        return Path.home() / "Desktop"

    def ensure_desktop_shortcut(self):
        if not getattr(sys, "frozen", False) or sys.platform != "win32":
            return
        if not self._is_game_install_dir():
            self._debug_log(f"desktop shortcut skipped outside game root: {self.root_dir}")
            return
        shortcut = self._desktop_path() / "ANTHOLOGY.lnk"
        target = Path(sys.executable).resolve()
        icon_location = f"{target},0"
        try:
            script = (
                "$shell = New-Object -ComObject WScript.Shell; "
                f"$link = $shell.CreateShortcut('{self._ps_literal(shortcut)}'); "
                f"$link.TargetPath = '{self._ps_literal(target)}'; "
                f"$link.WorkingDirectory = '{self._ps_literal(self.root_dir)}'; "
                f"$link.IconLocation = '{self._ps_literal(icon_location)}'; "
                "$link.Description = 'ANTHOLOGY Launcher'; "
                "$link.Save()"
            )
            subprocess.run(
                ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script],
                cwd=str(self.root_dir),
                timeout=10,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
                env=self._prepare_external_launch(),
                check=True,
            )
        except Exception as exc:
            self._debug_log(f"desktop shortcut failed: {type(exc).__name__}: {exc}")

    def _is_game_install_dir(self):
        return (
            (self.root_dir / "fsgame.ltx").exists()
            and (self.root_dir / "bin").is_dir()
            and (self.root_dir / "db").is_dir()
        )

    def _ps_literal(self, value):
        return str(value).replace("'", "''")

    def _modpack_missing_message(self, mods_dir):
        root = self.root_dir.parent
        expected = root / MODPACK_FOLDER / "mods"
        return (
            f"{TEXT[self.lang]['update_missing']}:\n{mods_dir}\n\n"
            f"{TEXT[self.lang]['update_expected']}:\n"
            f"{root}\\Anomaly-1.5.3-Anthology 2.1\n"
            f"{root}\\{MODPACK_FOLDER}\\mods\n\n"
            f"Update repo: {MODPACK_REPO}"
        )

    def _set_update_status(self, text, color=None):
        if self.update_status_item:
            try:
                self.canvas.itemconfig(self.update_status_item, text=text, fill=color or COLORS["muted"])
            except tk.TclError:
                self.update_status_item = None

    def _set_update_progress(self, value, text=None):
        if not self.update_progress_bg or not self.update_progress_fill:
            return
        value = max(0, min(100, int(value)))
        coords = self.canvas.coords(self.update_progress_bg)
        if len(coords) != 4:
            return
        x1, y1, x2, y2 = coords
        fill_x = x1 + ((x2 - x1) * value / 100.0)
        self.canvas.coords(self.update_progress_fill, x1, y1, fill_x, y2)
        self.canvas.itemconfig(self.update_progress_fill, state="normal" if value > 0 else "hidden")
        if self.update_progress_text is not None:
            self.canvas.itemconfig(self.update_progress_text, text=text if text is not None else (f"{value}%" if value else ""))

    def _set_engine_status(self, text, color=None):
        if self.engine_status_item:
            try:
                self.canvas.itemconfig(self.engine_status_item, text=text, fill=color or COLORS["muted"])
            except tk.TclError:
                self.engine_status_item = None

    def _set_engine_progress(self, value, text=None):
        if not self.engine_progress_bg or not self.engine_progress_fill:
            return
        value = max(0, min(100, int(value)))
        coords = self.canvas.coords(self.engine_progress_bg)
        if len(coords) != 4:
            return
        x1, y1, x2, y2 = coords
        fill_x = x1 + ((x2 - x1) * value / 100.0)
        self.canvas.coords(self.engine_progress_fill, x1, y1, fill_x, y2)
        self.canvas.itemconfig(self.engine_progress_fill, state="normal" if value > 0 else "hidden")
        if self.engine_progress_text is not None:
            self.canvas.itemconfig(self.engine_progress_text, text=text if text is not None else (f"{value}%" if value else ""))

    def sync_modpack_update(self):
        if self.updating:
            return
        if self._block_update_if_mod_organizer_running():
            return
        self.updating = True
        self._set_update_status("DB: проверка версии..." if self.lang == "ru" else "DB: checking version...", COLORS["accent_2"])
        self._set_update_progress(0, "")
        self._start_background_worker("sync-update", self._sync_combined_update_worker)

    def sync_db_update(self):
        if self.updating:
            return
        if self._block_update_if_mod_organizer_running():
            return
        self.updating = True
        self._set_update_status("DB: проверка версии..." if self.lang == "ru" else "DB: checking version...", COLORS["accent_2"])
        self._set_update_progress(0, "")
        self._start_background_worker("db-update", self._sync_db_update_worker)

    def sync_engine_update(self):
        if self.updating:
            self._debug_log("engine click ignored: update already running")
            return
        if self._block_update_if_mod_organizer_running():
            return
        manifest = self._engine_manifest()
        version = self._engine_manifest_version(manifest)
        label = self._engine_manifest_label(manifest)
        if not self._engine_update_available(manifest):
            self._debug_log(f"engine update skipped: already at {version}")
            self._set_engine_status(self._engine_status_text(), COLORS["accent"])
            self._set_engine_progress(100, "100%")
            self._show_update_result_dialog(True, f"Движок уже обновлен.\n\nВерсия: {version}")
            return
        if not messagebox.askyesno(
            "Anthology Launcher",
            f"Обновить движок {label} до версии {version}?",
        ):
            self._debug_log("engine update cancelled by user")
            return
        self._debug_log(f"engine update requested: mode=mt root={self.root_dir}")
        self.updating = True
        self.engine_update_manifest = manifest
        self._set_engine_status(f"Проверка движка {version}...", COLORS["accent_2"])
        self._set_engine_progress(0, "")
        self._set_update_status(f"Проверка движка {version}...", COLORS["accent_2"])
        self._set_update_progress(0, "")
        self._start_background_worker("engine-update", self._sync_engine_update_worker)

    def _start_background_worker(self, name, target, *args):
        self._debug_log(f"{name}: starting thread")

        def runner():
            self._debug_log(f"{name}: thread entered")
            try:
                target(*args)
            except BaseException as exc:
                self._debug_log(f"{name}: thread crashed: {type(exc).__name__}: {exc}")
                self.after(0, lambda e=exc: self._finish_git_update(False, f"Фоновая задача остановилась:\n{e}"))
            finally:
                self._debug_log(f"{name}: thread exited")

        try:
            thread = threading.Thread(target=runner, name=name)
            thread.start()
            self.worker_threads.append(thread)
            self._debug_log(f"{name}: thread start returned alive={thread.is_alive()}")
        except BaseException as exc:
            self._debug_log(f"{name}: failed to start thread: {type(exc).__name__}: {exc}")
            self._finish_git_update(False, f"Не удалось запустить фоновую задачу:\n{exc}")

    def _sync_engine_update_worker(self):
        mode = "mt"
        self._debug_log(f"engine worker started: mode={mode}")
        log_path = None
        try:
            self.after(0, lambda: self._set_engine_status("Проверка запущенных процессов...", COLORS["accent_2"]))
            self._debug_log("engine worker: checking running game processes")
            running = self._running_game_processes()
            self._debug_log(f"engine worker: running processes={running}")
            if running:
                names = ", ".join(running)
                self.after(0, lambda: self._finish_git_update(False, f"Закройте игру перед обновлением движка:\n{names}", operation="engine"))
                return

            manifest = self.engine_update_manifest or self._engine_manifest()
            version = self._engine_manifest_version(manifest)
            url = self._engine_manifest_url(manifest)
            mode = str(manifest.get("mode") or mode).strip() or "mt"
            label = self._engine_manifest_label(manifest)
            tmp_dir = self.root_dir / "webcache" / "engine_update"
            self._debug_log(f"engine worker: creating tmp_dir={tmp_dir}")
            self._ensure_directory(tmp_dir)
            log_path = tmp_dir / "engine_update.log"
            self._write_update_log(log_path, f"engine mode={mode} version={version}")
            self._write_update_log(log_path, f"download={url}")
            self._debug_log(f"engine worker: log_path={log_path}")

            zip_path = tmp_dir / f"engine_{mode}_{version}.zip"
            self.after(0, lambda: self._set_engine_status(f"Скачивание движка: {label}", COLORS["accent_2"]))
            self.after(0, lambda: self._set_update_status(f"Скачивание движка: {label}", COLORS["accent_2"]))
            self._download_update_archive(
                url,
                zip_path,
                status_callback=self._set_engine_status,
                progress_callback=self._set_engine_progress,
            )
            self._write_update_log(log_path, f"downloaded={zip_path} size={zip_path.stat().st_size}")

            self.after(0, lambda: self._set_engine_status("Установка движка...", COLORS["accent_2"]))
            self.after(0, lambda: self._set_update_status("Установка движка...", COLORS["accent_2"]))
            self.after(0, lambda: self._set_engine_progress(0, "0%"))
            self.after(0, lambda: self._set_update_progress(0, "0%"))
            backup_dir = tmp_dir / f"backup_{time.strftime('%Y%m%d_%H%M%S')}"
            with zipfile.ZipFile(zip_path, "r") as archive:
                self._install_engine_archive(archive, self.root_dir, backup_dir, log_path)
            self._save_engine_state(mode, label, backup_dir, version, url)
            self.after(0, self._refresh_engine_status)

            message = (
                f"Движок обновлен.\n\n"
                f"Версия: {version}\n"
                f"Тип: {label}\n\n"
                f"Backup: {backup_dir}"
            )
            notes = str(manifest.get("notes", "")).strip()
            if notes:
                message += f"\n\n{notes}"
            self.after(0, lambda: self._finish_git_update(True, message, operation="engine"))
        except (URLError, OSError, zipfile.BadZipFile, ValueError) as exc:
            self._debug_log(f"engine worker expected error: {type(exc).__name__}: {exc}")
            if log_path:
                self._write_update_log(log_path, f"ERROR={exc}")
            self.after(0, lambda e=exc: self._finish_git_update(False, f"Не удалось обновить движок:\n{e}", operation="engine"))
        except Exception as exc:
            self._debug_log(f"engine worker unexpected error: {type(exc).__name__}: {exc}")
            if log_path:
                self._write_update_log(log_path, f"ERROR={exc}")
            self.after(0, lambda e=exc: self._finish_git_update(False, f"Не удалось обновить движок:\n{e}", operation="engine"))

    def _running_game_processes(self):
        names = []
        try:
            output = subprocess.check_output(
                ["tasklist", "/FO", "CSV", "/NH"],
                text=True,
                timeout=5,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
        except (OSError, subprocess.CalledProcessError, subprocess.TimeoutExpired):
            return names
        for line in output.splitlines():
            line = line.strip()
            if line and not line.startswith("INFO:"):
                name = line.split('","', 1)[0].strip('"')
                lower_name = name.lower()
                if lower_name.startswith("anomaly") and lower_name.endswith(".exe") and lower_name != LAUNCHER_EXE_NAME.lower():
                    names.append(name)
        return names

    def _running_mod_organizer_processes(self):
        names = []
        try:
            output = subprocess.check_output(
                ["tasklist", "/FO", "CSV", "/NH"],
                text=True,
                timeout=5,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
        except (OSError, subprocess.CalledProcessError, subprocess.TimeoutExpired):
            return names
        for line in output.splitlines():
            line = line.strip()
            if line and not line.startswith("INFO:"):
                name = line.split('","', 1)[0].strip('"')
                if name.lower() == MOD_ORGANIZER_EXE_NAME.lower():
                    names.append(name)
        return names

    def _block_update_if_mod_organizer_running(self):
        running = self._running_mod_organizer_processes()
        if not running:
            return False
        self._debug_log(f"update blocked: Mod Organizer 2 is running: {', '.join(running)}")
        self._set_update_status(TEXT[self.lang]["update_failed"], COLORS["danger"])
        self._set_update_progress(0, "")
        messagebox.showerror("Anthology Launcher", TEXT[self.lang]["update_blocked_mo2"])
        return True

    def _sync_combined_update_worker(self):
        db_ok, db_message = self._sync_db_update_step()
        if not db_ok:
            self.after(0, lambda m=db_message: self._finish_git_update(False, m))
            return

        if not self._modpack_available():
            self._debug_log("sync-update: modpack folder missing, checking game packages")
            mod_ok, mod_message = self._sync_modpack_update_step()
            if mod_ok:
                combined_message = f"{db_message}\n\n{mod_message}"
                self.after(0, lambda m=combined_message: self._finish_git_update(True, m))
            else:
                self._debug_log("sync-update: no modpack/game package update, DB-only update mode")
                self.after(0, lambda m=db_message: self._finish_git_update(True, m))
            return

        self.after(0, lambda: self._set_update_status(TEXT[self.lang]["update_checking"], COLORS["accent_2"]))
        self.after(0, lambda: self._set_update_progress(0, ""))
        mod_ok, mod_message = self._sync_modpack_update_step()
        combined_message = f"{db_message}\n\n{mod_message}"
        self.after(0, lambda ok=mod_ok, m=combined_message: self._finish_git_update(ok, m))

    def _sync_modpack_update_worker(self):
        ok, message = self._sync_modpack_update_step()
        self.after(0, lambda: self._finish_git_update(ok, message))

    def _sync_modpack_update_step(self):
        t = TEXT[self.lang]
        log_path = None
        try:
            mods_dir = self._modpack_mods_dir()
            remote = self._download_update_version()
            try:
                game_payload_remote = self._download_game_payload_version()
            except Exception as exc:
                self._debug_log(f"game payload manifest unavailable: {type(exc).__name__}: {exc}")
                game_payload_remote = {}
            remote_version = str(remote.get("version", "")).strip()
            if not remote_version:
                return False, f"{t['update_failed']}:\nversion.json has no version"

            game_packages = self._game_packages(remote) + self._game_packages(game_payload_remote)
            pending_game_packages = self._pending_game_packages(game_packages)
            modpack_exists = mods_dir.exists()
            local = self._load_update_state(mods_dir) if modpack_exists else {}
            local_version = str(local.get("version", "")).strip()
            needs_repair = self._modpack_needs_repair(mods_dir, local) if modpack_exists else False
            needs_manifest_bootstrap = local_version == remote_version and not self._state_file_list(local) if modpack_exists else False
            manifest_removed_files = self._manifest_removed_files(remote) if modpack_exists else []
            needs_manifest_cleanup = any((mods_dir / rel).is_file() for rel in manifest_removed_files) if modpack_exists else False
            legacy_removed_files = self._legacy_update_removed_files() if modpack_exists else []
            needs_legacy_cleanup = any((mods_dir.parent / rel).is_file() for rel in legacy_removed_files) if modpack_exists else False
            folder_packages = self._folder_packages(remote) if modpack_exists else []
            pending_folder_packages = self._pending_folder_packages(mods_dir, folder_packages) if modpack_exists else []
            full_update_needed = (
                modpack_exists
                and (
                    local_version != remote_version
                    or needs_repair
                    or needs_manifest_bootstrap
                    or needs_manifest_cleanup
                    or needs_legacy_cleanup
                )
            )
            if not modpack_exists and not pending_game_packages:
                return False, self._modpack_missing_message(mods_dir)

            if not full_update_needed and not pending_folder_packages and not pending_game_packages:
                return True, f"{t['update_latest']}\n\n{t['label_version']}: {remote_version}"

            status_text = t["update_available_downloading"]
            if not full_update_needed and pending_game_packages and not pending_folder_packages:
                status_text = "Скачивание пакета файлов игры" if self.lang == "ru" else "Downloading game package"
            elif not full_update_needed and pending_folder_packages:
                status_text = "Скачивание отдельного мода/фикса" if self.lang == "ru" else "Downloading folder package"
            elif local_version == remote_version and (needs_repair or needs_manifest_bootstrap or needs_manifest_cleanup):
                status_text = t["update_repair"]
            self.after(0, lambda s=status_text: self._set_update_status(s, COLORS["accent_2"]))
            tmp_dir = self.root_dir / "webcache" / "launcher_update"
            self._reset_directory(tmp_dir)
            log_path = tmp_dir / "update.log"
            self._write_update_log(log_path, f"mods_dir={mods_dir}")
            self._write_update_log(
                log_path,
                f"remote_version={remote_version} local_version={local_version} needs_repair={needs_repair} folder_packages={len(pending_folder_packages)} game_packages={len(pending_game_packages)}",
            )
            deleted_files = 0
            deleted_dirs = 0
            if modpack_exists and full_update_needed:
                zip_url = remote.get("zip_url") or UPDATE_ZIP_URL
                zip_path = tmp_dir / "update.zip"
                self._write_update_log(log_path, f"download={zip_url}")
                self._download_update_archive(zip_url, zip_path, attempts=5, timeout=300)
                self._write_update_log(log_path, f"downloaded={zip_path} size={zip_path.stat().st_size}")

                self.after(0, lambda: self._set_update_status(t["update_applying"], COLORS["accent_2"]))
                self.after(0, lambda: self._set_update_progress(0, "0%"))
                with zipfile.ZipFile(zip_path, "r") as archive:
                    installed_files = self._install_update_archive(archive, mods_dir, log_path)
                cleanup_dirs = set()
                deleted_files += self._remove_stale_update_files(mods_dir, self._state_file_list(local), installed_files, log_path, cleanup_dirs)
                deleted_files += self._remove_manifest_files(mods_dir, manifest_removed_files, log_path, cleanup_dirs)
                deleted_files += self._remove_legacy_update_files(mods_dir, legacy_removed_files, log_path, cleanup_dirs)
                deleted_dirs += self._remove_empty_update_dirs(cleanup_dirs, mods_dir.parent, log_path)
                self._save_update_state(mods_dir, remote, installed_files)
                self._write_update_log(log_path, "main state saved")

            applied_packages = []
            if pending_folder_packages:
                applied_packages, package_deleted = self._apply_folder_packages(
                    mods_dir,
                    pending_folder_packages,
                    tmp_dir,
                    log_path,
                )
                deleted_files += package_deleted
            applied_game_packages = []
            if pending_game_packages:
                applied_game_packages, game_deleted = self._apply_game_packages(
                    self.root_dir,
                    pending_game_packages,
                    tmp_dir,
                    log_path,
                )
                deleted_files += game_deleted
            shutil.rmtree(tmp_dir, ignore_errors=True)

            notes = remote.get("notes", "")
            message = f"{t['update_done']}\n\n{t['label_version']}: {remote_version}"
            if applied_packages:
                label = "Отдельные пакеты" if self.lang == "ru" else "Folder packages"
                message += f"\n{label}: {', '.join(applied_packages)}"
            if applied_game_packages:
                label = "Файлы игры" if self.lang == "ru" else "Game files"
                message += f"\n{label}: {', '.join(applied_game_packages)}"
            if deleted_files:
                message += f"\n{t['label_removed_old_files']}: {deleted_files}"
            if deleted_dirs:
                message += f"\n{t['label_removed_empty_dirs']}: {deleted_dirs}"
            if notes:
                message += f"\n\n{notes}"
            return True, message
        except (URLError, OSError, zipfile.BadZipFile, ValueError) as exc:
            message = f"{t['update_failed']}:\n{exc}"
            if log_path:
                self._write_update_log(log_path, f"ERROR={exc}")
            return False, message
        except Exception as exc:
            message = f"{t['update_failed']}:\n{exc}"
            if log_path:
                self._write_update_log(log_path, f"ERROR={exc}")
            return False, message

    def _modpack_needs_repair(self, mods_dir, local_state=None):
        tracked_files = self._state_file_list(local_state or {})
        if tracked_files:
            missing = []
            for rel in tracked_files:
                if not (mods_dir / rel).is_file():
                    missing.append(rel.as_posix())
                    if len(missing) >= 10:
                        break
            if missing:
                self._debug_log(f"modpack repair needed: missing files: {' | '.join(missing)}")
                return True
            return False

        git_dir = mods_dir / ".git"
        if not git_dir.exists():
            return False
        try:
            output = subprocess.check_output(
                ["git", "status", "--porcelain", "--untracked-files=no"],
                cwd=str(mods_dir),
                text=True,
                timeout=30,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
        except (OSError, subprocess.CalledProcessError, subprocess.TimeoutExpired) as exc:
            self._debug_log(f"modpack repair check failed: {exc}")
            return False
        dirty = bool(output.strip())
        if dirty:
            lines = output.splitlines()
            preview = " | ".join(lines[:10])
            if len(lines) > 10:
                preview += f" | ... +{len(lines) - 10}"
            self._debug_log(f"modpack repair needed: {preview}")
        return dirty

    def _sync_db_update_worker(self):
        ok, message = self._sync_db_update_step()
        self.after(0, lambda: self._finish_git_update(ok, message))

    def check_content_updates_async(self):
        if self.updating or self.update_probe_running:
            return
        self.update_probe_running = True
        self._set_update_status(TEXT[self.lang]["update_checking"], COLORS["accent_2"])
        threading.Thread(target=self._check_content_updates_worker, daemon=True).start()

    def _check_content_updates_worker(self):
        try:
            updates = self._available_update_names()
            text = self._updates_status_text(updates)
            color = COLORS["accent_2"] if updates else COLORS["muted"]
            self.after(0, lambda s=text, c=color: None if self.updating else self._set_update_status(s, c))
        except Exception as exc:
            self._debug_log(f"content update check failed: {type(exc).__name__}: {exc}")
            self.after(0, lambda: None if self.updating else self._set_update_status(TEXT[self.lang]["update_check_failed"], COLORS["danger"]))
        finally:
            self.update_probe_running = False

    def _content_updates_available(self):
        return bool(self._available_update_names())

    def _available_update_names(self):
        updates = []
        checks = (
            ("modpack", "игра" if self.lang == "ru" else "game", self._modpack_update_available),
            ("db", "DB", self._db_update_available),
            ("engine", "движок" if self.lang == "ru" else "engine", self._engine_update_available),
        )
        for key, name, check in checks:
            try:
                if check():
                    updates.append(name)
            except Exception as exc:
                self._debug_log(f"{key} update check failed: {type(exc).__name__}: {exc}")
        return updates

    def _updates_status_text(self, updates):
        if not updates:
            return TEXT[self.lang]["update_none"]
        prefix = "Есть обновление:" if self.lang == "ru" else "Update available:"
        return f"{prefix} {', '.join(updates)}"

    def _modpack_update_available(self):
        mods_dir = self._modpack_mods_dir()
        remote = self._download_update_version()
        remote_version = str(remote.get("version", "")).strip()
        if not remote_version:
            return False

        game_packages = self._game_packages(remote)
        try:
            game_packages += self._game_packages(self._download_game_payload_version())
        except Exception as exc:
            self._debug_log(f"game payload update check failed: {type(exc).__name__}: {exc}")
        if self._pending_game_packages(game_packages):
            return True

        if not mods_dir.exists():
            return False

        local = self._load_update_state(mods_dir)
        local_version = str(local.get("version", "")).strip()
        if remote_version != local_version:
            return True

        if self._pending_folder_packages(mods_dir, self._folder_packages(remote)):
            return True

        return self._modpack_needs_repair(mods_dir, local) or not self._state_file_list(local)

    def _db_update_available(self):
        db_dir = self.root_dir / "db"
        if not db_dir.exists():
            return True

        remote = self._download_db_update_version()
        remote_version = str(remote.get("version", "")).strip()
        if not remote_version:
            return False

        game_packages = self._game_packages(remote)
        try:
            game_packages += self._game_packages(self._download_game_payload_version())
        except Exception as exc:
            self._debug_log(f"game payload update check failed: {type(exc).__name__}: {exc}")
        if self._pending_game_packages(game_packages):
            return True

        local = self._load_json_file(self._db_state_path())
        return remote_version != str(local.get("version", "")).strip()

    def _engine_manifest(self):
        try:
            manifest = self._download_engine_version()
        except Exception as exc:
            self._debug_log(f"engine manifest fallback: {type(exc).__name__}: {exc}")
            manifest = {}
        if not isinstance(manifest, dict):
            manifest = {}
        manifest.setdefault("version", ENGINE_RELEASE_VERSION)
        manifest.setdefault("mode", "mt")
        manifest.setdefault("label", "MT TEST")
        manifest.setdefault("url", ENGINE_MT_URL)
        self.engine_update_manifest = manifest
        return manifest

    def _engine_manifest_version(self, manifest):
        return str(manifest.get("version") or ENGINE_RELEASE_VERSION).strip() or ENGINE_RELEASE_VERSION

    def _engine_manifest_url(self, manifest):
        return str(manifest.get("url") or ENGINE_MT_URL).strip() or ENGINE_MT_URL

    def _engine_manifest_label(self, manifest):
        return str(manifest.get("label") or "MT TEST").strip() or "MT TEST"

    def _engine_update_available(self, manifest=None):
        manifest = manifest or self._engine_manifest()
        version = self._engine_manifest_version(manifest)
        state = self._load_engine_state()
        return str(state.get("version", "")).strip() != version

    def _sync_db_update_step(self):
        t = TEXT[self.lang]
        log_path = None
        try:
            self.after(0, lambda: self._set_update_status(t["db_checking_process"], COLORS["accent_2"]))
            running = self._running_game_processes()
            if running:
                names = ", ".join(running)
                return False, f"{t['db_close_game']}:\n{names}"

            db_dir = self.root_dir / "db"
            if not db_dir.exists():
                return False, f"{t['db_missing']}:\n{db_dir}"

            tmp_dir = self.root_dir / "webcache" / "db_update"
            self._ensure_directory(tmp_dir)
            log_path = tmp_dir / "db_update.log"
            self._write_update_log(log_path, f"db_dir={db_dir}")

            remote = self._download_db_update_version()
            try:
                game_payload_remote = self._download_game_payload_version()
            except Exception as exc:
                self._debug_log(f"game payload manifest unavailable: {type(exc).__name__}: {exc}")
                game_payload_remote = {}
            entries = self._db_manifest_entries(remote)
            removed_entries = self._db_removed_files(remote)
            game_packages = self._game_packages(remote) + self._game_packages(game_payload_remote)
            pending_game_packages = self._pending_game_packages(game_packages)
            remote_version = str(remote.get("version", "")).strip()
            if not remote_version:
                return False, f"{t['db_failed']}:\n{t['db_no_version']}"
            if not entries:
                return False, f"{t['db_failed']}:\n{t['db_no_files']}"
            self._validate_db_manifest_transition(entries, removed_entries)

            changed = self._db_files_needing_download(entries, log_path)
            total = len(changed)
            staged = []
            self._write_update_log(log_path, f"download files={total}")
            for index, entry in enumerate(changed, start=1):
                rel = entry["path"]
                target = self.root_dir / rel
                tmp_file = tmp_dir / f"{index:04d}-{target.name}.download"
                tmp_file.unlink(missing_ok=True)
                url = self._db_entry_url(remote, entry)
                self.after(0, lambda i=index, n=total, p=rel: self._set_update_status(f"{t['db_downloading']} {i}/{n} {Path(p).name}", COLORS["accent_2"]))
                self._write_update_log(log_path, f"download {index}/{total}: {url} -> {target}")
                self._download_update_archive(url, tmp_file)
                self._verify_db_file(tmp_file, entry)
                staged.append((entry, tmp_file, target))

            backup_root = self._db_backup_root(remote_version)
            self.after(0, lambda: self._set_update_status(t["db_removing_extra"], COLORS["accent_2"]))
            deleted = self._mirror_db_archives(entries, log_path, backup_root)
            deleted += self._remove_db_removed_files(removed_entries, log_path, backup_root)

            for index, (_entry, tmp_file, target) in enumerate(staged, start=1):
                self._backup_db_file(target, backup_root, log_path)
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.move(str(tmp_file), str(target))
                value = int(index * 100 / max(1, total))
                self.after(0, lambda v=value: self._set_update_progress(v, f"{v}%"))

            self._save_db_update_state(remote)
            self._prune_db_backups()
            applied_game_packages = []
            if pending_game_packages:
                applied_game_packages, game_deleted = self._apply_game_packages(
                    self.root_dir,
                    pending_game_packages,
                    tmp_dir,
                    log_path,
                )
                deleted += game_deleted

            if not changed and not applied_game_packages:
                message = f"{t['db_latest']}\n\n{t['label_version']}: {remote_version}\n{t['label_removed_files']}: {deleted}"
                if backup_root.exists():
                    message += f"\nBackup: {backup_root}"
                return True, message
            notes = str(remote.get("notes", "")).strip()
            message = f"{t['db_done']}\n\n{t['label_version']}: {remote_version}\n{t['label_downloaded_files']}: {total}\n{t['label_removed_files']}: {deleted}"
            if applied_game_packages:
                label = "Файлы игры" if self.lang == "ru" else "Game files"
                message += f"\n{label}: {', '.join(applied_game_packages)}"
            if backup_root.exists():
                message += f"\nBackup: {backup_root}"
            if notes:
                message += f"\n\n{notes}"
            return True, message
        except (URLError, OSError, ValueError) as exc:
            if log_path:
                self._write_update_log(log_path, f"ERROR={exc}")
            return False, f"{t['db_failed']}:\n{exc}"
        except Exception as exc:
            if log_path:
                self._write_update_log(log_path, f"ERROR={exc}")
            return False, f"{t['db_failed']}:\n{exc}"

    def _download_update_version(self):
        try:
            url = f"{UPDATE_VERSION_URL}?t={int(time.time())}"
            with urlopen(url, timeout=30) as response:
                return json.loads(response.read().decode("utf-8-sig"))
        except Exception:
            request = Request(
                UPDATE_VERSION_API_URL,
                headers={
                    "Accept": "application/vnd.github.raw",
                    "Cache-Control": "no-cache",
                    "User-Agent": "AnthologyLauncher",
                },
            )
            with urlopen(request, timeout=30) as response:
                return json.loads(response.read().decode("utf-8-sig"))

    def _download_db_update_version(self):
        try:
            url = f"{DB_UPDATE_VERSION_RAW_URL}?t={int(time.time())}"
            with urlopen(url, timeout=30) as response:
                return json.loads(response.read().decode("utf-8-sig"))
        except Exception:
            request = Request(
                DB_UPDATE_VERSION_URL,
                headers={
                    "Accept": "application/vnd.github.raw",
                    "Cache-Control": "no-cache",
                    "User-Agent": "AnthologyLauncher",
                },
            )
            with urlopen(request, timeout=30) as response:
                return json.loads(response.read().decode("utf-8-sig"))

    def _download_game_payload_version(self):
        try:
            url = f"{GAME_PAYLOAD_VERSION_RAW_URL}?t={int(time.time())}"
            with urlopen(url, timeout=30) as response:
                return json.loads(response.read().decode("utf-8-sig"))
        except Exception:
            request = Request(
                GAME_PAYLOAD_VERSION_URL,
                headers={
                    "Accept": "application/vnd.github.raw",
                    "Cache-Control": "no-cache",
                    "User-Agent": "AnthologyLauncher",
                },
            )
            with urlopen(request, timeout=30) as response:
                return json.loads(response.read().decode("utf-8-sig"))

    def _download_engine_version(self):
        try:
            url = f"{ENGINE_VERSION_RAW_URL}?t={int(time.time())}"
            with urlopen(url, timeout=20) as response:
                return json.loads(response.read().decode("utf-8-sig"))
        except Exception:
            request = Request(
                ENGINE_VERSION_URL,
                headers={
                    "Accept": "application/vnd.github.raw",
                    "Cache-Control": "no-cache",
                    "User-Agent": "AnthologyLauncher",
                },
            )
            with urlopen(request, timeout=20) as response:
                return json.loads(response.read().decode("utf-8-sig"))

    def check_launcher_update_async(self):
        if not getattr(sys, "frozen", False):
            return
        threading.Thread(target=self._check_launcher_update_worker, daemon=True).start()

    def _check_launcher_update_worker(self):
        try:
            remote = self._download_launcher_version()
            remote_version = str(remote.get("version", "")).strip()
            if not remote_version or not self._is_newer_version(remote_version, LAUNCHER_VERSION):
                return
            self.after(0, lambda: self._confirm_launcher_update(remote))
        except Exception:
            return

    def _download_launcher_version(self):
        try:
            url = f"{LAUNCHER_VERSION_RAW_URL}?t={int(time.time())}"
            with urlopen(url, timeout=20) as response:
                return json.loads(response.read().decode("utf-8-sig"))
        except Exception:
            request = Request(
                LAUNCHER_VERSION_URL,
                headers={
                    "Accept": "application/vnd.github.raw",
                    "Cache-Control": "no-cache",
                    "User-Agent": "AnthologyLauncher",
                },
            )
            with urlopen(request, timeout=20) as response:
                return json.loads(response.read().decode("utf-8-sig"))

    def _is_newer_version(self, remote_version, local_version):
        def parts(value):
            result = []
            for part in str(value).replace("-", ".").split("."):
                try:
                    result.append(int(part))
                except ValueError:
                    result.append(part)
            return result

        return parts(remote_version) > parts(local_version)

    def _confirm_launcher_update(self, remote):
        remote_version = str(remote.get("version", "")).strip()
        message = (
            f"Доступна новая версия лаунчера: {remote_version}\n"
            f"Текущая версия: {LAUNCHER_VERSION}\n\n"
            "Обновить сейчас?"
        )
        if messagebox.askyesno("Anthology Launcher", message):
            threading.Thread(target=self._install_launcher_update_worker, args=(remote,), daemon=True).start()

    def _install_launcher_update_worker(self, remote):
        log_path = self._launcher_update_debug_log_path()
        try:
            url = remote.get("exe_url") or LAUNCHER_EXE_URL
            tmp_base = Path(tempfile.gettempdir()) / "AnthologyLauncherUpdate"
            self._ensure_directory(tmp_base)
            tmp_dir = tmp_base / f"launcher_self_update_{os.getpid()}"
            self._reset_directory(tmp_dir)
            new_exe = tmp_dir / LAUNCHER_EXE_NAME
            self._write_launcher_update_debug(
                log_path,
                [
                    "download start",
                    f"launcher_version={LAUNCHER_VERSION}",
                    f"url={url}",
                    f"root_dir={self.root_dir}",
                    f"sys_executable={Path(sys.executable).resolve()}",
                    f"cwd={Path.cwd()}",
                    f"pid={os.getpid()}",
                    f"tmp_dir={tmp_dir}",
                ],
            )
            self.after(0, lambda: self._set_update_status("Обновление лаунчера...", COLORS["accent_2"]))
            self.after(0, lambda: self._set_update_progress(0, "0%"))
            self._download_update_archive(url, new_exe, attempts=6, timeout=180)
            self._write_launcher_update_debug(
                log_path,
                [
                    "download finished",
                    f"downloaded_path={new_exe}",
                    f"downloaded_size={new_exe.stat().st_size if new_exe.exists() else 'missing'}",
                ],
            )
            self.after(0, lambda: self._restart_with_launcher_update(new_exe))
        except Exception as exc:
            self._write_launcher_update_debug(log_path, [f"ERROR={type(exc).__name__}: {exc}"])
            self.after(0, lambda e=exc: messagebox.showerror("Anthology Launcher", f"Не удалось обновить лаунчер:\n{e}"))

    def _launcher_update_debug_log_path(self):
        return Path(tempfile.gettempdir()) / "AnthologyLauncherUpdate" / "launcher_update_debug.log"

    def _write_launcher_update_debug(self, path, lines):
        try:
            path.parent.mkdir(parents=True, exist_ok=True)
            stamp = time.strftime("%Y-%m-%d %H:%M:%S")
            with path.open("a", encoding="utf-8") as handle:
                handle.write(f"[{stamp}]\n")
                for line in lines:
                    handle.write(str(line) + "\n")
                handle.write("\n")
        except Exception:
            pass

    def _restart_with_launcher_update(self, new_exe):
        current_exe = Path(sys.executable).resolve()
        if current_exe.name.lower() != LAUNCHER_EXE_NAME.lower():
            current_exe = current_exe.with_name(LAUNCHER_EXE_NAME)
        updater = new_exe.parent / "apply_launcher_update.bat"
        updater_log = new_exe.parent / "apply_launcher_update.log"
        lines = [
            "@echo off",
            "chcp 65001 >nul",
            "setlocal",
            f"set \"SRC={new_exe}\"",
            f"set \"DST={current_exe}\"",
            f"set \"DST_DIR={self.root_dir}\"",
            f"set \"LOG={updater_log}\"",
            f"set \"PID={os.getpid()}\"",
            "echo updater started > \"%LOG%\"",
            "echo date=%DATE% time=%TIME% >> \"%LOG%\"",
            "echo src=%SRC% >> \"%LOG%\"",
            "echo dst=%DST% >> \"%LOG%\"",
            "echo dst_dir=%DST_DIR% >> \"%LOG%\"",
            "echo pid=%PID% >> \"%LOG%\"",
            "if exist \"%SRC%\" for %%A in (\"%SRC%\") do echo src_size=%%~zA >> \"%LOG%\"",
            "if exist \"%DST%\" for %%A in (\"%DST%\") do echo dst_size_before=%%~zA >> \"%LOG%\"",
            "tasklist /FI \"IMAGENAME eq AnomalyLauncher.exe\" >> \"%LOG%\" 2>&1",
            ":wait_loop",
            "tasklist /FI \"PID eq %PID%\" | find \"%PID%\" >nul",
            "if not errorlevel 1 (",
            "  timeout /t 1 /nobreak >nul",
            "  goto wait_loop",
            ")",
            "set /a COPY_TRY=0",
            ":copy_loop",
            "set /a COPY_TRY+=1",
            "copy /Y \"%SRC%\" \"%DST%\" >> \"%LOG%\" 2>&1",
            "if errorlevel 1 (",
            "  if %COPY_TRY% GEQ 180 goto copy_failed",
            "  timeout /t 1 /nobreak >nul",
            "  goto copy_loop",
            ")",
            "echo copy ok >> \"%LOG%\"",
            "if exist \"%DST%\" for %%A in (\"%DST%\") do echo dst_size_after=%%~zA >> \"%LOG%\"",
            "echo launcher updated; restarting >> \"%LOG%\"",
            "timeout /t 2 /nobreak >nul",
            "echo env before cleanup: >> \"%LOG%\"",
            "set _PYI >> \"%LOG%\" 2>&1",
            "set _MEI >> \"%LOG%\" 2>&1",
            "echo clearing PyInstaller environment >> \"%LOG%\"",
            "for /f \"tokens=1 delims==\" %%E in ('set _PYI 2^>nul') do set \"%%E=\"",
            "for /f \"tokens=1 delims==\" %%E in ('set _MEI 2^>nul') do set \"%%E=\"",
            "set \"PYINSTALLER_RESET_ENVIRONMENT=1\"",
            "echo env after cleanup: >> \"%LOG%\"",
            "set _PYI >> \"%LOG%\" 2>&1",
            "set _MEI >> \"%LOG%\" 2>&1",
            "echo PYINSTALLER_RESET_ENVIRONMENT=%PYINSTALLER_RESET_ENVIRONMENT% >> \"%LOG%\"",
            "echo start command: start \"\" /D \"%DST_DIR%\" \"%DST%\" >> \"%LOG%\"",
            "start \"\" /D \"%DST_DIR%\" \"%DST%\"",
            "echo restart requested >> \"%LOG%\"",
            "timeout /t 1 /nobreak >nul",
            "tasklist /FI \"IMAGENAME eq AnomalyLauncher.exe\" >> \"%LOG%\" 2>&1",
            "timeout /t 3 /nobreak >nul",
            "del /q \"%SRC%\" >> \"%LOG%\" 2>&1",
            "del /q \"%~f0\" >nul 2>&1",
            "exit /b 0",
            ":copy_failed",
            "echo copy failed >> \"%LOG%\"",
            "pause",
        ]
        updater.write_text("\r\n".join(lines) + "\r\n", encoding="utf-8")
        messagebox.showinfo(
            "Anthology Launcher",
            "Обновление лаунчера скачано.\n\n"
            "После нажатия OK лаунчер закроется и заменит файл.\n"
            "После обновления лаунчер запустится автоматически.",
        )
        self._shell_open_file(updater, show=0)
        self.destroy()

    def _shell_open_file(self, path, show=1):
        if sys.platform != "win32":
            subprocess.Popen([str(path)], cwd=str(path.parent), env=self._prepare_external_launch())
            return
        try:
            import ctypes
            result = ctypes.windll.shell32.ShellExecuteW(None, "open", str(path), None, str(path.parent), int(show))
            if result <= 32:
                raise OSError(f"ShellExecuteW failed: {result}")
        except Exception:
            os.startfile(str(path))

    def _download_update_archive(self, url, path, attempts=3, timeout=60, status_callback=None, progress_callback=None):
        status_callback = status_callback or self._set_update_status
        progress_callback = progress_callback or self._set_update_progress
        last_error = None
        for attempt in range(1, attempts + 1):
            try:
                self._download_update_archive_once(url, path, progress_callback, timeout)
                return
            except Exception as exc:
                last_error = exc
                try:
                    path.unlink(missing_ok=True)
                except OSError:
                    pass
                if attempt >= attempts:
                    break
                status = f"{TEXT[self.lang]['update_downloading']} ({attempt + 1}/{attempts})"
                self.after(0, lambda s=status, cb=status_callback: cb(s, COLORS["accent_2"]))
                time.sleep(2.0)
        raise last_error

    def _download_update_archive_once(self, url, path, progress_callback, timeout):
        last = {"value": -1}
        self._ensure_directory(path.parent)
        with urlopen(url, timeout=timeout) as response, path.open("wb") as target:
            size_header = response.headers.get("Content-Length")
            total_size = int(size_header) if size_header and size_header.isdigit() else 0
            downloaded = 0
            while True:
                chunk = response.read(1024 * 1024)
                if not chunk:
                    break
                target.write(chunk)
                downloaded += len(chunk)
                if total_size <= 0:
                    continue
                value = min(100, int(downloaded * 100 / total_size))
                if value != last["value"]:
                    last["value"] = value
                    self.after(0, lambda v=value, cb=progress_callback: cb(v, f"{v}%"))

    def _ensure_directory(self, path):
        if path.exists() and not path.is_dir():
            path.unlink()
        path.mkdir(parents=True, exist_ok=True)

    def _load_json_file(self, path):
        if not path.exists():
            return {}
        try:
            return json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return {}

    def _reset_directory(self, path):
        if path.exists():
            if path.is_dir():
                shutil.rmtree(path, ignore_errors=True)
            else:
                path.unlink()
        path.mkdir(parents=True, exist_ok=True)

    def _state_path(self, mods_dir):
        return mods_dir.parent / ".launcher_update_state.json"

    def _load_update_state(self, mods_dir):
        return self._load_json_file(self._state_path(mods_dir))

    def _state_file_list(self, state):
        raw_files = state.get("files")
        if not isinstance(raw_files, list):
            return []
        files = []
        for item in raw_files:
            if not isinstance(item, str):
                continue
            parts = Path(item.replace("\\", "/")).parts
            if not parts or any(part in ("", ".", "..") for part in parts):
                continue
            files.append(Path(*parts))
        return files

    def _save_update_state(self, mods_dir, remote, files=None):
        state = {
            "version": str(remote.get("version", "")).strip(),
            "repo": MODPACK_REPO,
            "updated_at": time.strftime("%Y-%m-%d %H:%M"),
        }
        if files:
            state["files"] = sorted({Path(path).as_posix() for path in files}, key=str.casefold)
        self._state_path(mods_dir).write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")

    def _folder_package_state_path(self, mods_dir):
        return mods_dir.parent / ".launcher_folder_packages_state.json"

    def _load_folder_package_state(self, mods_dir):
        state = self._load_json_file(self._folder_package_state_path(mods_dir))
        if not isinstance(state.get("packages"), dict):
            state["packages"] = {}
        return state

    def _save_folder_package_state(self, mods_dir, state):
        state["updated_at"] = time.strftime("%Y-%m-%d %H:%M")
        self._folder_package_state_path(mods_dir).write_text(
            json.dumps(state, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )

    def _game_package_state_path(self):
        return self.root_dir / "webcache" / "game_packages_state.json"

    def _load_game_package_state(self):
        state = self._load_json_file(self._game_package_state_path())
        if not isinstance(state.get("packages"), dict):
            state["packages"] = {}
        return state

    def _save_game_package_state(self, state):
        state["updated_at"] = time.strftime("%Y-%m-%d %H:%M")
        path = self._game_package_state_path()
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")

    def _folder_package_relative_allowed(self, path, package, allow_full=False):
        path = Path(path)
        if path.is_absolute():
            return False
        parts = path.parts
        if not parts or any(part in ("", ".", "..") for part in parts):
            return False
        if parts[0].casefold() != package["folder"].casefold():
            return False
        if allow_full or package["mode"] == "full":
            return len(parts) > 1
        lowered = [part.casefold() for part in parts]
        if len(parts) < 4 or lowered[1] != "gamedata":
            return False
        return lowered[2] in UPDATE_ALLOWED_PARTS

    def _folder_package_paths(self, values, package, field):
        if values in (None, ""):
            return []
        if not isinstance(values, list):
            raise ValueError(f"folder package {field} must be a list")
        paths = []
        seen = set()
        for value in values:
            if not isinstance(value, str):
                raise ValueError(f"folder package {field} entries must be strings")
            path = Path(value.replace("\\", "/"))
            key = path.as_posix().casefold()
            if key in seen:
                continue
            if not self._folder_package_relative_allowed(path, package, allow_full=(field == "removed_files")):
                raise ValueError(f"folder package has invalid {field} path: {value}")
            seen.add(key)
            paths.append(path)
        return paths

    def _game_package_relative_allowed(self, path):
        path = Path(path)
        if path.is_absolute():
            return False
        parts = path.parts
        if not parts or any(part in ("", ".", "..") for part in parts):
            return False
        blocked = {".git", ".github", ".vscode", "__pycache__", "webcache"}
        return not any(part.casefold() in blocked for part in parts)

    def _game_package_paths(self, values, field):
        if values in (None, ""):
            return []
        if not isinstance(values, list):
            raise ValueError(f"game package {field} must be a list")
        paths = []
        seen = set()
        for value in values:
            if not isinstance(value, str):
                raise ValueError(f"game package {field} entries must be strings")
            path = Path(value.replace("\\", "/"))
            key = path.as_posix().casefold()
            if key in seen:
                continue
            if not self._game_package_relative_allowed(path):
                raise ValueError(f"game package has invalid {field} path: {value}")
            seen.add(key)
            paths.append(path)
        return paths

    def _folder_packages(self, remote):
        raw = remote.get("folder_packages", [])
        if raw in (None, ""):
            return []
        if not isinstance(raw, list):
            raise ValueError("version.json folder_packages must be a list")
        packages = []
        seen = set()
        allowed_urls = (
            "https://github.com/Alex020104/anthology-mo2-modpack/releases/download/",
        )
        for index, item in enumerate(raw, start=1):
            if not isinstance(item, dict):
                raise ValueError(f"folder package #{index} must be an object")
            package_id = str(item.get("id", "")).strip()
            folder = str(item.get("folder", "")).strip().replace("\\", "/")
            version = str(item.get("version", "")).strip()
            url = str(item.get("url", "")).strip()
            mode = str(item.get("mode", "standard")).strip().casefold()
            folder_path = Path(folder)
            if not package_id or not re.fullmatch(r"[a-z0-9-]+", package_id):
                raise ValueError(f"folder package #{index} has invalid id")
            if package_id in seen:
                raise ValueError(f"duplicate folder package id: {package_id}")
            if len(folder_path.parts) != 1 or folder_path.is_absolute() or folder_path.parts[0] in (".", ".."):
                raise ValueError(f"folder package #{index} must target one top-level mods folder")
            if not version:
                raise ValueError(f"folder package #{index} has no version")
            if mode not in {"standard", "full"}:
                raise ValueError(f"folder package #{index} has invalid mode: {mode}")
            if not any(url.startswith(prefix) for prefix in allowed_urls):
                raise ValueError(f"folder package #{index} has an untrusted URL")
            package = dict(item)
            package.update({"id": package_id, "folder": folder_path.as_posix(), "version": version, "url": url, "mode": mode})
            package["_files"] = self._folder_package_paths(item.get("files"), package, "files")
            package["_removed_files"] = self._folder_package_paths(item.get("removed_files"), package, "removed_files")
            if not package["_files"]:
                raise ValueError(f"folder package #{index} has no files")
            size = item.get("size")
            if size is not None:
                try:
                    package["size"] = int(size)
                except (TypeError, ValueError) as exc:
                    raise ValueError(f"folder package #{index} has invalid size") from exc
            digest = str(item.get("sha256", "")).strip().casefold()
            if digest and not re.fullmatch(r"[0-9a-f]{64}", digest):
                raise ValueError(f"folder package #{index} has invalid sha256")
            package["sha256"] = digest
            packages.append(package)
            seen.add(package_id)
        return packages

    def _game_packages(self, remote):
        raw = remote.get("game_packages", [])
        if raw in (None, ""):
            return []
        if not isinstance(raw, list):
            raise ValueError("version.json game_packages must be a list")
        packages = []
        seen = set()
        allowed_urls = (
            "https://github.com/Alex020104/anthology-db/releases/download/",
            "https://github.com/Alex020104/anthology-mo2-modpack/releases/download/",
            "https://github.com/Alex020104/anthology-game-files/releases/download/",
        )
        for index, item in enumerate(raw, start=1):
            if not isinstance(item, dict):
                raise ValueError(f"game package #{index} must be an object")
            package_id = str(item.get("id", "")).strip()
            name = str(item.get("name", "")).strip() or package_id
            version = str(item.get("version", "")).strip()
            url = str(item.get("url", "")).strip()
            if not package_id or not re.fullmatch(r"[a-z0-9-]+", package_id):
                raise ValueError(f"game package #{index} has invalid id")
            if package_id in seen:
                raise ValueError(f"duplicate game package id: {package_id}")
            if not version:
                raise ValueError(f"game package #{index} has no version")
            if not any(url.startswith(prefix) for prefix in allowed_urls):
                raise ValueError(f"game package #{index} has an untrusted URL")
            package = dict(item)
            package.update({"id": package_id, "name": name, "version": version, "url": url})
            package["_files"] = self._game_package_paths(item.get("files"), "files")
            package["_removed_files"] = self._game_package_paths(item.get("removed_files"), "removed_files")
            if not package["_files"]:
                raise ValueError(f"game package #{index} has no files")
            size = item.get("size")
            if size is not None:
                try:
                    package["size"] = int(size)
                except (TypeError, ValueError) as exc:
                    raise ValueError(f"game package #{index} has invalid size") from exc
            digest = str(item.get("sha256", "")).strip().casefold()
            if digest and not re.fullmatch(r"[0-9a-f]{64}", digest):
                raise ValueError(f"game package #{index} has invalid sha256")
            package["sha256"] = digest
            packages.append(package)
            seen.add(package_id)
        return packages

    def _pending_folder_packages(self, mods_dir, packages):
        state = self._load_folder_package_state(mods_dir).get("packages", {})
        pending = []
        for package in packages:
            installed = state.get(package["id"], {})
            installed_version = str(installed.get("version", "")).strip()
            expected_missing = any(not (mods_dir / path).is_file() for path in package["_files"])
            removed_present = any((mods_dir / path).is_file() for path in package["_removed_files"])
            if installed_version != package["version"] or expected_missing or removed_present:
                pending.append(package)
        return pending

    def _pending_game_packages(self, packages):
        state = self._load_game_package_state().get("packages", {})
        pending = []
        for package in packages:
            installed = state.get(package["id"], {})
            installed_version = str(installed.get("version", "")).strip()
            expected_missing = any(not (self.root_dir / path).is_file() for path in package["_files"])
            removed_present = any((self.root_dir / path).is_file() for path in package["_removed_files"])
            if installed_version != package["version"] or expected_missing or removed_present:
                pending.append(package)
        return pending

    def _folder_package_archive_relative(self, name, package):
        normalized = name.replace("\\", "/")
        if normalized.startswith("/") or re.match(r"^[A-Za-z]:", normalized):
            return None
        path = Path(normalized)
        if not self._folder_package_relative_allowed(path, package):
            return None
        return path

    def _game_package_archive_relative(self, name):
        normalized = name.replace("\\", "/")
        if normalized.startswith("/") or re.match(r"^[A-Za-z]:", normalized):
            return None
        path = Path(normalized)
        if not self._game_package_relative_allowed(path):
            return None
        return path

    def _install_folder_package_archive(self, archive, mods_dir, package, log_path=None):
        entries = []
        seen = set()
        for info in archive.infolist():
            if info.is_dir():
                continue
            relative = self._folder_package_archive_relative(info.filename, package)
            if relative is None:
                raise ValueError(f"folder package contains an invalid path: {info.filename}")
            key = relative.as_posix().casefold()
            if key in seen:
                raise ValueError(f"folder package contains a duplicate path: {relative}")
            seen.add(key)
            entries.append((info, relative))
        expected = {path.as_posix().casefold() for path in package["_files"]}
        if seen != expected:
            missing = sorted(expected - seen)
            extra = sorted(seen - expected)
            raise ValueError(f"folder package file list mismatch (missing={len(missing)}, extra={len(extra)})")
        total = max(1, len(entries))
        installed = []
        for index, (info, relative) in enumerate(entries, start=1):
            target = mods_dir / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            self._make_writable(target)
            with archive.open(info, "r") as source, target.open("wb") as output:
                shutil.copyfileobj(source, output, length=1024 * 1024)
            installed.append(relative)
            if log_path and (index == 1 or index == total or index % 25 == 0):
                self._write_update_log(log_path, f"folder package copy {index}/{total}: {target}")
        return installed

    def _install_game_package_archive(self, archive, root_dir, package, log_path=None):
        entries = []
        seen = set()
        for info in archive.infolist():
            if info.is_dir():
                continue
            relative = self._game_package_archive_relative(info.filename)
            if relative is None:
                raise ValueError(f"game package contains an invalid path: {info.filename}")
            key = relative.as_posix().casefold()
            if key in seen:
                raise ValueError(f"game package contains a duplicate path: {relative}")
            seen.add(key)
            entries.append((info, relative))
        expected = {path.as_posix().casefold() for path in package["_files"]}
        if seen != expected:
            missing = sorted(expected - seen)
            extra = sorted(seen - expected)
            raise ValueError(f"game package file list mismatch (missing={len(missing)}, extra={len(extra)})")
        total = max(1, len(entries))
        installed = []
        backup_root = root_dir / "webcache" / "game_packages" / "backups" / f"{package['id']}-{package['version']}"
        for index, (info, relative) in enumerate(entries, start=1):
            target = root_dir / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            if target.exists():
                backup = backup_root / relative
                backup.parent.mkdir(parents=True, exist_ok=True)
                self._make_writable(target)
                shutil.copy2(target, backup)
            with archive.open(info, "r") as source, target.open("wb") as output:
                shutil.copyfileobj(source, output, length=1024 * 1024)
            installed.append(relative)
            if log_path and (index == 1 or index == total or index % 25 == 0):
                self._write_update_log(log_path, f"game package copy {index}/{total}: {target}")
        return installed

    def _remove_folder_package_files(self, mods_dir, paths, package, keep=None, log_path=None, cleanup_dirs=None):
        keep_keys = {Path(path).as_posix().casefold() for path in (keep or [])}
        deleted = 0
        for relative in paths:
            relative = Path(relative)
            if relative.as_posix().casefold() in keep_keys or not self._folder_package_relative_allowed(relative, package, allow_full=True):
                continue
            target = mods_dir / relative
            try:
                if not target.is_file() or not self._is_relative_to(target.resolve(), mods_dir.resolve()):
                    continue
                self._make_writable(target)
                target.unlink()
                deleted += 1
                if cleanup_dirs is not None:
                    cleanup_dirs.add(target.parent)
                if log_path:
                    self._write_update_log(log_path, f"folder package delete: {target}")
            except OSError as exc:
                if log_path:
                    self._write_update_log(log_path, f"folder package delete failed: {target}: {exc}")
        return deleted

    def _remove_game_package_files(self, root_dir, paths, keep=None, log_path=None, cleanup_dirs=None):
        keep_keys = {Path(path).as_posix().casefold() for path in (keep or [])}
        deleted = 0
        backup_root = root_dir / "webcache" / "game_packages" / "removed_backups" / time.strftime("%Y%m%d-%H%M%S")
        for relative in paths:
            relative = Path(relative)
            if relative.as_posix().casefold() in keep_keys or not self._game_package_relative_allowed(relative):
                continue
            target = root_dir / relative
            try:
                if not target.is_file() or not self._is_relative_to(target.resolve(), root_dir.resolve()):
                    continue
                backup = backup_root / relative
                backup.parent.mkdir(parents=True, exist_ok=True)
                self._make_writable(target)
                shutil.copy2(target, backup)
                target.unlink()
                deleted += 1
                if cleanup_dirs is not None:
                    cleanup_dirs.add(target.parent)
                if log_path:
                    self._write_update_log(log_path, f"game package delete: {target}")
            except OSError as exc:
                if log_path:
                    self._write_update_log(log_path, f"game package delete failed: {target}: {exc}")
        return deleted

    def _apply_folder_packages(self, mods_dir, packages, tmp_dir, log_path=None):
        state = self._load_folder_package_state(mods_dir)
        package_state = state.setdefault("packages", {})
        applied = []
        deleted = 0
        for index, package in enumerate(packages, start=1):
            label = f"{package['folder']} {package['version']}"
            status = f"Установка мода/фикса: {label}" if self.lang == "ru" else f"Installing folder package: {label}"
            self.after(0, lambda s=status: self._set_update_status(s, COLORS["accent_2"]))
            zip_path = tmp_dir / f"folder-package-{package['id']}.zip"
            self._write_update_log(log_path, f"folder package download={package['url']}")
            self._download_update_archive(package["url"], zip_path, attempts=5, timeout=300)
            expected_size = package.get("size")
            if expected_size is not None and zip_path.stat().st_size != expected_size:
                raise ValueError(f"folder package size mismatch: {package['folder']}")
            if package.get("sha256") and self._sha256_file(zip_path).casefold() != package["sha256"]:
                raise ValueError(f"folder package sha256 mismatch: {package['folder']}")
            with zipfile.ZipFile(zip_path, "r") as archive:
                installed = self._install_folder_package_archive(archive, mods_dir, package, log_path)
            previous = package_state.get(package["id"], {})
            previous_files = [Path(path) for path in previous.get("files", []) if isinstance(path, str)]
            cleanup_dirs = set()
            deleted += self._remove_folder_package_files(mods_dir, previous_files, package, installed, log_path, cleanup_dirs)
            deleted += self._remove_folder_package_files(mods_dir, package["_removed_files"], package, installed, log_path, cleanup_dirs)
            self._remove_empty_update_dirs(cleanup_dirs, mods_dir, log_path)
            package_state[package["id"]] = {
                "folder": package["folder"],
                "mode": package["mode"],
                "version": package["version"],
                "files": sorted({path.as_posix() for path in installed}, key=str.casefold),
                "installed_at": time.strftime("%Y-%m-%d %H:%M"),
            }
            self._save_folder_package_state(mods_dir, state)
            applied.append(label)
            self.after(0, lambda v=int(index * 100 / max(1, len(packages))): self._set_update_progress(v, f"{v}%"))
        return applied, deleted

    def _apply_game_packages(self, root_dir, packages, tmp_dir, log_path=None):
        state = self._load_game_package_state()
        package_state = state.setdefault("packages", {})
        applied = []
        deleted = 0
        for index, package in enumerate(packages, start=1):
            label = f"{package['name']} {package['version']}"
            status = f"Установка файлов игры: {label}" if self.lang == "ru" else f"Installing game files: {label}"
            self.after(0, lambda s=status: self._set_update_status(s, COLORS["accent_2"]))
            zip_path = tmp_dir / f"game-package-{package['id']}.zip"
            self._write_update_log(log_path, f"game package download={package['url']}")
            self._download_update_archive(package["url"], zip_path, attempts=5, timeout=300)
            expected_size = package.get("size")
            if expected_size is not None and zip_path.stat().st_size != expected_size:
                raise ValueError(f"game package size mismatch: {package['name']}")
            if package.get("sha256") and self._sha256_file(zip_path).casefold() != package["sha256"]:
                raise ValueError(f"game package sha256 mismatch: {package['name']}")
            with zipfile.ZipFile(zip_path, "r") as archive:
                installed = self._install_game_package_archive(archive, root_dir, package, log_path)
            previous = package_state.get(package["id"], {})
            previous_files = [Path(path) for path in previous.get("files", []) if isinstance(path, str)]
            cleanup_dirs = set()
            deleted += self._remove_game_package_files(root_dir, previous_files, installed, log_path, cleanup_dirs)
            deleted += self._remove_game_package_files(root_dir, package["_removed_files"], installed, log_path, cleanup_dirs)
            self._remove_empty_update_dirs(cleanup_dirs, root_dir, log_path)
            package_state[package["id"]] = {
                "name": package["name"],
                "version": package["version"],
                "files": sorted({path.as_posix() for path in installed}, key=str.casefold),
                "installed_at": time.strftime("%Y-%m-%d %H:%M"),
            }
            self._save_game_package_state(state)
            applied.append(label)
            self.after(0, lambda v=int(index * 100 / max(1, len(packages))): self._set_update_progress(v, f"{v}%"))
        return applied, deleted

    def _remove_stale_update_files(self, mods_dir, previous_files, current_files, log_path=None, cleanup_dirs=None):
        current = {Path(path).as_posix().casefold() for path in current_files}
        deleted = 0
        for rel in previous_files:
            rel_key = rel.as_posix().casefold()
            if rel_key in current or self._should_preserve_update_path(rel) or not self._is_update_relative_allowed(rel):
                continue
            target = mods_dir / rel
            try:
                if not target.is_file() or not self._is_relative_to(target.resolve(), mods_dir.resolve()):
                    continue
                self._make_writable(target)
                target.unlink()
                if cleanup_dirs is not None:
                    cleanup_dirs.add(target.parent)
                deleted += 1
                if log_path:
                    self._write_update_log(log_path, f"delete stale: {target}")
            except OSError as exc:
                if log_path:
                    self._write_update_log(log_path, f"delete stale failed: {target}: {exc}")
        return deleted

    def _manifest_removed_files(self, remote):
        raw_files = remote.get("removed_files", [])
        if raw_files in ("", None):
            return []
        if not isinstance(raw_files, list):
            raise ValueError("version.json removed_files must be a list")
        files = []
        seen = set()
        for item in raw_files:
            if not isinstance(item, str):
                raise ValueError("version.json removed_files entries must be strings")
            rel = Path(item.replace("\\", "/"))
            key = rel.as_posix().casefold()
            if key in seen:
                continue
            if self._should_preserve_update_path(rel) or not self._is_update_relative_allowed(rel):
                raise ValueError(f"invalid removed file path: {item}")
            seen.add(key)
            files.append(rel)
        return files

    def _legacy_update_removed_files(self):
        return [Path(path) for path in sorted(UPDATE_LEGACY_REMOVE_PATHS, key=str.casefold)]

    def _remove_manifest_files(self, mods_dir, files, log_path=None, cleanup_dirs=None):
        deleted = 0
        for rel in files:
            target = mods_dir / rel
            try:
                if not target.is_file() or not self._is_relative_to(target.resolve(), mods_dir.resolve()):
                    continue
                self._make_writable(target)
                target.unlink()
                if cleanup_dirs is not None:
                    cleanup_dirs.add(target.parent)
                deleted += 1
                if log_path:
                    self._write_update_log(log_path, f"delete manifest removed_file: {target}")
            except OSError as exc:
                if log_path:
                    self._write_update_log(log_path, f"delete manifest removed_file failed: {target}: {exc}")
        return deleted

    def _remove_legacy_update_files(self, mods_dir, files, log_path=None, cleanup_dirs=None):
        mo2_root = mods_dir.parent
        deleted = 0
        for rel in files:
            target = mo2_root / rel
            try:
                if not target.is_file() or not self._is_relative_to(target.resolve(), mo2_root.resolve()):
                    continue
                self._make_writable(target)
                target.unlink()
                if cleanup_dirs is not None:
                    cleanup_dirs.add(target.parent)
                deleted += 1
                if log_path:
                    self._write_update_log(log_path, f"delete legacy removed_file: {target}")
            except OSError as exc:
                if log_path:
                    self._write_update_log(log_path, f"delete legacy removed_file failed: {target}: {exc}")
        return deleted

    def _remove_empty_update_dirs(self, directories, boundary, log_path=None):
        boundary = boundary.resolve()
        deleted = 0
        pending = sorted({Path(path) for path in directories}, key=lambda path: len(path.parts), reverse=True)
        deleted_paths = set()
        for directory in pending:
            current = directory
            while True:
                try:
                    resolved = current.resolve()
                except OSError:
                    break
                key = str(resolved).casefold()
                if key in deleted_paths or resolved == boundary or not self._is_relative_to(resolved, boundary):
                    break
                rel = resolved.relative_to(boundary)
                if self._should_preserve_update_path(rel):
                    break
                try:
                    current.rmdir()
                    deleted_paths.add(key)
                    deleted += 1
                    if log_path:
                        self._write_update_log(log_path, f"delete empty dir: {current}")
                except OSError:
                    break
                current = current.parent
        return deleted

    def _is_relative_to(self, path, parent):
        try:
            path.relative_to(parent)
            return True
        except ValueError:
            return False

    def _is_update_relative_allowed(self, path):
        path = Path(path)
        if path.is_absolute():
            return False
        parts = path.parts
        if not parts or any(part in ("", ".", "..") for part in parts):
            return False
        if parts[0].casefold() in UPDATE_MANAGED_FULL_FOLDERS:
            return True
        lowered = [part.lower() for part in parts]
        if "gamedata" not in lowered:
            return False
        index = lowered.index("gamedata")
        return index + 1 < len(parts) and lowered[index + 1] in UPDATE_ALLOWED_PARTS

    def _should_preserve_update_path(self, path):
        parts = Path(path).parts
        if parts and parts[0].casefold() in UPDATE_MANAGED_FOLDERS:
            return False
        normalized = Path(path).as_posix().casefold()
        return any(marker in normalized for marker in UPDATE_PRESERVE_PATH_MARKERS)

    def _db_state_path(self):
        return self.root_dir / "webcache" / "db_update" / "db_state.json"

    def _save_db_update_state(self, remote):
        manifest_files = [
            entry["path"].as_posix()
            for entry in self._db_manifest_entries(remote)
        ]
        state = {
            "version": str(remote.get("version", "")).strip(),
            "repo": DB_REPO,
            "updated_at": time.strftime("%Y-%m-%d %H:%M"),
            "files": sorted(set(manifest_files), key=str.casefold),
        }
        path = self._db_state_path()
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")

    def _validate_db_manifest_transition(self, entries, removed_files):
        current = {entry["path"].as_posix().casefold() for entry in entries}
        removed = {Path(path).as_posix().casefold() for path in removed_files}
        overlap = sorted(current & removed)
        if overlap:
            raise ValueError(
                "DB manifest lists the same paths in files and removed_files: "
                + ", ".join(overlap[:8])
            )

        previous_raw = self._load_json_file(self._db_state_path()).get("files", [])
        if not isinstance(previous_raw, list):
            return
        previous = set()
        for value in previous_raw:
            path = self._normalize_db_manifest_path(value)
            if path:
                previous.add(path.as_posix().casefold())
        unexpected = sorted(previous - current - removed)
        if unexpected:
            raise ValueError(
                "DB manifest unexpectedly dropped files without removed_files: "
                + ", ".join(unexpected[:8])
            )

    def _db_backup_root(self, version):
        stamp = time.strftime("%Y%m%d_%H%M%S")
        safe_version = re.sub(r"[^0-9A-Za-z._-]+", "_", str(version)).strip("_") or "unknown"
        return self.root_dir / "webcache" / "db_update" / "backups" / f"{stamp}_{safe_version}"

    def _backup_db_file(self, target, backup_root, log_path=None):
        target = Path(target)
        if not target.is_file():
            return False
        root = self.root_dir.resolve()
        resolved = target.resolve()
        if not self._is_relative_to(resolved, root):
            raise ValueError(f"refusing to back up DB file outside game root: {target}")
        relative = resolved.relative_to(root)
        destination = backup_root / relative
        if destination.exists():
            raise ValueError(f"duplicate DB backup destination: {destination}")
        destination.parent.mkdir(parents=True, exist_ok=True)
        self._make_writable(target)
        shutil.move(str(target), str(destination))
        if log_path:
            self._write_update_log(log_path, f"backup DB file: {target} -> {destination}")
        return True

    def _prune_db_backups(self, keep=3):
        root = self.root_dir / "webcache" / "db_update" / "backups"
        if not root.is_dir():
            return
        resolved_root = root.resolve()
        folders = sorted(
            (path for path in root.iterdir() if path.is_dir()),
            key=lambda path: path.stat().st_mtime,
            reverse=True,
        )
        for path in folders[keep:]:
            if self._is_relative_to(path.resolve(), resolved_root):
                shutil.rmtree(path, ignore_errors=True)

    def _db_manifest_entries(self, remote):
        raw_files = remote.get("files")
        if not isinstance(raw_files, list):
            raise ValueError("db_version.json files must be a list")

        entries = []
        seen = set()
        for item in raw_files:
            if isinstance(item, str):
                item = {"path": item}
            if not isinstance(item, dict):
                raise ValueError("db_version.json file entry must be an object")
            path = self._normalize_db_manifest_path(item.get("path", ""))
            if not path:
                raise ValueError(f"invalid DB file path: {item.get('path')}")
            key = path.as_posix().casefold()
            if key in seen:
                raise ValueError(f"duplicate DB file path: {path.as_posix()}")
            seen.add(key)
            entry = dict(item)
            entry["path"] = path
            if "size" in entry and entry["size"] not in ("", None):
                entry["size"] = int(entry["size"])
            if entry.get("sha256"):
                entry["sha256"] = str(entry["sha256"]).strip().lower()
            entries.append(entry)
        return entries

    def _db_removed_files(self, remote):
        raw_files = remote.get("removed_files", [])
        if raw_files in ("", None):
            return []
        if not isinstance(raw_files, list):
            raise ValueError("db_version.json removed_files must be a list")
        files = []
        seen = set()
        for item in raw_files:
            if not isinstance(item, str):
                raise ValueError("db_version.json removed_files entries must be strings")
            parts = Path(item.replace("\\", "/")).parts
            if len(parts) < 2 or parts[0].lower() != "db":
                raise ValueError(f"invalid DB removed file path: {item}")
            if any(part in ("", ".", "..") for part in parts):
                raise ValueError(f"invalid DB removed file path: {item}")
            path = Path(*parts)
            if not self._is_db_archive_file(path):
                raise ValueError(f"invalid DB removed file path: {item}")
            key = path.as_posix().casefold()
            if key not in seen:
                seen.add(key)
                files.append(path)
        return files

    def _normalize_db_manifest_path(self, value):
        if not isinstance(value, str):
            return None
        parts = Path(value.replace("\\", "/")).parts
        if len(parts) < 2 or parts[0].lower() != "db":
            return None
        if any(part in ("", ".", "..") for part in parts):
            return None
        key = Path(*parts).as_posix().casefold()
        if key in DB_ALLOWED_FILES:
            path = Path(*parts)
            if not self._is_db_archive_file(path):
                return None
            return path
        if len(parts) == 2:
            if parts[1].lower() not in DB_ROOT_FILES:
                return None
        elif parts[1].lower() not in DB_ALLOWED_PARTS:
            return None
        path = Path(*parts)
        if not self._is_db_archive_file(path):
            return None
        return path

    def _is_db_archive_file(self, path):
        suffix = path.suffix.lower().lstrip(".")
        if suffix == "db" or suffix == "xdb":
            return True
        if suffix.startswith("db") and suffix[2:].isdigit():
            return True
        if suffix.startswith("xdb") and suffix[3:].isdigit():
            return True
        return False

    def _mirror_db_archives(self, entries, log_path=None, backup_root=None):
        allowed = {entry["path"].as_posix().casefold() for entry in entries}
        deleted = 0
        for folder in DB_ALLOWED_PARTS:
            root = self.root_dir / "db" / folder
            if not root.exists():
                continue
            for path in sorted(root.rglob("*"), key=lambda item: str(item).casefold()):
                if not path.is_file() or not self._is_db_archive_file(path):
                    continue
                rel = path.relative_to(self.root_dir).as_posix().casefold()
                if rel in allowed or rel in DB_PRESERVE_PATHS:
                    continue
                if backup_root is not None:
                    self._backup_db_file(path, backup_root, log_path)
                else:
                    self._make_writable(path)
                    path.unlink()
                deleted += 1
                if log_path:
                    self._write_update_log(log_path, f"remove extra: {path}")
        return deleted

    def _remove_db_removed_files(self, files, log_path=None, backup_root=None):
        deleted = 0
        for rel in files:
            target = self.root_dir / rel
            try:
                if not target.is_file() or not self._is_relative_to(target.resolve(), self.root_dir.resolve()):
                    continue
                if backup_root is not None:
                    self._backup_db_file(target, backup_root, log_path)
                else:
                    self._make_writable(target)
                    target.unlink()
                deleted += 1
                if log_path:
                    self._write_update_log(log_path, f"remove DB removed_file: {target}")
            except OSError as exc:
                if log_path:
                    self._write_update_log(log_path, f"delete DB removed_file failed: {target}: {exc}")
        return deleted

    def _db_files_needing_download(self, entries, log_path=None):
        t = TEXT[self.lang]
        changed = []
        total = max(1, len(entries))
        for index, entry in enumerate(entries, start=1):
            rel = entry["path"]
            target = self.root_dir / rel
            self.after(0, lambda i=index, n=total: self._set_update_status(f"{t['db_checking_hashes']} {i}/{n}", COLORS["accent_2"]))
            if not self._db_file_matches(target, entry):
                changed.append(entry)
                if log_path:
                    self._write_update_log(log_path, f"needs download: {target}")
            value = int(index * 35 / total)
            self.after(0, lambda v=value: self._set_update_progress(v, f"{v}%"))
        return changed

    def _db_file_matches(self, path, entry):
        if not path.exists():
            return False
        expected_size = entry.get("size")
        if expected_size is not None and path.stat().st_size != expected_size:
            return False
        expected_hash = entry.get("sha256")
        if expected_hash and self._sha256_file(path) != expected_hash:
            return False
        return True

    def _sha256_file(self, path):
        digest = hashlib.sha256()
        with path.open("rb") as handle:
            while True:
                chunk = handle.read(1024 * 1024)
                if not chunk:
                    break
                digest.update(chunk)
        return digest.hexdigest()

    def _verify_db_file(self, path, entry):
        expected_size = entry.get("size")
        if expected_size is not None and path.stat().st_size != expected_size:
            raise ValueError(f"downloaded size mismatch for {entry['path']}")
        expected_hash = entry.get("sha256")
        if expected_hash and self._sha256_file(path) != expected_hash:
            raise ValueError(f"downloaded sha256 mismatch for {entry['path']}")

    def _db_entry_url(self, remote, entry):
        if entry.get("url"):
            return str(entry["url"])
        base_url = str(remote.get("base_url", "")).strip()
        asset_name = str(entry.get("asset_name") or entry["path"].as_posix()).strip()
        if not base_url:
            raise ValueError(f"DB file has no url and manifest has no base_url: {entry['path']}")
        return base_url.rstrip("/") + "/" + quote(asset_name.replace("\\", "/"), safe="/")

    def _engine_update_dir(self):
        return self.root_dir / "webcache" / "engine_update"

    def _engine_state_path(self):
        return self._engine_update_dir() / "engine_state.json"

    def _load_engine_state(self):
        path = self._engine_state_path()
        if not path.exists():
            return {}
        try:
            return json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return {}

    def _save_engine_state(self, mode, label, backup_dir, version=None, url=None):
        state = {
            "version": version or ENGINE_RELEASE_VERSION,
            "mode": mode,
            "label": label,
            "url": url or ENGINE_MT_URL,
            "installed_at": time.strftime("%Y-%m-%d %H:%M"),
            "backup": str(backup_dir),
        }
        path = self._engine_state_path()
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")

    def _engine_status_text(self):
        state = self._load_engine_state()
        if state:
            version = state.get("version", "unknown")
            label = state.get("label") or ("MT TEST" if state.get("mode") == "mt" else "обычная")
            installed = state.get("installed_at", "дата неизвестна")
            return f"Движок: {version} / {label} / установлен {installed}"
        return "Движок: не обновлялся через лаунчер"

    def _refresh_engine_status(self):
        if self.engine_status_item:
            self.canvas.itemconfig(self.engine_status_item, text=self._engine_status_text(), fill=COLORS["muted"])

    def _write_update_log(self, path, text):
        try:
            path.parent.mkdir(parents=True, exist_ok=True)
            with path.open("a", encoding="utf-8") as handle:
                handle.write(text + "\n")
        except OSError:
            pass

    def _debug_log(self, text):
        try:
            path = self.root_dir / "webcache" / "launcher_debug.log"
            path.parent.mkdir(parents=True, exist_ok=True)
            stamp = time.strftime("%Y-%m-%d %H:%M:%S")
            with path.open("a", encoding="utf-8") as handle:
                handle.write(f"{stamp} {text}\n")
        except Exception:
            pass

    def _install_update_archive(self, archive, dst, log_path=None):
        self.after(0, lambda: self._set_update_status(TEXT[self.lang]["update_preparing"], COLORS["accent_2"]))
        files = [info for info in archive.infolist() if not info.is_dir() and self._archive_update_relative(info.filename)]
        total = max(1, len(files))
        if not files:
            raise ValueError("Update archive has no configs/scripts/textures files")
        if log_path:
            self._write_update_log(log_path, f"install files={len(files)}")
        self.after(0, lambda: self._set_update_status(TEXT[self.lang]["update_applying"], COLORS["accent_2"]))
        installed = []
        for index, info in enumerate(files, start=1):
            relative = self._archive_update_relative(info.filename)
            target_path = dst / relative
            installed.append(relative)
            if log_path and (index == 1 or index == total or index % 50 == 0):
                self._write_update_log(log_path, f"copy {index}/{total}: {target_path}")
            target_path.parent.mkdir(parents=True, exist_ok=True)
            self._make_writable(target_path)
            with archive.open(info, "r") as source, target_path.open("wb") as target:
                shutil.copyfileobj(source, target, length=1024 * 1024)
            if index == total or index % 10 == 0:
                value = 50 + int(index * 50 / total)
                self.after(0, lambda v=value: self._set_update_progress(v, f"{v}%"))
        return installed

    def _install_engine_archive(self, archive, dst, backup_dir, log_path=None):
        files = [info for info in archive.infolist() if not info.is_dir() and self._archive_engine_relative(info.filename)]
        total = max(1, len(files))
        if not files:
            raise ValueError("Engine archive has no bin/db files")
        if log_path:
            self._write_update_log(log_path, f"install engine files={len(files)}")
        for index, info in enumerate(files, start=1):
            relative = self._archive_engine_relative(info.filename)
            target_path = dst / relative
            backup_path = backup_dir / relative
            if log_path and (index == 1 or index == total or index % 10 == 0):
                self._write_update_log(log_path, f"copy {index}/{total}: {target_path}")
            target_path.parent.mkdir(parents=True, exist_ok=True)
            if target_path.exists():
                backup_path.parent.mkdir(parents=True, exist_ok=True)
                self._make_writable(target_path)
                shutil.copy2(target_path, backup_path)
            with archive.open(info, "r") as source, target_path.open("wb") as target:
                shutil.copyfileobj(source, target, length=1024 * 1024)
            if index == total or index % 2 == 0:
                value = 50 + int(index * 50 / total)
                self.after(0, lambda v=value: self._set_update_progress(v, f"{v}%"))
                self.after(0, lambda v=value: self._set_engine_progress(v, f"{v}%"))

    def _archive_update_relative(self, name):
        parts = Path(name.replace("\\", "/")).parts
        if ".git" in parts or ".github" in parts or ".vscode" in parts or "version.json" in parts:
            return None
        if len(parts) > 1 and parts[1].casefold() in UPDATE_MANAGED_FULL_FOLDERS:
            return Path(*parts[1:])
        if "gamedata" not in parts:
            return None
        index = parts.index("gamedata")
        if index + 1 >= len(parts) or parts[index + 1].lower() not in UPDATE_ALLOWED_PARTS:
            return None
        if index == 0:
            return None
        return Path(*parts[1:])

    def _archive_engine_relative(self, name):
        parts = Path(name.replace("\\", "/")).parts
        if not parts or parts[0].lower() not in ENGINE_ALLOWED_PARTS:
            return None
        return Path(*parts)

    def _make_writable(self, path):
        if not path.exists():
            return
        try:
            os.chmod(path, stat.S_IWRITE | stat.S_IREAD)
        except OSError:
            pass

    def _finish_git_update(self, ok, message, operation="modpack"):
        self.updating = False
        color = COLORS["accent"] if ok else COLORS["danger"]
        t = TEXT[self.lang]
        if operation == "engine":
            engine_text = self._engine_status_text() if ok else "Не удалось обновить движок"
            self._set_engine_status(engine_text, color)
            self._set_engine_progress(100 if ok else 0, "100%" if ok else "")
            self._set_update_status(t["update_ready"], COLORS["muted"])
            self._set_update_progress(0, "")
        else:
            if not ok:
                status_text = t["update_failed"]
            elif t["update_done"] in message:
                status_text = t["update_done"]
            elif t["db_done"] in message:
                status_text = t["db_done"]
            elif t["update_latest"] in message:
                status_text = t["update_latest"]
            elif t["db_latest"] in message:
                status_text = t["db_latest"]
            else:
                status_text = t["update_done"]
            self._set_update_status(status_text, color)
            self._set_update_progress(100 if ok else 0, "100%" if ok else "")
        self._show_update_result_dialog(ok, message)

    def _show_update_result_dialog(self, ok, message):
        dialog = tk.Toplevel(self)
        dialog.overrideredirect(True)
        dialog.transient(self)
        dialog.configure(bg=COLORS["bg"])
        dialog.attributes("-topmost", True)

        width = 520
        text_width = width - 92
        rows = self._update_result_rows(message, ok)
        content_height = sum(self._update_result_row_height(kind, text, text_width) for kind, text in rows)
        height = min(680, max(320, 196 + content_height))
        x = self.winfo_rootx() + max(0, (self.winfo_width() - width) // 2)
        y = self.winfo_rooty() + max(0, (self.winfo_height() - height) // 2)
        dialog.geometry(f"{width}x{height}+{x}+{y}")

        canvas = tk.Canvas(dialog, width=width, height=height, bg=COLORS["bg"], highlightthickness=0)
        canvas.pack(fill="both", expand=True)
        canvas.create_rectangle(1, 1, width - 2, height - 2, outline=COLORS["accent"], width=1)
        canvas.create_rectangle(18, 18, width - 18, height - 18, outline=COLORS["line_soft"], fill=COLORS["glass_soft"], stipple="gray25")
        canvas.create_text(34, 36, text="ЦЕНТР ОБНОВЛЕНИЙ", anchor="w", fill=COLORS["accent"], font=("Segoe UI Semibold", 12, "bold"))
        close_box = canvas.create_text(width - 42, 36, text="X", anchor="center", fill=COLORS["accent"], font=("Segoe UI Semibold", 11, "bold"))
        title = "Обновление завершено" if ok else "Обновление не завершено"
        subtitle = "Все операции выполнены." if ok else "Проверьте детали ниже."
        canvas.create_text(34, 66, text=title, anchor="w", fill=COLORS["text"], font=("Segoe UI Semibold", 16, "bold"))
        canvas.create_text(34, 92, text=subtitle, anchor="w", fill=COLORS["muted"], font=("Segoe UI", 10))

        y_pos = 126
        for kind, text in rows:
            if kind == "spacer":
                y_pos += self._update_result_row_height(kind, text, text_width)
                continue
            fill = COLORS["accent"] if kind == "section" else COLORS["muted"] if kind == "detail" else COLORS["text"]
            font = ("Segoe UI Semibold", 11, "bold") if kind == "section" else ("Segoe UI", 10)
            canvas.create_text(42, y_pos, text=text, anchor="w", fill=fill, font=font, width=text_width)
            y_pos += self._update_result_row_height(kind, text, text_width)

        button_w = 128
        button_h = 38
        bx1 = width - button_w - 34
        by1 = height - button_h - 30
        button = canvas.create_rectangle(bx1, by1, bx1 + button_w, by1 + button_h, outline=COLORS["accent"], fill=COLORS["glass_lift"], width=1)
        label = canvas.create_text(bx1 + button_w / 2, by1 + button_h / 2, text="OK", fill=COLORS["text"], font=("Segoe UI Semibold", 10, "bold"))

        def close(_event=None):
            dialog.destroy()

        def start_drag(event):
            dialog._drag_x = event.x
            dialog._drag_y = event.y

        def drag(event):
            dialog.geometry(f"+{dialog.winfo_x() + event.x - dialog._drag_x}+{dialog.winfo_y() + event.y - dialog._drag_y}")

        for item in (button, label, close_box):
            canvas.tag_bind(item, "<Button-1>", close)
        for item in (button, label):
            canvas.tag_bind(item, "<Enter>", lambda _e: canvas.itemconfig(button, fill=COLORS["glass"]))
            canvas.tag_bind(item, "<Leave>", lambda _e: canvas.itemconfig(button, fill=COLORS["glass_lift"]))
        canvas.bind("<ButtonPress-1>", start_drag)
        canvas.bind("<B1-Motion>", drag)
        dialog.bind("<Escape>", close)
        dialog.focus_force()
        dialog.after(200, lambda: dialog.attributes("-topmost", False) if dialog.winfo_exists() else None)

    def _update_result_rows(self, message, ok):
        message = self._repair_mojibake(message)
        if not ok:
            return [("section", "Ошибка"), ("body", message.strip() or "Неизвестная ошибка")]

        rows = []
        blocks = [block.strip() for block in message.split("\n\n") if block.strip()]
        last_section = None
        for block in blocks:
            lines = [self._repair_mojibake(line.strip()) for line in block.splitlines() if line.strip()]
            if not lines:
                continue
            head = lines[0].rstrip(".")
            if "DB" in head:
                if rows:
                    rows.append(("spacer", ""))
                last_section = "db"
                rows.append(("section", f"DB: {self._friendly_update_status(head, 'db')}"))
                rows.extend(("detail", self._friendly_update_line(line)) for line in lines[1:])
            elif "Модпак" in head or "Modpack" in head:
                if rows:
                    rows.append(("spacer", ""))
                last_section = "modpack"
                rows.append(("section", f"Модпак: {self._friendly_update_status(head, 'modpack')}"))
                rows.extend(("detail", self._friendly_update_line(line)) for line in lines[1:])
            elif "Движок" in head or "Engine" in head:
                if rows:
                    rows.append(("spacer", ""))
                last_section = "engine"
                rows.append(("section", f"Движок: {self._friendly_update_status(head, 'engine')}"))
                rows.extend(("detail", self._friendly_update_line(line)) for line in lines[1:])
            elif "Файлы игры" in head or "Game files" in head:
                if rows:
                    rows.append(("spacer", ""))
                last_section = "game"
                rows.append(("section", f"Игра: {self._friendly_update_status(head, 'game')}"))
                rows.extend(("detail", self._friendly_update_line(line)) for line in lines[1:])
            else:
                prefix = "Примечание"
                if last_section == "db":
                    prefix = "Заметка DB"
                elif last_section == "modpack":
                    prefix = "Заметка модпака"
                elif last_section == "engine":
                    prefix = "Заметка движка"
                elif last_section == "game":
                    prefix = "Заметка игры"
                note = self._friendly_update_note(block)
                if note:
                    rows.append(("detail", f"{prefix}: {note}"))
        return rows or [("section", "Готово"), ("detail", "Обновления обработаны.")]

    def _repair_mojibake(self, value):
        if not isinstance(value, str) or not value:
            return value

        def score(text):
            bad = sum(text.count(token) for token in ("Р", "С", "Ð", "Ñ", "Â", "�"))
            cyr = sum(1 for char in text if "\u0400" <= char <= "\u04ff")
            return cyr * 3 - bad * 4

        current = value
        best = value
        best_score = score(value)
        for _ in range(4):
            changed = False
            for encoding in ("cp1251", "latin1"):
                try:
                    candidate = current.encode(encoding).decode("utf-8")
                except (UnicodeEncodeError, UnicodeDecodeError):
                    continue
                candidate_score = score(candidate)
                if candidate != current and candidate_score > best_score:
                    best = candidate
                    best_score = candidate_score
                    current = candidate
                    changed = True
                    break
            if not changed:
                break
        return best

    def _update_result_row_height(self, kind, text="", text_width=428):
        if kind == "section":
            return 32
        if kind == "spacer":
            return 14
        chars_per_line = max(24, int(text_width / 7))
        visual_lines = max(1, (len(str(text)) + chars_per_line - 1) // chars_per_line)
        return 22 * visual_lines + 4

    def _friendly_update_status(self, text, subject):
        text = self._repair_mojibake(text)
        lowered = text.casefold()
        if "уже" in lowered or "latest" in lowered or "up to date" in lowered:
            return "актуальна" if subject == "db" else "актуален"
        if "обнов" in lowered or "updated" in lowered or "скачан" in lowered:
            return "обновлена" if subject == "db" else "обновлён"
        return text

    def _friendly_update_line(self, line):
        line = self._repair_mojibake(line)
        if self._is_broken_text(line):
            return ""
        if line.startswith("Backup:"):
            backup_name = Path(line.split(":", 1)[1].strip()).name
            return f"Резервная копия: {backup_name}"
        replacements = {
            "Удалено лишних файлов": "Лишних файлов удалено",
            "Удалено старых файлов": "Старых файлов удалено",
            "Удалено пустых папок": "Пустых папок удалено",
            "Скачано файлов": "Файлов скачано",
            "Отдельные пакеты": "Отдельные пакеты",
            "Файлы игры": "Файлы игры",
        }
        for source, target in replacements.items():
            if line.startswith(source):
                return line.replace(source, target, 1)
        return line

    def _friendly_update_note(self, text):
        text = self._repair_mojibake(text).strip()
        if text.startswith("Backup:"):
            return ""
        if self._is_broken_text(text):
            return ""
        lowered = text.casefold().rstrip(".")
        generic_prefixes = (
            "обновление db",
            "обновление mo2",
            "обновление модпака",
            "обновление файлов игры",
            "anthology work git update",
            "modpack update",
            "db anthology update",
        )
        if any(lowered.startswith(prefix) for prefix in generic_prefixes):
            return ""
        return text

    def _is_broken_text(self, text):
        if not isinstance(text, str):
            return False
        stripped = text.strip()
        if not stripped:
            return False
        question_count = stripped.count("?")
        if question_count < 6:
            return False
        letters = sum(1 for char in stripped if char.isalpha())
        return question_count >= max(6, letters)

    def _fill_donation_body(self, body):
        body.tag_configure("intro", foreground=COLORS["text"], font=("Segoe UI", 10))
        body.tag_configure("section", foreground=COLORS["accent"], font=("Segoe UI Semibold", 12, "bold"), spacing1=10, spacing3=6)
        body.tag_configure("role", foreground=COLORS["muted"], font=("Segoe UI", 9))
        body.tag_configure("detail", foreground=COLORS["text"], font=("Segoe UI", 10), lmargin1=14, lmargin2=14)
        body.tag_configure("link", foreground="#8beedb", underline=True, font=("Segoe UI Semibold", 10, "bold"))
        body.tag_configure("note", foreground=COLORS["accent_2"], font=("Segoe UI Semibold", 10, "bold"), spacing3=12)
        link_urls = {}

        def put(text, tag="detail"):
            body.insert("end", text, tag)

        def put_link(label, url):
            put(f"{label}: ", "detail")
            start = body.index("end-1c")
            tag = f"link_{len(link_urls)}"
            body.insert("end", url, ("link", tag))
            end = body.index("end-1c")
            body.tag_add(tag, start, end)
            link_urls[tag] = url
            put("\n")

        def open_link(event):
            index = body.index(f"@{event.x},{event.y}")
            for tag in body.tag_names(index):
                if tag in link_urls:
                    webbrowser.open_new_tab(link_urls[tag])
                    return "break"
            return None

        def update_cursor(event):
            index = body.index(f"@{event.x},{event.y}")
            cursor = "hand2" if any(tag in link_urls for tag in body.tag_names(index)) else "arrow"
            body.configure(cursor=cursor)

        body.bind("<Button-1>", open_link)
        body.bind("<Motion>", update_cursor)
        body.bind("<Leave>", lambda _e: body.configure(cursor="arrow"))

        put("Реквизиты для пожертвований - Details for donations\n", "section")
        put("Это лучший человек: если бы его не было, не было бы и Anthology. Поддержите лучше его!\n", "note")

        put("@Макс Ратный\n", "section")
        put("Ведущий разработчик Anthology Creator Anthology\n", "role")
        put("Карта GAZPROM: 4249 1703 9251 4208 - Р.М.В\n")
        put("Карта СБЕРБАНК: 2202 2009 4047 5112 - Р.Г.Ю\n")
        put_link("Boosty", "https://boosty.to/maks_ratniy/donate")
        put_link("DonationAlerts", "https://www.donationalerts.com/r/maks_ratniy")

        put("\n@Шура\n", "section")
        put("Дизайнер оружия Anthology / weapon designer\n", "role")
        put("АльфаБанк: 2200 1529 8529 0975\n")
        put_link("Boosty", "https://boosty.to/chenchik.2014")

        put("\n@дедушка Ли\n", "section")
        put("Поддержка Discord и перевод на русский язык\n", "role")
        put("Kaspi KZ: 4400 4303 8621 3281 - VITALIY LI\n")
        put("ЮMoney: 2204 1201 1060 8698 - YOOMONEY VIRTUAL\n")

    def play_online(self):
        self.play()

    def play(self):
        self._prepare_game_launch()
        mo2_path = self._mod_organizer_exe()
        if not mo2_path.exists():
            messagebox.showerror(
                "Anthology Launcher",
                f"{TEXT[self.lang]['mo2_missing']}:\n{mo2_path}\n\n{TEXT[self.lang]['mo2_expected']}",
            )
            return
        try:
            self._sync_mod_organizer_paths(mo2_path)
            self._start_relay_chat_if_enabled()
            self._shell_open_file(mo2_path)
            self.destroy()
        except Exception as exc:
            messagebox.showerror("Anthology Launcher", f"{TEXT[self.lang]['launch_error']}:\n{mo2_path}\n\n{exc}")

    def play_original(self):
        self._prepare_game_launch()
        game_exe = self._selected_game_exe()
        if not game_exe.exists():
            messagebox.showerror("Anthology Launcher", f"{TEXT[self.lang]['launch_error']}:\n{game_exe}")
            return
        try:
            self._start_relay_chat_if_enabled()
            subprocess.Popen(
                [str(game_exe)],
                cwd=str(game_exe.parent),
                env=self._prepare_external_launch(),
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
            self.destroy()
        except Exception as exc:
            messagebox.showerror("Anthology Launcher", f"{TEXT[self.lang]['launch_error']}:\n{game_exe}\n\n{exc}")

    def _prepare_game_launch(self):
        self.write_config()
        self.write_commandline()
        self.apply_sound_fix()
        self._delete_shader_cache()
        if self.reset_user or not (self.root_dir / "appdata" / "user.ltx").exists():
            self.reset_user_ltx_file()


if __name__ == "__main__":
    os.chdir(app_dir())
    LauncherApp().mainloop()
