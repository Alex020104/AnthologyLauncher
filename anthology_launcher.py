import os
import json
import shutil
import subprocess
import stat
import sys
import threading
import time
import webbrowser
import zipfile
from pathlib import Path
from urllib.error import URLError
from urllib.request import urlopen
import tkinter as tk
from tkinter import messagebox
from PIL import Image, ImageEnhance, ImageTk


WIDTH = 1180
HEIGHT = 720
TOP_BAR = 72
MARGIN = 64
RENDERERS = ["DX11", "DX10", "DX9", "DX8"]
RENDER_LABELS = {
    "DX11": "DirectX 11 / R4",
    "DX10": "DirectX 10 / R2",
    "DX9": "DirectX 9 / R1",
    "DX8": "DirectX 8 / R0",
}
SHADOWS = [1536, 2048, 2560, 3072, 4096]
LAUNCHER_VERSION = "2026.05.24.3"
LAUNCHER_VERSION_URL = "https://raw.githubusercontent.com/sysliveprime-ctrl/AnthologyLauncher/main/launcher_version.json"
LAUNCHER_EXE_URL = "https://github.com/sysliveprime-ctrl/AnthologyLauncher/releases/latest/download/AnomalyLauncher.exe"
LAUNCHER_EXE_NAME = "AnomalyLauncher.exe"
MODPACK_FOLDER = "SYS_A.N.T.H.O.L.O.G.Y_mo2_CBT"
MODPACK_REPO = "https://github.com/sysliveprime-ctrl/anthology-mo2-modpack"
UPDATE_VERSION_URL = "https://raw.githubusercontent.com/sysliveprime-ctrl/anthology-mo2-modpack/main/version.json"
UPDATE_ZIP_URL = "https://github.com/sysliveprime-ctrl/anthology-mo2-modpack/archive/refs/heads/main.zip"
UPDATE_ALLOWED_PARTS = {"configs", "scripts"}

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
        "play": "Играть",
        "settings": "Настройки",
        "back": "Назад",
        "save": "Сохранить",
        "cache": "Очистить кэш",
        "about": "О проекте",
        "support": "Поддержать проект",
        "quit": "Выход",
        "news": "Новости проекта",
        "update": "Центр обновлений",
        "ready": "Готово к запуску",
        "build": "ANTHOLOGY 2.1 OBT",
        "channel": "Open Beta",
        "server": "Сервер обновлений будет подключен позже",
        "update_button": "Синхронизация",
        "update_ready": "Готово к проверке обновлений",
        "update_checking": "Проверка версии...",
        "update_downloading": "Скачивание обновления...",
        "update_applying": "Установка обновления...",
        "update_preparing": "Подготовка файлов...",
        "update_done": "Модпак обновлен.",
        "update_latest": "Уже установлена последняя версия.",
        "update_missing": "Не найдена папка модпака",
        "update_expected": "Папка модпака должна лежать рядом с папкой игры",
        "update_failed": "Не удалось обновить модпак",
        "news_1": "Подготовка к открытому тестированию",
        "news_1_body": "Сборка готовится к ОБТ. Сейчас лаунчер запускает игру, хранит локальные настройки и подготовлен под будущий сервер обновлений.",
        "news_2": "MO2 профиль Anthology 2.1",
        "news_2_body": "Основной запуск рассчитан на чистый профиль Mod Organizer 2. Сторонние аддоны лучше не ставить поверх оружейной экосистемы.",
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
    },
    "en": {
        "play": "Play",
        "settings": "Settings",
        "back": "Back",
        "save": "Save",
        "cache": "Clear cache",
        "about": "About",
        "support": "Support project",
        "quit": "Exit",
        "news": "Project news",
        "update": "Update center",
        "ready": "Ready to launch",
        "build": "ANTHOLOGY 2.1 OBT",
        "channel": "Open Beta",
        "server": "Update server will be connected later",
        "update_button": "Sync",
        "update_ready": "Ready to check updates",
        "update_checking": "Checking version...",
        "update_downloading": "Downloading update...",
        "update_applying": "Applying update...",
        "update_preparing": "Preparing files...",
        "update_done": "Modpack updated.",
        "update_latest": "The latest version is already installed.",
        "update_missing": "Modpack folder was not found",
        "update_expected": "The modpack folder must be next to the game folder",
        "update_failed": "Failed to update modpack",
        "news_1": "Open beta preparation",
        "news_1_body": "The build is preparing for OBT. The launcher starts the game, stores local settings, and is ready for a future update server.",
        "news_2": "MO2 Anthology 2.1 profile",
        "news_2_body": "Main startup is intended for a clean Mod Organizer 2 profile. Third-party addons should not be installed on top of the weapon ecosystem.",
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
        super().__init__()
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
        self.view = "home"
        self.updating = False
        self.update_status_item = None
        self.update_progress_bg = None
        self.update_progress_fill = None
        self.update_progress_text = None
        self.items = []
        self.view_widgets = []
        self.buttons = {}
        self.render_buttons = {}
        self.toggle_items = {}

        self.overrideredirect(True)
        self.geometry(f"{WIDTH}x{HEIGHT}+120+80")
        self.resizable(False, False)
        self.configure(bg=COLORS["bg"])

        icon = self.assets / "a.ico"
        if icon.exists():
            self.iconbitmap(str(icon))

        self._load_config()
        self._load_background()
        self._build_base()
        self.show_home()
        self.after(1500, self.check_launcher_update_async)

    def _load_background(self):
        bg = Image.open(self.assets / "Launcher.png").resize((WIDTH, HEIGHT), Image.Resampling.LANCZOS)
        bg = ImageEnhance.Brightness(bg).enhance(0.72)
        bg = ImageEnhance.Contrast(bg).enhance(0.96)
        self.bg_img = ImageTk.PhotoImage(bg)

    def _build_base(self):
        self.canvas = tk.Canvas(self, width=WIDTH, height=HEIGHT, highlightthickness=0, bd=0, bg=COLORS["bg"])
        self.canvas.pack(fill="both", expand=True)
        self.canvas.create_image(0, 0, image=self.bg_img, anchor="nw")
        self.canvas.create_rectangle(0, 0, WIDTH, HEIGHT, fill="#020504", stipple="gray50", outline="")
        self.canvas.create_rectangle(0, 0, WIDTH, HEIGHT, outline="#2c4942", width=1)
        self.canvas.create_rectangle(0, 0, WIDTH, TOP_BAR, fill=COLORS["glass"], stipple="gray50", outline="")
        self.canvas.create_line(MARGIN, TOP_BAR - 1, WIDTH - MARGIN, TOP_BAR - 1, fill="#9ee9dc")
        self.canvas.create_text(MARGIN, 36, text="ANTHOLOGY", anchor="w", fill=COLORS["text"], font=("Segoe UI Semibold", 14, "bold"))
        self.canvas.create_text(MARGIN + 128, 37, text="LAUNCHER", anchor="w", fill=COLORS["muted"], font=("Segoe UI", 9))
        self.canvas.create_rectangle(MARGIN + 216, 25, MARGIN + 280, 47, fill=COLORS["glass_lift"], stipple="gray50", outline="#6d8982")
        self.canvas.create_text(MARGIN + 248, 36, text="2.1 OBT", anchor="center", fill=COLORS["accent_2"], font=("Segoe UI Semibold", 8, "bold"))

        self.min_btn = self.canvas.create_text(WIDTH - 74, 36, text="-", fill=COLORS["muted"], font=("Segoe UI", 18))
        self.close_btn = self.canvas.create_text(WIDTH - 38, 36, text="x", fill=COLORS["muted"], font=("Segoe UI", 13, "bold"))
        self.canvas.tag_bind(self.min_btn, "<Button-1>", lambda _e: self.iconify())
        self.canvas.tag_bind(self.close_btn, "<Button-1>", lambda _e: self.destroy())
        self.canvas.tag_bind("all", "<ButtonPress-1>", self._start_drag)
        self.canvas.tag_bind("all", "<B1-Motion>", self._drag)

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

        self.buttons["youtube"] = self._button(790, 92, 108, 34, "YouTube", lambda: webbrowser.open("https://youtube.com/@Sys-live-prime"))
        self.buttons["vk"] = self._button(910, 92, 96, 34, "VK", lambda: webbrowser.open("https://vk.com/club219667646"))
        self.buttons["discord"] = self._button(1018, 92, 108, 34, "Discord", lambda: webbrowser.open("https://discord.gg/pZYeVxEwGc"))
        self.buttons["support"] = self._button(790, 140, 336, 36, t["support"], self.show_support)

        self._panel(64, 132, 404, 356, alpha="frameless")
        self._section_label(104, 174, t["news"])
        self._news_item(104, 230, t["news_1"], t["news_1_body"], width=312)
        self._add(self.canvas.create_line(104, 322, 428, 322, fill="#ffffff", stipple="gray25"))
        self._news_item(104, 366, t["news_2"], t["news_2_body"], width=312)

        self.buttons["settings"] = self._button(964, 516, 164, 42, t["settings"], self.show_settings)
        self.buttons["about"] = self._button(802, 516, 142, 42, t["about"], self.about)

        self._bottom_update_bar(t)
        self.flag_id = self._add(self.canvas.create_image(748, 526, anchor="nw", image=self.flag_us if self.lang == "ru" else self.flag_ru))
        self.canvas.tag_bind(self.flag_id, "<Button-1>", lambda _e: self.toggle_language())

    def show_settings(self):
        self.view = "settings"
        self._clear_view()
        t = TEXT[self.lang]

        self._panel(64, 92, 1052, 536, alpha="solid")
        self._section_label(104, 134, t["settings"])
        self.buttons["back"] = self._button(966, 116, 110, 36, t["back"], self.show_home)

        self._add(self.canvas.create_text(104, 190, text=t["renderer"], anchor="w", fill=COLORS["muted"], font=("Segoe UI", 10)))
        x = 104
        for renderer in RENDERERS:
            self.render_buttons[renderer] = self._button(x, 222, 184, 42, RENDER_LABELS[renderer], lambda r=renderer: self._set_renderer(r))
            x += 202

        self.toggle_items = {
            "debug": self._toggle(104, 330, t["debug"], lambda: self._flip("debug")),
            "sound_fix": self._toggle(104, 384, t["sound_fix"], lambda: self._flip("sound_fix")),
            "prefetch": self._toggle(104, 438, t["prefetch"], lambda: self._flip("prefetch")),
            "reset": self._toggle(574, 330, t["reset"], lambda: self._flip("reset_user")),
            "avx": self._toggle(574, 384, t["avx"], lambda: self._flip("avx")),
        }

        self._add(self.canvas.create_text(574, 450, text=t["shadow"], anchor="w", fill=COLORS["muted"], font=("Segoe UI", 10)))
        self.shadow_value = self._add(self.canvas.create_text(724, 450, text=str(SHADOWS[self.shadow]), anchor="w", fill=COLORS["text"], font=("Segoe UI Semibold", 15, "bold")))
        self.buttons["shadow_minus"] = self._button(574, 486, 74, 40, "<", self._shadow_prev)
        self.buttons["shadow_plus"] = self._button(662, 486, 74, 40, ">", self._shadow_next)
        self.buttons["save"] = self._button(104, 552, 190, 48, t["save"], self.save_settings, primary=True)

        self._refresh_all()

    def show_support(self):
        self.view = "support"
        self._clear_view()
        t = TEXT[self.lang]

        self._panel(64, 92, 1052, 536, alpha="solid")
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
        y = 616
        self.buttons["play"] = self._button(64, y, 178, 46, t["play"], self.play, primary=True)
        self.buttons["cache"] = self._button(258, y, 180, 46, t["cache"], self.delete_shader_cache)
        self._panel(462, y, 666, 46, alpha="bar")
        self.buttons["update"] = self._button(936, y + 7, 166, 32, t["update_button"], self.sync_modpack_update)
        self._add(self.canvas.create_text(488, y + 15, text=t["update"].upper(), anchor="w", fill=COLORS["accent"], font=("Segoe UI Semibold", 8, "bold")))
        self.update_status_item = self._add(self.canvas.create_text(488, y + 32, text=t["update_ready"], anchor="w", fill=COLORS["muted"], font=("Segoe UI", 8)))
        self.update_progress_bg = self._add(self.canvas.create_rectangle(676, y + 20, 914, y + 27, fill="#091211", outline="#476760"))
        self.update_progress_fill = self._add(self.canvas.create_rectangle(676, y + 20, 676, y + 27, fill=COLORS["accent"], outline=""))
        self.update_progress_text = self._add(self.canvas.create_text(795, y + 33, text="", anchor="center", fill=COLORS["muted"], font=("Segoe UI", 7)))
        self._set_update_progress(0, "")

    def _panel(self, x, y, w, h, alpha="solid"):
        fill = {"solid": COLORS["glass"], "light": COLORS["glass_soft"], "bar": COLORS["glass"], "frameless": COLORS["glass_soft"]}[alpha]
        outline = {"solid": "#8ed6c9", "light": "#7ab6aa", "bar": "#63847d", "frameless": ""}[alpha]
        self._add(self.canvas.create_rectangle(x + 10, y + 12, x + w + 10, y + h + 12, fill="#010302", stipple="gray50", outline=""))
        stipple = {"solid": "gray50", "light": "gray25", "bar": "gray50", "frameless": "gray25"}[alpha]
        kwargs = {"fill": fill, "outline": outline, "width": 1}
        if stipple:
            kwargs["stipple"] = stipple
        self._add(self.canvas.create_rectangle(x, y, x + w, y + h, **kwargs))
        if alpha != "frameless":
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
        label = self._add(self.canvas.create_text(x + w / 2, y + h / 2, text=text, fill=COLORS["text"], font=("Segoe UI Semibold", 15 if primary else 10, "bold")))
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
        if event.y <= TOP_BAR:
            self.drag_x = event.x
            self.drag_y = event.y

    def _drag(self, event):
        if self.drag_y <= TOP_BAR:
            self.geometry(f"+{self.winfo_x() + event.x - self.drag_x}+{self.winfo_y() + event.y - self.drag_y}")

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

    def save_settings(self):
        self.write_config()
        messagebox.showinfo("Anthology Launcher", TEXT[self.lang]["saved"])

    def about(self):
        messagebox.showinfo("Anthology Launcher", "Anthology Launcher\nModern Python UI\nA.N.T.H.O.L.O.G.Y 2.1 OBT")

    def support_project(self):
        self.show_support()

    def _prepare_external_launch(self):
        env = os.environ.copy()
        for key in ("_PYI_APPLICATION_HOME_DIR", "_PYI_ARCHIVE_FILE", "_PYI_PARENT_PROCESS_LEVEL", "_MEIPASS2"):
            env.pop(key, None)
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
            self.canvas.itemconfig(self.update_status_item, text=text, fill=color or COLORS["muted"])

    def _set_update_progress(self, value, text=None):
        if not self.update_progress_bg or not self.update_progress_fill:
            return
        value = max(0, min(100, int(value)))
        x1, y1, x2, y2 = self.canvas.coords(self.update_progress_bg)
        fill_x = x1 + ((x2 - x1) * value / 100.0)
        self.canvas.coords(self.update_progress_fill, x1, y1, fill_x, y2)
        self.canvas.itemconfig(self.update_progress_fill, state="normal" if value > 0 else "hidden")
        if self.update_progress_text is not None:
            self.canvas.itemconfig(self.update_progress_text, text=text if text is not None else (f"{value}%" if value else ""))

    def sync_modpack_update(self):
        if self.updating:
            return
        self.updating = True
        self._set_update_status(TEXT[self.lang]["update_checking"], COLORS["accent_2"])
        self._set_update_progress(0, "")
        threading.Thread(target=self._sync_modpack_update_worker, daemon=True).start()

    def _sync_modpack_update_worker(self):
        t = TEXT[self.lang]
        log_path = None
        try:
            mods_dir = self._modpack_mods_dir()
            if not mods_dir.exists():
                self.after(0, lambda: self._finish_git_update(False, self._modpack_missing_message(mods_dir)))
                return

            remote = self._download_update_version()
            local = self._load_update_state(mods_dir)
            remote_version = str(remote.get("version", "")).strip()
            if not remote_version:
                self.after(0, lambda: self._finish_git_update(False, f"{t['update_failed']}:\nversion.json has no version"))
                return
            if str(local.get("version", "")).strip() == remote_version:
                self.after(0, lambda: self._finish_git_update(True, f"{t['update_latest']}\n\nVersion: {remote_version}"))
                return

            self.after(0, lambda: self._set_update_status(t["update_downloading"], COLORS["accent_2"]))
            zip_url = remote.get("zip_url") or UPDATE_ZIP_URL
            tmp_dir = self.root_dir / "webcache" / "launcher_update"
            if tmp_dir.exists():
                shutil.rmtree(tmp_dir, ignore_errors=True)
            tmp_dir.mkdir(parents=True, exist_ok=True)
            log_path = tmp_dir / "update.log"
            self._write_update_log(log_path, f"mods_dir={mods_dir}")
            zip_path = tmp_dir / "update.zip"
            self._write_update_log(log_path, f"download={zip_url}")
            self._download_update_archive(zip_url, zip_path)
            self._write_update_log(log_path, f"downloaded={zip_path} size={zip_path.stat().st_size}")

            self.after(0, lambda: self._set_update_status(t["update_applying"], COLORS["accent_2"]))
            self.after(0, lambda: self._set_update_progress(0, "0%"))
            with zipfile.ZipFile(zip_path, "r") as archive:
                self._install_update_archive(archive, mods_dir, log_path)
            self._save_update_state(mods_dir, remote)
            self._write_update_log(log_path, "state saved")
            shutil.rmtree(tmp_dir, ignore_errors=True)

            notes = remote.get("notes", "")
            message = f"{t['update_done']}\n\nVersion: {remote_version}"
            if notes:
                message += f"\n\n{notes}"
            self.after(0, lambda: self._finish_git_update(True, message))
        except (URLError, OSError, zipfile.BadZipFile, ValueError) as exc:
            message = f"{t['update_failed']}:\n{exc}"
            if log_path:
                self._write_update_log(log_path, f"ERROR={exc}")
            self.after(0, lambda m=message: self._finish_git_update(False, m))
        except Exception as exc:
            message = f"{t['update_failed']}:\n{exc}"
            if log_path:
                self._write_update_log(log_path, f"ERROR={exc}")
            self.after(0, lambda m=message: self._finish_git_update(False, m))

    def _download_update_version(self):
        url = f"{UPDATE_VERSION_URL}?t={int(time.time())}"
        with urlopen(url, timeout=30) as response:
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
        url = f"{LAUNCHER_VERSION_URL}?t={int(time.time())}"
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
        notes = str(remote.get("notes", "")).strip()
        message = f"Доступна новая версия лаунчера: {remote_version}\nТекущая версия: {LAUNCHER_VERSION}\n\nОбновить сейчас?"
        if notes:
            message += f"\n\n{notes}"
        if messagebox.askyesno("Anthology Launcher", message):
            threading.Thread(target=self._install_launcher_update_worker, args=(remote,), daemon=True).start()

    def _install_launcher_update_worker(self, remote):
        try:
            url = remote.get("exe_url") or LAUNCHER_EXE_URL
            tmp_dir = self.root_dir / "webcache" / "launcher_self_update"
            if tmp_dir.exists():
                shutil.rmtree(tmp_dir, ignore_errors=True)
            tmp_dir.mkdir(parents=True, exist_ok=True)
            new_exe = tmp_dir / LAUNCHER_EXE_NAME
            self.after(0, lambda: self._set_update_status("Обновление лаунчера...", COLORS["accent_2"]))
            self.after(0, lambda: self._set_update_progress(0, "0%"))
            self._download_update_archive(url, new_exe)
            self.after(0, lambda: self._restart_with_launcher_update(new_exe))
        except Exception as exc:
            self.after(0, lambda e=exc: messagebox.showerror("Anthology Launcher", f"Не удалось обновить лаунчер:\n{e}"))

    def _restart_with_launcher_update(self, new_exe):
        current_exe = Path(sys.executable).resolve()
        if current_exe.name.lower() != LAUNCHER_EXE_NAME.lower():
            current_exe = current_exe.with_name(LAUNCHER_EXE_NAME)
        updater = new_exe.parent / "apply_launcher_update.bat"
        lines = [
            "@echo off",
            "chcp 65001 >nul",
            f"set \"SRC={new_exe}\"",
            f"set \"DST={current_exe}\"",
            f"set \"PID={os.getpid()}\"",
            ":wait_loop",
            "tasklist /FI \"PID eq %PID%\" | find \"%PID%\" >nul",
            "if not errorlevel 1 (",
            "  timeout /t 1 /nobreak >nul",
            "  goto wait_loop",
            ")",
            "copy /Y \"%SRC%\" \"%DST%\" >nul",
            "start \"\" \"%DST%\"",
            "del \"%~f0\"",
        ]
        updater.write_text("\r\n".join(lines) + "\r\n", encoding="utf-8")
        subprocess.Popen(["cmd.exe", "/c", "start", "", str(updater)], cwd=str(self.root_dir))
        self.destroy()

    def _download_update_archive(self, url, path, attempts=3):
        last_error = None
        for attempt in range(1, attempts + 1):
            try:
                self._download_update_archive_once(url, path)
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
                self.after(0, lambda s=status: self._set_update_status(s, COLORS["accent_2"]))
                time.sleep(1.0)
        raise last_error

    def _download_update_archive_once(self, url, path):
        last = {"value": -1}
        with urlopen(url, timeout=60) as response, path.open("wb") as target:
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
                    self.after(0, lambda v=value: self._set_update_progress(v, f"{v}%"))

    def _state_path(self, mods_dir):
        return mods_dir.parent / ".launcher_update_state.json"

    def _load_update_state(self, mods_dir):
        path = self._state_path(mods_dir)
        if not path.exists():
            return {}
        try:
            return json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return {}

    def _save_update_state(self, mods_dir, remote):
        state = {
            "version": str(remote.get("version", "")).strip(),
            "repo": MODPACK_REPO,
        }
        self._state_path(mods_dir).write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")

    def _write_update_log(self, path, text):
        try:
            path.parent.mkdir(parents=True, exist_ok=True)
            with path.open("a", encoding="utf-8") as handle:
                handle.write(text + "\n")
        except OSError:
            pass

    def _install_update_archive(self, archive, dst, log_path=None):
        self.after(0, lambda: self._set_update_status(TEXT[self.lang]["update_preparing"], COLORS["accent_2"]))
        files = [info for info in archive.infolist() if not info.is_dir() and self._archive_update_relative(info.filename)]
        total = max(1, len(files))
        if not files:
            raise ValueError("Update archive has no configs/scripts files")
        if log_path:
            self._write_update_log(log_path, f"install files={len(files)}")
        self.after(0, lambda: self._set_update_status(TEXT[self.lang]["update_applying"], COLORS["accent_2"]))
        for index, info in enumerate(files, start=1):
            relative = self._archive_update_relative(info.filename)
            target_path = dst / relative
            if log_path and (index == 1 or index == total or index % 50 == 0):
                self._write_update_log(log_path, f"copy {index}/{total}: {target_path}")
            target_path.parent.mkdir(parents=True, exist_ok=True)
            self._make_writable(target_path)
            with archive.open(info, "r") as source, target_path.open("wb") as target:
                shutil.copyfileobj(source, target, length=1024 * 1024)
            if index == total or index % 10 == 0:
                value = 50 + int(index * 50 / total)
                self.after(0, lambda v=value: self._set_update_progress(v, f"{v}%"))

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

    def _make_writable(self, path):
        if not path.exists():
            return
        try:
            os.chmod(path, stat.S_IWRITE | stat.S_IREAD)
        except OSError:
            pass

    def _finish_git_update(self, ok, message):
        self.updating = False
        self._set_update_status(TEXT[self.lang]["update_done" if ok else "update_failed"], COLORS["accent"] if ok else COLORS["danger"])
        self._set_update_progress(100 if ok else 0, "100%" if ok else "")
        box = messagebox.showinfo if ok else messagebox.showerror
        box("Anthology Launcher", message)

    def _fill_donation_body(self, body):
        body.tag_configure("intro", foreground=COLORS["text"], font=("Segoe UI", 10))
        body.tag_configure("section", foreground=COLORS["accent"], font=("Segoe UI Semibold", 12, "bold"), spacing1=10, spacing3=6)
        body.tag_configure("role", foreground=COLORS["muted"], font=("Segoe UI", 9))
        body.tag_configure("detail", foreground=COLORS["text"], font=("Segoe UI", 10), lmargin1=14, lmargin2=14)
        body.tag_configure("link", foreground="#8beedb", underline=True, font=("Segoe UI Semibold", 10, "bold"))
        body.tag_configure("note", foreground=COLORS["accent_2"], font=("Segoe UI Semibold", 10, "bold"), spacing3=12)

        def put(text, tag="detail"):
            body.insert("end", text, tag)

        def put_link(label, url):
            put(f"{label}: ", "detail")
            start = body.index("end")
            put(url, "link")
            end = body.index("end")
            tag = f"link_{len(body.tag_names())}_{start.replace('.', '_')}"
            body.tag_add(tag, start, end)
            body.tag_bind(tag, "<Button-1>", lambda _e, u=url: webbrowser.open(u))
            body.tag_bind(tag, "<Enter>", lambda _e: body.configure(cursor="hand2"))
            body.tag_bind(tag, "<Leave>", lambda _e: body.configure(cursor="arrow"))
            put("\n")

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
        self.write_config()
        self.write_commandline()
        self.apply_sound_fix()
        if self.reset_user or not (self.root_dir / "appdata" / "user.ltx").exists():
            self.reset_user_ltx_file()
        exe = "Anomaly" + self.renderer
        if self.avx:
            exe += "AVX"
        exe_path = self.root_dir / "bin" / (exe + ".exe")
        if not exe_path.exists():
            messagebox.showerror("Anthology Launcher", f"{TEXT[self.lang]['launch_error']}:\n{exe_path}")
            return
        try:
            subprocess.Popen([str(exe_path)], cwd=str(self.root_dir), env=self._prepare_external_launch(), close_fds=True)
            self.destroy()
        except Exception as exc:
            messagebox.showerror("Anthology Launcher", f"{TEXT[self.lang]['launch_error']}:\n{exe_path}\n\n{exc}")


if __name__ == "__main__":
    os.chdir(app_dir())
    LauncherApp().mainloop()
