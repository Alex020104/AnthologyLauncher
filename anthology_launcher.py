import os
import hashlib
import json
import shutil
import subprocess
import stat
import sys
import tempfile
import threading
import time
import webbrowser
import zipfile
from pathlib import Path
from urllib.error import URLError
from urllib.parse import quote
from urllib.request import Request, urlopen
import tkinter as tk
from tkinter import messagebox
from PIL import Image, ImageEnhance, ImageTk


WIDTH = 1180
HEIGHT = 720
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
LAUNCHER_VERSION = "2026.05.29.6"
LAUNCHER_VERSION_URL = "https://api.github.com/repos/sysliveprime-ctrl/AnthologyLauncher/contents/launcher_version.json?ref=main"
LAUNCHER_VERSION_RAW_URL = "https://raw.githubusercontent.com/sysliveprime-ctrl/AnthologyLauncher/main/launcher_version.json"
LAUNCHER_EXE_URL = "https://github.com/sysliveprime-ctrl/AnthologyLauncher/releases/latest/download/AnomalyLauncher.exe"
LAUNCHER_EXE_NAME = "AnomalyLauncher.exe"
MOD_ORGANIZER_EXE_NAME = "ModOrganizer.exe"
ENGINE_RELEASE_VERSION = "2026.5.8-nanfix"
ENGINE_MT_URL = "https://github.com/sysliveprime-ctrl/xray-monolith/releases/download/2026.5.8-nanfix/STALKER-Anomaly-modded-exes-MT-TEST_2026.5.8-nanfix.zip"
ENGINE_VERSION_URL = "https://api.github.com/repos/sysliveprime-ctrl/xray-monolith/contents/engine_version.json?ref=anthology-2026.5.8-mt-nanfix"
ENGINE_VERSION_RAW_URL = "https://raw.githubusercontent.com/sysliveprime-ctrl/xray-monolith/anthology-2026.5.8-mt-nanfix/engine_version.json"
ENGINE_ALLOWED_PARTS = {"bin", "db"}
MODPACK_FOLDER = "SYS_A.N.T.H.O.L.O.G.Y_mo2_CBT"
MODPACK_REPO = "https://github.com/sysliveprime-ctrl/anthology-mo2-modpack"
UPDATE_VERSION_URL = "https://raw.githubusercontent.com/sysliveprime-ctrl/anthology-mo2-modpack/main/version.json"
UPDATE_VERSION_API_URL = "https://api.github.com/repos/sysliveprime-ctrl/anthology-mo2-modpack/contents/version.json?ref=main"
UPDATE_ZIP_URL = "https://github.com/sysliveprime-ctrl/anthology-mo2-modpack/archive/refs/heads/main.zip"
UPDATE_ALLOWED_PARTS = {"configs", "scripts", "textures"}
UPDATE_PRESERVE_PATH_MARKERS = (
    "r.a.k weapon pack adaptation",
)
DB_REPO = "https://github.com/sysliveprime-ctrl/anthology-db"
DB_UPDATE_VERSION_URL = "https://api.github.com/repos/sysliveprime-ctrl/anthology-db/contents/db_version.json?ref=main"
DB_UPDATE_VERSION_RAW_URL = "https://raw.githubusercontent.com/sysliveprime-ctrl/anthology-db/main/db_version.json"
DB_ALLOWED_PARTS = {"configs", "mods"}

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
        "play": "Играть с модпаком",
        "play_original": "Играть в оригинал",
        "settings": "Настройки",
        "back": "Назад",
        "save": "Сохранить",
        "cache": "Очистить кэш",
        "logs": "Открыть логи",
        "about": "О проекте",
        "support": "Поддержать проект",
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
        "label_downloaded_files": "Скачано файлов",
        "news_1": "Требуется новая игра",
        "news_1_body": "После этого обновления необходимо начать новую игру: был заменен all.spawn. Старые сохранения могут работать некорректно.",
        "news_2": "MO2 профиль Anthology 2.1",
        "news_2_body": "Основной запуск рассчитан на чистый профиль Mod Organizer 2. Сторонние аддоны лучше не ставить поверх оружейной экосистемы.",
        "news_3": "Тестовое обновление движка",
        "news_3_body": "Доступна экспериментальная версия MT-движка. Устанавливайте её только если готовы к возможной нестабильности; перед обновлением рекомендуется сохранить резервную копию.",
        "debug": "Режим отладки",
        "sound_fix": "Обход проблем со звуком",
        "prefetch": "Предзагрузка звуков",
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
        "play": "Play with modpack",
        "play_original": "Play original",
        "settings": "Settings",
        "back": "Back",
        "save": "Save",
        "cache": "Clear cache",
        "logs": "Open logs",
        "about": "About",
        "support": "Support project",
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
        "label_downloaded_files": "Downloaded files",
        "news_1": "New game required",
        "news_1_body": "This update requires starting a new game because all.spawn was replaced. Old saves may not work correctly.",
        "news_2": "MO2 Anthology 2.1 profile",
        "news_2_body": "Main startup is intended for a clean Mod Organizer 2 profile. Third-party addons should not be installed on top of the weapon ecosystem.",
        "news_3": "Experimental engine update",
        "news_3_body": "An experimental MT engine build is available. Install it only if you are ready for possible instability; making a backup before updating is recommended.",
        "debug": "Debug mode",
        "sound_fix": "Sound workaround",
        "prefetch": "Prefetch sounds",
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
        self.view_widgets = []
        self.buttons = {}
        self.render_buttons = {}
        self.toggle_items = {}

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
        bg = Image.open(self.assets / "Launcher.png").resize((WIDTH, HEIGHT), Image.Resampling.LANCZOS)
        bg = ImageEnhance.Brightness(bg).enhance(0.72)
        bg = ImageEnhance.Contrast(bg).enhance(0.96)
        self.bg_img = ImageTk.PhotoImage(bg)

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
        self.canvas = tk.Canvas(self, width=WIDTH, height=HEIGHT, highlightthickness=0, bd=0, bg=COLORS["bg"])
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

    def _clear_view(self):
        for widget in self.view_widgets:
            widget.destroy()
        self.view_widgets = []
        for item in self.items:
            self.canvas.delete(item)
        self.items = []
        self.buttons = {}
        self.render_buttons = {}
        self.toggle_items = {}

    def _add(self, item):
        self.items.append(item)
        return item

    def _add_widget(self, widget, x, y, w, h):
        self.view_widgets.append(widget)
        return self._add(self.canvas.create_window(x, y, width=w, height=h, anchor="nw", window=widget))

    def show_home(self):
        self.view = "home"
        self._clear_view()
        t = TEXT[self.lang]

        self.buttons["youtube"] = self._button(710, 118, 118, 38, "YouTube", lambda: webbrowser.open("https://youtube.com/@Sys-live-prime"))
        self.buttons["vk"] = self._button(836, 118, 118, 38, "VK", lambda: webbrowser.open("https://vk.com/club219667646"))
        self.buttons["discord"] = self._button(961, 118, 118, 38, "Discord", lambda: webbrowser.open("https://discord.gg/pZYeVxEwGc"))
        self.buttons["support"] = self._button(713, 174, 365, 38, t["support"], self.show_support)

        self._section_label(108, 206, t["news"])
        self._news_item(108, 272, t["news_1"], t["news_1_body"], width=328)
        self._news_item(108, 368, t["news_2"], t["news_2_body"], width=328)
        self._news_item(108, 476, t["news_3"], t["news_3_body"], width=328)

        self._add(self.canvas.create_line(67, 573, 1103, 573, fill=COLORS["accent"], stipple="gray50", width=2))
        self.buttons["logs"] = self._button(715, 604, 177, 38, t["logs"], self.open_logs_folder)
        self.buttons["cache"] = self._button(904, 604, 176, 38, t["cache"], self.delete_shader_cache)
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

    def _bottom_update_bar(self, t):
        self.buttons["play"] = self._button(87, 604, 260, 38, t["play"], self.play, primary=True)
        self.buttons["play_original"] = self._button(87, 654, 260, 38, t["play_original"], self.play_original, primary=True)
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

    def _button(self, x, y, w, h, text, command, primary=False):
        fill = "#173a35" if primary else COLORS["glass_lift"]
        hover = "#23544d" if primary else "#162c28"
        outline = COLORS["accent"] if primary else "#829d96"
        rect = self._add(self.canvas.create_rectangle(x, y, x + w, y + h, fill=fill, stipple="gray50", outline=outline, width=1))
        self._add(self.canvas.create_line(x + 1, y + 1, x + w - 1, y + 1, fill="#ffffff", stipple="gray50"))
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
        if event.y <= TOP_BAR and event.x < WIDTH - 112:
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
        max_x = max(0, self.winfo_screenwidth() - WIDTH)
        max_y = max(0, self.winfo_screenheight() - HEIGHT)
        return max_x, max_y

    def _clamp_window_position(self, x, y):
        max_x, max_y = self._screen_bounds()
        return max(0, min(int(x), max_x)), max(0, min(int(y), max_y))

    def _center_window(self):
        self.update_idletasks()
        x = (self.winfo_screenwidth() - WIDTH) // 2
        y = (self.winfo_screenheight() - HEIGHT) // 2
        x, y = self._clamp_window_position(x, y)
        self.geometry(f"{WIDTH}x{HEIGHT}+{x}+{y}")

    def _refresh_all(self):
        for name, btn in self.render_buttons.items():
            active = name == self.renderer
            self.canvas.itemconfig(btn["rect"], fill="#1c554d" if active else COLORS["glass_lift"], outline=COLORS["accent"] if active else "#829d96")

        values = {
            "debug": self.debug,
            "sound_fix": self.sound_fix,
            "prefetch": self.prefetch,
            "reset": self.reset_user,
            "avx": self.avx,
        }
        for key, item in self.toggle_items.items():
            active = values[key]
            x, y = item["x"], item["y"]
            self.canvas.itemconfig(item["box"], fill="#1c554d" if active else COLORS["glass_lift"], outline=COLORS["accent"] if active else "#829d96")
            self.canvas.coords(item["knob"], x + (29 if active else 5), y + 5, x + (43 if active else 19), y + 19)
            self.canvas.itemconfig(item["knob"], fill="#e8fff7" if active else COLORS["faint"])
        if hasattr(self, "shadow_value"):
            self.canvas.itemconfig(self.shadow_value, text=str(SHADOWS[self.shadow]))

    def toggle_language(self):
        self.lang = "en" if self.lang == "ru" else "ru"
        if self.view == "settings":
            self.show_settings()
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

    def write_config(self):
        lines = [
            self.renderer,
            "DBG" if self.debug else "NODBG",
            str(self.shadow),
            "SNDFIX" if self.sound_fix else "NOSNDFIX",
            "SNDPREFETCH" if self.prefetch else "NOSNDPREFETCH",
            "EN" if self.lang == "en" else "RU",
            "AVX" if self.avx else "NOAVX",
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

    def delete_shader_cache(self):
        cache = self.root_dir / "appdata" / "shaders_cache"
        if not cache.exists():
            messagebox.showinfo("Anthology Launcher", TEXT[self.lang]["cache_missing"])
            return
        shutil.rmtree(cache)
        messagebox.showinfo("Anthology Launcher", TEXT[self.lang]["cache_done"])

    def open_logs_folder(self):
        logs = self.root_dir / "appdata" / "logs"
        logs.mkdir(parents=True, exist_ok=True)
        if sys.platform == "win32":
            os.startfile(logs)
        else:
            webbrowser.open(logs.as_uri())

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
            self._debug_log("sync-update: modpack folder missing, DB-only update mode")
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
            if not mods_dir.exists():
                return False, self._modpack_missing_message(mods_dir)

            remote = self._download_update_version()
            local = self._load_update_state(mods_dir)
            remote_version = str(remote.get("version", "")).strip()
            if not remote_version:
                return False, f"{t['update_failed']}:\nversion.json has no version"
            local_version = str(local.get("version", "")).strip()
            needs_repair = self._modpack_needs_repair(mods_dir, local)
            needs_manifest_bootstrap = local_version == remote_version and not self._state_file_list(local)
            manifest_removed_files = self._manifest_removed_files(remote)
            needs_manifest_cleanup = any((mods_dir / rel).is_file() for rel in manifest_removed_files)
            if local_version == remote_version and not needs_repair and not needs_manifest_bootstrap and not needs_manifest_cleanup:
                return True, f"{t['update_latest']}\n\n{t['label_version']}: {remote_version}"

            status_text = t["update_available_downloading"]
            if local_version == remote_version and (needs_repair or needs_manifest_bootstrap or needs_manifest_cleanup):
                status_text = t["update_repair"]
            self.after(0, lambda s=status_text: self._set_update_status(s, COLORS["accent_2"]))
            zip_url = remote.get("zip_url") or UPDATE_ZIP_URL
            tmp_dir = self.root_dir / "webcache" / "launcher_update"
            self._reset_directory(tmp_dir)
            log_path = tmp_dir / "update.log"
            self._write_update_log(log_path, f"mods_dir={mods_dir}")
            self._write_update_log(log_path, f"remote_version={remote_version} local_version={local_version} needs_repair={needs_repair}")
            zip_path = tmp_dir / "update.zip"
            self._write_update_log(log_path, f"download={zip_url}")
            self._download_update_archive(zip_url, zip_path, attempts=5, timeout=300)
            self._write_update_log(log_path, f"downloaded={zip_path} size={zip_path.stat().st_size}")

            self.after(0, lambda: self._set_update_status(t["update_applying"], COLORS["accent_2"]))
            self.after(0, lambda: self._set_update_progress(0, "0%"))
            with zipfile.ZipFile(zip_path, "r") as archive:
                installed_files = self._install_update_archive(archive, mods_dir, log_path)
            deleted_files = self._remove_stale_update_files(mods_dir, self._state_file_list(local), installed_files, log_path)
            deleted_files += self._remove_manifest_files(mods_dir, manifest_removed_files, log_path)
            self._save_update_state(mods_dir, remote, installed_files)
            self._write_update_log(log_path, "state saved")
            shutil.rmtree(tmp_dir, ignore_errors=True)

            notes = remote.get("notes", "")
            message = f"{t['update_done']}\n\n{t['label_version']}: {remote_version}"
            if deleted_files:
                message += f"\n{t['label_removed_old_files']}: {deleted_files}"
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
        if not mods_dir.exists():
            return False

        remote = self._download_update_version()
        remote_version = str(remote.get("version", "")).strip()
        if not remote_version:
            return False

        local = self._load_update_state(mods_dir)
        local_version = str(local.get("version", "")).strip()
        if remote_version != local_version:
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
            entries = self._db_manifest_entries(remote)
            remote_version = str(remote.get("version", "")).strip()
            if not remote_version:
                return False, f"{t['db_failed']}:\n{t['db_no_version']}"
            if not entries:
                return False, f"{t['db_failed']}:\n{t['db_no_files']}"

            self.after(0, lambda: self._set_update_status(t["db_removing_extra"], COLORS["accent_2"]))
            deleted = self._mirror_db_archives(entries, log_path)

            changed = self._db_files_needing_download(entries, log_path)
            if not changed:
                self._save_db_update_state(remote)
                message = f"{t['db_latest']}\n\n{t['label_version']}: {remote_version}\n{t['label_removed_files']}: {deleted}"
                return True, message

            total = len(changed)
            self._write_update_log(log_path, f"download files={total}")
            for index, entry in enumerate(changed, start=1):
                rel = entry["path"]
                target = self.root_dir / rel
                tmp_file = tmp_dir / (target.name + ".download")
                url = self._db_entry_url(remote, entry)
                self.after(0, lambda i=index, n=total, p=rel: self._set_update_status(f"{t['db_downloading']} {i}/{n} {Path(p).name}", COLORS["accent_2"]))
                self._write_update_log(log_path, f"download {index}/{total}: {url} -> {target}")
                self._download_update_archive(url, tmp_file)
                self._verify_db_file(tmp_file, entry)
                target.parent.mkdir(parents=True, exist_ok=True)
                self._make_writable(target)
                shutil.move(str(tmp_file), str(target))
                value = int(index * 100 / total)
                self.after(0, lambda v=value: self._set_update_progress(v, f"{v}%"))

            self._save_db_update_state(remote)
            notes = str(remote.get("notes", "")).strip()
            message = f"{t['db_done']}\n\n{t['label_version']}: {remote_version}\n{t['label_downloaded_files']}: {total}\n{t['label_removed_files']}: {deleted}"
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
        except Exception:
            url = f"{UPDATE_VERSION_URL}?t={int(time.time())}"
            with urlopen(url, timeout=30) as response:
                return json.loads(response.read().decode("utf-8-sig"))

    def _download_db_update_version(self):
        try:
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
        except Exception:
            url = f"{DB_UPDATE_VERSION_RAW_URL}?t={int(time.time())}"
            with urlopen(url, timeout=30) as response:
                return json.loads(response.read().decode("utf-8-sig"))

    def _download_engine_version(self):
        try:
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
        except Exception:
            url = f"{ENGINE_VERSION_RAW_URL}?t={int(time.time())}"
            with urlopen(url, timeout=20) as response:
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
        except Exception:
            url = f"{LAUNCHER_VERSION_RAW_URL}?t={int(time.time())}"
            with urlopen(url, timeout=20) as response:
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
            self._download_update_archive(url, new_exe)
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

    def _remove_stale_update_files(self, mods_dir, previous_files, current_files, log_path=None):
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

    def _remove_manifest_files(self, mods_dir, files, log_path=None):
        deleted = 0
        for rel in files:
            target = mods_dir / rel
            try:
                if not target.is_file() or not self._is_relative_to(target.resolve(), mods_dir.resolve()):
                    continue
                self._make_writable(target)
                target.unlink()
                deleted += 1
                if log_path:
                    self._write_update_log(log_path, f"delete manifest removed_file: {target}")
            except OSError as exc:
                if log_path:
                    self._write_update_log(log_path, f"delete manifest removed_file failed: {target}: {exc}")
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
        lowered = [part.lower() for part in parts]
        if "gamedata" not in lowered:
            return False
        index = lowered.index("gamedata")
        return index + 1 < len(parts) and lowered[index + 1] in UPDATE_ALLOWED_PARTS

    def _should_preserve_update_path(self, path):
        normalized = Path(path).as_posix().casefold()
        return any(marker in normalized for marker in UPDATE_PRESERVE_PATH_MARKERS)

    def _db_state_path(self):
        return self.root_dir / "webcache" / "db_update" / "db_state.json"

    def _save_db_update_state(self, remote):
        state = {
            "version": str(remote.get("version", "")).strip(),
            "repo": DB_REPO,
            "updated_at": time.strftime("%Y-%m-%d %H:%M"),
        }
        path = self._db_state_path()
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")

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

    def _normalize_db_manifest_path(self, value):
        if not isinstance(value, str):
            return None
        parts = Path(value.replace("\\", "/")).parts
        if len(parts) < 3 or parts[0].lower() != "db":
            return None
        if parts[1].lower() not in DB_ALLOWED_PARTS:
            return None
        if any(part in ("", ".", "..") for part in parts):
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

    def _mirror_db_archives(self, entries, log_path=None):
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
                if rel in allowed:
                    continue
                self._make_writable(path)
                path.unlink()
                deleted += 1
                if log_path:
                    self._write_update_log(log_path, f"delete extra: {path}")
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
        if not ok:
            return [("section", "Ошибка"), ("body", message.strip() or "Неизвестная ошибка")]

        rows = []
        blocks = [block.strip() for block in message.split("\n\n") if block.strip()]
        last_section = None
        for block in blocks:
            lines = [line.strip() for line in block.splitlines() if line.strip()]
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
            else:
                prefix = "Примечание"
                if last_section == "db":
                    prefix = "Заметка DB"
                elif last_section == "modpack":
                    prefix = "Заметка модпака"
                elif last_section == "engine":
                    prefix = "Заметка движка"
                note = self._friendly_update_note(block)
                if note:
                    rows.append(("detail", f"{prefix}: {note}"))
        return rows or [("section", "Готово"), ("detail", "Обновления обработаны.")]

    def _update_result_row_height(self, kind, text="", text_width=428):
        if kind == "section":
            return 32
        if kind == "spacer":
            return 14
        chars_per_line = max(24, int(text_width / 7))
        visual_lines = max(1, (len(str(text)) + chars_per_line - 1) // chars_per_line)
        return 22 * visual_lines + 4

    def _friendly_update_status(self, text, subject):
        lowered = text.casefold()
        if "уже" in lowered or "latest" in lowered or "up to date" in lowered:
            return "актуальна" if subject == "db" else "актуален"
        if "обнов" in lowered or "updated" in lowered:
            return "обновлена" if subject == "db" else "обновлён"
        return text

    def _friendly_update_line(self, line):
        if line.startswith("Backup:"):
            backup_name = Path(line.split(":", 1)[1].strip()).name
            return f"Резервная копия: {backup_name}"
        replacements = {
            "Удалено лишних файлов": "Лишних файлов удалено",
            "Удалено старых файлов": "Старых файлов удалено",
            "Скачано файлов": "Файлов скачано",
        }
        for source, target in replacements.items():
            if line.startswith(source):
                return line.replace(source, target, 1)
        return line

    def _friendly_update_note(self, text):
        if text.startswith("Backup:"):
            return ""
        return text

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

        put("\n@ANTHOLOGY | SYS\n", "section")
        put("Разработчик модпака и техническая поддержка / modpack developer and technical support\n", "role")
        put_link("YouTube", "https://youtube.com/@Sys-live-prime")
        put_link("Twitch", "https://twitch.tv/sysliveprime")
        put_link("VK Live", "https://live.vkvideo.ru/sys_live_prime")
        put_link("DonatePay", "https://donatepay.ru/don/1479286")
        put_link("DonationAlerts", "https://donationalerts.com/r/sys_live_prime")
        put_link("Boosty", "https://boosty.to/sys.live.prime")
        put("Сбербанк: +7 950 739 99 51 - Дмитрий К.\n")

        put("\n@Шура\n", "section")
        put("Дизайнер оружия Anthology / weapon designer\n", "role")
        put("АльфаБанк: 2200 1529 8529 0975\n")
        put_link("Boosty", "https://boosty.to/chenchik.2014")

        put("\n@дедушка Ли\n", "section")
        put("Поддержка Discord и перевод на русский язык\n", "role")
        put("Kaspi KZ: 4400 4303 8621 3281 - VITALIY LI\n")
        put("ЮMoney: 2204 1201 1060 8698 - YOOMONEY VIRTUAL\n")

        put("\n@Патриарх Кирилл\n", "section")
        put("Комьюнити менеджер\n", "role")
        put_link("Boosty", "https://boosty.to/kirill.dmitrov")
        put("Сбербанк: 2202 2088 4315 3975\n")

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
        if self.reset_user or not (self.root_dir / "appdata" / "user.ltx").exists():
            self.reset_user_ltx_file()


if __name__ == "__main__":
    os.chdir(app_dir())
    LauncherApp().mainloop()
