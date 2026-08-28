(() => {
    const dictionary = {
        "Основная навигация": ["Main navigation", "Hauptnavigation"],
        "Главная": ["Home", "Start"],
        "Библиотека": ["Library", "Bibliothek"],
        "Информация": ["Information", "Informationen"],
        "Сообщество": ["Community", "Community"],
        "Обновления": ["Updates", "Updates"],
        "Баг-репорт": ["Bug report", "Fehlerbericht"],
        "Установка": ["Installation", "Installation"],
        "Настройки": ["Settings", "Einstellungen"],
        "КОМАНДНЫЙ ЦЕНТР": ["COMMAND CENTER", "KOMMANDOZENTRALE"],
        "КАТАЛОГ": ["CATALOG", "KATALOG"],
        "СПРАВОЧНИК": ["GUIDE", "HANDBUCH"],
        "СВЯЗЬ": ["COMMUNICATION", "KOMMUNIKATION"],
        "СБОРКА": ["BUILD", "ZUSAMMENSTELLUNG"],
        "СИНХРОНИЗАЦИЯ": ["SYNCHRONIZATION", "SYNCHRONISIERUNG"],
        "ПОДДЕРЖКА": ["SUPPORT", "SUPPORT"],
        "РАЗВЁРТЫВАНИЕ": ["DEPLOYMENT", "BEREITSTELLUNG"],
        "КОНФИГУРАЦИЯ": ["CONFIGURATION", "KONFIGURATION"],
        "Добро пожаловать в Зону": ["Welcome to the Zone", "Willkommen in der Zone"],
        "Моды и дополнения": ["Mods and add-ons", "Mods und Erweiterungen"],
        "Anthology и сюжеты": ["Anthology and stories", "Anthology und Handlungen"],
        "Профили и моды": ["Profiles and mods", "Profile und Mods"],
        "Обращение разработчику": ["Contact the developer", "Entwickler kontaktieren"],
        "Об Anthology и сюжетах": ["About Anthology and stories", "Über Anthology und Handlungen"],
        "Чат и обратная связь": ["Chat and feedback", "Chat und Feedback"],
        "Рабочая область Mod Organizer 2": ["Mod Organizer 2 workspace", "Mod-Organizer-2-Arbeitsbereich"],
        "Центр обновлений": ["Update center", "Update-Zentrale"],
        "Оформленное обращение": ["Structured report", "Strukturierte Meldung"],
        "Установка и подключение игры": ["Install and connect the game", "Spiel installieren und verbinden"],
        "Параметры запуска Anomaly": ["Anomaly launch settings", "Anomaly-Starteinstellungen"],
        "СОСТОЯНИЕ СБОРКИ": ["BUILD STATUS", "BUILD-STATUS"],
        "Anthology обнаружена": ["Anthology detected", "Anthology erkannt"],
        "Путь не настроен": ["Path not configured", "Pfad nicht konfiguriert"],
        "СЕРВЕР НА СВЯЗИ": ["SERVER ONLINE", "SERVER ONLINE"],
        "Открыть папку игры": ["Open game folder", "Spielordner öffnen"],
        "НОВАЯ ПЛАТФОРМА ANTHOLOGY": ["NEW ANTHOLOGY PLATFORM", "NEUE ANTHOLOGY-PLATTFORM"],
        "Зона становится": ["The Zone becomes", "Die Zone wird"],
        "больше лаунчера.": ["more than a launcher.", "mehr als ein Launcher."],
        "Обновления из нескольких источников, библиотека модов, видео, голосования и прямая связь с командой — в одном приложении.": ["Updates from multiple sources, a mod library, videos, polls and direct contact with the team — in one application.", "Updates aus mehreren Quellen, Mod-Bibliothek, Videos, Umfragen und direkter Kontakt zum Team — in einer Anwendung."],
        "ИГРАТЬ С МОДПАКОМ": ["PLAY MODPACK", "MODPACK SPIELEN"],
        "ИГРАТЬ В ОРИГИНАЛ": ["PLAY ORIGINAL", "ORIGINAL SPIELEN"],
        "ИГРАТЬ ОНЛАЙН": ["PLAY ONLINE", "ONLINE SPIELEN"],
        "ЦЕНТР ОБНОВЛЕНИЙ →": ["UPDATE CENTER →", "UPDATE-ZENTRALE →"],
        "КАНАЛ": ["CHANNEL", "KANAL"],
        "ИСТОЧНИКИ": ["SOURCES", "QUELLEN"],
        "ПРОВЕРКА": ["VERIFICATION", "PRÜFUNG"],
        "ПОСЛЕДНИЕ СОБЫТИЯ": ["LATEST EVENTS", "LETZTE EREIGNISSE"],
        "Новости разработки": ["Development news", "Entwicklungsneuigkeiten"],
        "ВСЕ НОВОСТИ →": ["ALL NEWS →", "ALLE NEUIGKEITEN →"],
        "МЕДИА": ["MEDIA", "MEDIEN"],
        "Видео в лаунчере": ["Video in the launcher", "Video im Launcher"],
        "ССЫЛКА НА ВИДЕО": ["VIDEO LINK", "VIDEO-LINK"],
        "ВИДЕО-ПЛЕЕР ГОТОВ": ["VIDEO PLAYER READY", "VIDEOPLAYER BEREIT"],
        "URL управляется через сервер новостей": ["The URL is managed by the news server", "Die URL wird über den News-Server verwaltet"],
        "Поиск внутри выбранного раздела": ["Search in the selected section", "Im ausgewählten Bereich suchen"],
        "Личные проекты разработчиков": ["Developers' projects", "Projekte der Entwickler"],
        "Активное тестирование и сложные технические решения": ["Active testing and complex technical solutions", "Aktive Tests und komplexe technische Lösungen"],
        "Новые проекты от модмейкеров": ["New projects from mod authors", "Neue Projekte von Mod-Autoren"],
        "Дополнительные проекты и разнообразие для игры": ["Additional projects and more variety", "Zusätzliche Projekte und mehr Vielfalt"],
        "Решения спорных механик": ["Alternatives for controversial mechanics", "Alternativen für umstrittene Mechaniken"],
        "Опциональная отмена или упрощение отдельных решений": ["Optional removal or simplification of individual mechanics", "Optionale Entfernung oder Vereinfachung einzelner Mechaniken"],
        "ПУБЛИКАЦИЯ ANTHOLOGY": ["ANTHOLOGY PUBLICATION", "ANTHOLOGY-VERÖFFENTLICHUNG"],
        "СВЕРНУТЬ": ["COLLAPSE", "EINKLAPPEN"],
        "ЧИТАТЬ ПОЛНОСТЬЮ": ["READ MORE", "VOLLSTÄNDIG LESEN"],
        "ЗАГРУЗКА…": ["DOWNLOADING…", "DOWNLOAD…"],
        "УСТАНОВИТЬ В MO2": ["INSTALL TO MO2", "IN MO2 INSTALLIEREN"],
        "ВЫБЕРИТЕ ПРОФИЛЬ MO2": ["SELECT MO2 PROFILE", "MO2-PROFIL WÄHLEN"],
        "ОТКРЫТЬ КАРТОЧКУ": ["OPEN CARD", "KARTE ÖFFNEN"],
        "Ничего не найдено": ["Nothing found", "Nichts gefunden"],
        "Измените строку поиска или выберите другой раздел библиотеки.": ["Change the search query or select another library section.", "Ändern Sie die Suche oder wählen Sie einen anderen Bibliotheksbereich."],
        "Каталог расширяется": ["The catalog is growing", "Der Katalog wächst"],
        "КАНАЛЫ": ["CHANNELS", "KANÄLE"],
        "ЯЗЫК": ["LANGUAGE", "SPRACHE"],
        "Реальный чат": ["Real Chat", "Echtzeit-Chat"],
        "Игровой мост Anthology 2.1": ["Anthology 2.1 game bridge", "Anthology-2.1-Spielbrücke"],
        "Встроенный чат лаунчера и игровой мост": ["Built-in launcher chat and game bridge", "Integrierter Launcher-Chat und Spielbrücke"],
        "○ ПОДКЛЮЧЕНИЕ": ["○ CONNECTING", "○ VERBINDUNG"],
        "СИСТЕМА": ["SYSTEM", "SYSTEM"],
        "Сейчас": ["Now", "Jetzt"],
        "Сообщение в Реальный чат": ["Message Real Chat", "Nachricht an Echtzeit-Chat"],
        "УЧАСТНИКИ": ["PARTICIPANTS", "TEILNEHMER"],
        "ПАРАМЕТРЫ": ["OPTIONS", "OPTIONEN"],
        "Получаем список": ["Loading participants", "Teilnehmer werden geladen"],
        "Участники появятся после подключения к каналу.": ["Participants will appear after connecting to the channel.", "Teilnehmer erscheinen nach der Verbindung mit dem Kanal."],
        "ВЫ": ["YOU", "SIE"],
        "ЛС": ["DM", "PN"],
        "Канал подключён": ["Channel connected", "Kanal verbunden"],
        "Нет подключения": ["Not connected", "Nicht verbunden"],
        "Открыть параметры": ["Open options", "Optionen öffnen"],
        "ПОДКЛЮЧЕНИЕ": ["CONNECTION", "VERBINDUNG"],
        "Профиль Реального чата": ["Real Chat profile", "Echtzeit-Chat-Profil"],
        "Канал": ["Channel", "Kanal"],
        "Русский / Славянский": ["Russian / Slavic", "Russisch / Slawisch"],
        "Определять группировку в игре": ["Detect faction in game", "Fraktion im Spiel erkennen"],
        "Автоматически читать состояние Anomaly": ["Read Anomaly state automatically", "Anomaly-Status automatisch lesen"],
        "Группировка": ["Faction", "Fraktion"],
        "Одиночки": ["Loners", "Einzelgänger"],
        "Бандиты": ["Bandits", "Banditen"],
        "Чистое небо": ["Clear Sky", "Clear Sky"],
        "Долг": ["Duty", "Wächter"],
        "Учёные": ["Ecologists", "Ökologen"],
        "Свобода": ["Freedom", "Freiheit"],
        "Наёмники": ["Mercenaries", "Söldner"],
        "Военные": ["Military", "Militär"],
        "Монолит": ["Monolith", "Monolith"],
        "Ренегаты": ["Renegades", "Renegaten"],
        "Зомбированные": ["Zombified", "Zombifizierte"],
        "Время сообщений": ["Message timestamps", "Zeitstempel"],
        "Показывать отметки времени в истории": ["Show timestamps in history", "Zeitstempel im Verlauf anzeigen"],
        "СОБЫТИЯ": ["EVENTS", "EREIGNISSE"],
        "Уведомления о смертях": ["Death notifications", "Todesmeldungen"],
        "Отправлять события": ["Send events", "Ereignisse senden"],
        "Передавать смерти игрока в канал": ["Send player deaths to the channel", "Spielertode an den Kanal senden"],
        "Получать события": ["Receive events", "Ereignisse empfangen"],
        "Показывать события других игроков": ["Show events from other players", "Ereignisse anderer Spieler anzeigen"],
        "Интервал, секунд": ["Interval, seconds", "Intervall, Sekunden"],
        "ИНТЕРФЕЙС ИГРЫ": ["IN-GAME INTERFACE", "SPIELINTERFACE"],
        "Окно и новости": ["Window and news", "Fenster und Meldungen"],
        "Длительность новости, секунд": ["News duration, seconds", "Meldungsdauer, Sekunden"],
        "Клавиша чата": ["Chat key", "Chat-Taste"],
        "Тильда (~)": ["Tilde (~)", "Tilde (~)"],
        "Звук новостей": ["News sound", "Meldungston"],
        "Проигрывать сигнал нового сообщения": ["Play a new-message sound", "Ton bei neuer Nachricht abspielen"],
        "Закрывать после отправки": ["Close after sending", "Nach dem Senden schließen"],
        "Скрывать игровое окно после сообщения": ["Hide the in-game window after sending", "Spielfenster nach dem Senden ausblenden"],
        "НАЗАД": ["BACK", "ZURÜCK"],
        "ПРИМЕНИТЬ": ["APPLY", "ANWENDEN"],
        "Выберите участника справа для личного сообщения": ["Select a participant on the right for a direct message", "Wählen Sie rechts einen Teilnehmer für eine private Nachricht"],
        "Отменить личное сообщение": ["Cancel direct message", "Private Nachricht abbrechen"],
        "ГОЛОСОВАНИЕ": ["POLL", "UMFRAGE"],
        "ОБРАТНАЯ СВЯЗЬ": ["FEEDBACK", "FEEDBACK"],
        "ЗАКРЫТЬ ×": ["CLOSE ×", "SCHLIESSEN ×"],
        "ОПУБЛИКОВАНО": ["PUBLISHED", "VERÖFFENTLICHT"],
        "СПРАВОЧНИК ANTHOLOGY": ["ANTHOLOGY GUIDE", "ANTHOLOGY-HANDBUCH"],
        "ФОНОВОЕ ИЗОБРАЖЕНИЕ БУДЕТ ДОБАВЛЕНО": ["BACKGROUND IMAGE WILL BE ADDED", "HINTERGRUNDBILD WIRD HINZUGEFÜGT"],
        "СЮЖЕТНЫЙ КАТАЛОГ": ["STORY CATALOG", "HANDLUNGSKATALOG"],
        "Системные требования": ["System requirements", "Systemanforderungen"],
        "Минимальные и рекомендуемые параметры для оригинала и модпака.": ["Minimum and recommended specifications for the original game and modpack.", "Minimale und empfohlene Anforderungen für Originalspiel und Modpack."],
        "Информация об Оригинале": ["About the original game", "Informationen zum Originalspiel"],
        "Основа Anthology и доступные сюжетные линии без опционального модпака.": ["The Anthology foundation and available storylines without the optional modpack.", "Die Anthology-Grundlage und verfügbare Handlungen ohne optionales Modpack."],
        "Информация о Модпаке": ["About the modpack", "Informationen zum Modpack"],
        "Профили Standard и Hard, совместимость и принцип модульности.": ["Standard and Hard profiles, compatibility and modular design.", "Standard- und Hard-Profile, Kompatibilität und modularer Aufbau."],
        "Сюжеты": ["Stories", "Handlungen"],
        "Отдельные карточки всех оригинальных, модифицированных и freeplay-историй.": ["Individual cards for every original, modified and freeplay story.", "Einzelne Karten für alle originalen, modifizierten und Freeplay-Handlungen."],
        "ДОСТУПЕН": ["AVAILABLE", "VERFÜGBAR"],
        "В РАЗРАБОТКЕ": ["IN DEVELOPMENT", "IN ENTWICKLUNG"],
        "ОРИГИНАЛЬНЫЙ СЮЖЕТ": ["ORIGINAL STORY", "ORIGINALHANDLUNG"],
        "СЮЖЕТНАЯ МОДИФИКАЦИЯ": ["STORY MODIFICATION", "HANDLUNGSMODIFIKATION"],
        "Одна версия.": ["One version.", "Eine Version."],
        "Вся сборка.": ["The entire build.", "Die gesamte Zusammenstellung."],
        "Корень игры и MO2 обновляются одной транзакцией. Устаревшие управляемые файлы удаляются с резервной копией, а ошибка возвращает всю предыдущую сборку.": ["The game root and MO2 are updated in one transaction. Obsolete managed files are backed up and removed; any error restores the complete previous build.", "Spielwurzel und MO2 werden in einer Transaktion aktualisiert. Veraltete verwaltete Dateien werden gesichert und entfernt; bei einem Fehler wird die gesamte vorherige Version wiederhergestellt."],
        "ИСТОЧНИК АРХИВА": ["ARCHIVE SOURCE", "ARCHIVQUELLE"],
        "Предпочитаемое зеркало": ["Preferred mirror", "Bevorzugter Spiegelserver"],
        "Ресурс": ["Source", "Quelle"],
        "Автоматически": ["Automatic", "Automatisch"],
        "Другой HTTPS": ["Other HTTPS", "Anderes HTTPS"],
        "ПРОВЕРИТЬ ОБНОВЛЕНИЯ": ["CHECK FOR UPDATES", "NACH UPDATES SUCHEN"],
        "УСТАНОВИТЬ ОБНОВЛЕНИЕ": ["INSTALL UPDATE", "UPDATE INSTALLIEREN"],
        "ОТКАТИТЬ ПОСЛЕДНЕЕ ОБНОВЛЕНИЕ": ["ROLL BACK LAST UPDATE", "LETZTES UPDATE ZURÜCKSETZEN"],
        "ОТМЕНА": ["CANCEL", "ABBRECHEN"],
        "Готов к безопасной проверке": ["Ready for a secure check", "Bereit zur sicheren Prüfung"],
        "Обновление доступно": ["Update available", "Update verfügbar"],
        "Версия актуальна": ["Up to date", "Version ist aktuell"],
        "ОФОРМЛЕННОЕ ОБРАЩЕНИЕ": ["STRUCTURED REPORT", "STRUKTURIERTE MELDUNG"],
        "Сообщить об ошибке": ["Report a bug", "Fehler melden"],
        "Название проблемы": ["Issue title", "Problemtitel"],
        "Что произошло": ["What happened", "Was ist passiert"],
        "Шаги для воспроизведения": ["Steps to reproduce", "Schritte zum Reproduzieren"],
        "Ожидаемый результат": ["Expected result", "Erwartetes Ergebnis"],
        "Версия сборки": ["Build version", "Build-Version"],
        "Характеристики ПК": ["PC specifications", "PC-Spezifikationen"],
        "Ссылка на полный пакет файлов": ["Link to the full file package", "Link zum vollständigen Dateipaket"],
        "Мелкие вложения": ["Small attachments", "Kleine Anhänge"],
        "ДОБАВИТЬ ФАЙЛ": ["ADD FILE", "DATEI HINZUFÜGEN"],
        "ОТПРАВИТЬ БАГ-РЕПОРТ": ["SEND BUG REPORT", "FEHLERBERICHT SENDEN"],
        "ANOMALY RUNTIME CONTROL": ["ANOMALY RUNTIME CONTROL", "ANOMALY-LAUFZEITSTEUERUNG"],
        "Соберите свой": ["Configure your", "Konfigurieren Sie Ihr"],
        "профиль запуска.": ["launch profile.", "Startprofil."],
        "Здесь только параметры игры. Установка, обновления и подключение MO2 вынесены в собственные разделы Anthology Next.": ["Only game settings are shown here. Installation, updates and MO2 connection have their own Anthology Next sections.", "Hier stehen nur Spieleinstellungen. Installation, Updates und MO2-Verbindung befinden sich in eigenen Anthology-Next-Bereichen."],
        "ТЕКУЩИЙ ПРОФИЛЬ": ["CURRENT PROFILE", "AKTUELLES PROFIL"],
        "ИСПОЛНЯЕМЫЙ ФАЙЛ": ["EXECUTABLE", "AUSFÜHRBARE DATEI"],
        "КОРЕНЬ ИГРЫ": ["GAME ROOT", "SPIELWURZEL"],
        "НЕ ПОДКЛЮЧЁН": ["NOT CONNECTED", "NICHT VERBUNDEN"],
        "ГРАФИЧЕСКИЙ КОНТУР": ["GRAPHICS", "GRAFIK"],
        "Рендер и карта теней": ["Renderer and shadow map", "Renderer und Schattenkarte"],
        "Выберите основу запуска": ["Select launch renderer", "Start-Renderer auswählen"],
        "Разрешение карты теней": ["Shadow map resolution", "Schattenkartenauflösung"],
        "СИСТЕМА": ["SYSTEM", "SYSTEM"],
        "Совместимость и диагностика": ["Compatibility and diagnostics", "Kompatibilität und Diagnose"],
        "Инструкции AVX": ["AVX instructions", "AVX-Anweisungen"],
        "Выбирает AVX-версию исполняемого файла": ["Selects the AVX executable", "Wählt die AVX-Version der ausführbaren Datei"],
        "Отладочный режим для разработчиков и тестеров": ["Debug mode for developers and testers", "Debug-Modus für Entwickler und Tester"],
        "Добавляет ключ -dbg. Обычному игроку включать только в крайнем случае": ["Adds the -dbg option. Regular players should enable it only as a last resort", "Fügt die Option -dbg hinzu. Normale Spieler sollten sie nur im äußersten Fall aktivieren"],
        "Сбросить user.ltx": ["Reset user.ltx", "user.ltx zurücksetzen"],
        "Одноразовое восстановление при следующем запуске": ["One-time reset on the next launch", "Einmaliges Zurücksetzen beim nächsten Start"],
        "НЕ НУЖНО": ["NOT NEEDED", "NICHT NÖTIG"],
        "ОЖИДАЕТ": ["PENDING", "AUSSTEHEND"],
        "АУДИО": ["AUDIO", "AUDIO"],
        "Загрузка и совместимость звука": ["Audio loading and compatibility", "Audio-Laden und Kompatibilität"],
        "Предзагрузка звуков": ["Preload sounds", "Sounds vorladen"],
        "Обход проблем OpenAL": ["OpenAL workaround", "OpenAL-Problemumgehung"],
        "ИГРОВОЙ МОСТ": ["GAME BRIDGE", "SPIELBRÜCKE"],
        "Мост уже подключён": ["Bridge connected", "Brücke verbunden"],
        "Мост ожидает запуска": ["Bridge waiting", "Brücke wartet"],
        "Чат работает из корня игры и остаётся доступен внутри Anomaly.": ["Chat uses the game root and remains available inside Anomaly.", "Der Chat nutzt die Spielwurzel und bleibt in Anomaly verfügbar."],
        "Подключать заранее": ["Connect in advance", "Im Voraus verbinden"],
        "Запускать Реальный чат вместе с лаунчером": ["Start Real Chat with the launcher", "Echtzeit-Chat mit dem Launcher starten"],
        "АВТО": ["AUTO", "AUTO"],
        "ВРУЧНУЮ": ["MANUAL", "MANUELL"],
        "ЯЗЫК ИНТЕРФЕЙСА": ["INTERFACE LANGUAGE", "OBERFLÄCHENSPRACHE"],
        "Язык лаунчера": ["Launcher language", "Launcher-Sprache"],
        "Русский": ["Russian", "Russisch"],
        "Меняется весь интерфейс лаунчера, а новости, информация и библиотека используют опубликованный перевод выбранного языка.": ["The entire launcher interface changes; news, information and the library use the published translation for the selected language.", "Die gesamte Launcher-Oberfläche wird umgestellt; Neuigkeiten, Informationen und Bibliothek verwenden die veröffentlichte Übersetzung der gewählten Sprache."],
        "ПРИМЕНЕНИЕ ПРОФИЛЯ": ["APPLY PROFILE", "PROFIL ANWENDEN"],
        "СОХРАНИТЬ ПРОФИЛЬ": ["SAVE PROFILE", "PROFIL SPEICHERN"],
        "ЛОКАЛЬНАЯ УСТАНОВКА": ["LOCAL INSTALLATION", "LOKALE INSTALLATION"],
        "Игра, модпак и канал обновлений": ["Game, modpack and update channel", "Spiel, Modpack und Update-Kanal"],
        "УСТАНОВКА ИГРЫ": ["GAME INSTALLATION", "SPIELINSTALLATION"],
        "Комплектные бинарные пакеты": ["Bundled binary packages", "Mitgelieferte Binärpakete"],
        "ВЫБРАТЬ ПАПКУ": ["SELECT FOLDER", "ORDNER WÄHLEN"],
        "УСТАНОВИТЬ ИГРУ": ["INSTALL GAME", "SPIEL INSTALLIEREN"],
        "ОБЫЧНЫЙ УСТАНОВЩИК": ["STANDARD INSTALLER", "STANDARD-INSTALLER"],
        "Запустить Setup": ["Run Setup", "Setup starten"],
        "ВЫБРАТЬ SETUP": ["SELECT SETUP", "SETUP WÄHLEN"],
        "ЗАПУСТИТЬ SETUP": ["RUN SETUP", "SETUP STARTEN"],
        "УЖЕ УСТАНОВЛЕНО": ["ALREADY INSTALLED", "BEREITS INSTALLIERT"],
        "Корень Anomaly": ["Anomaly root", "Anomaly-Wurzel"],
        "ВЫБРАТЬ": ["SELECT", "WÄHLEN"],
        "ОТКЛЮЧИТЬ": ["DISCONNECT", "TRENNEN"],
        "МОДПАК": ["MODPACK", "MODPACK"],
        "ВЫБРАТЬ ПАПКУ MO2": ["SELECT MO2 FOLDER", "MO2-ORDNER WÄHLEN"],
        "Подписанный канал": ["Signed channel", "Signierter Kanal"],
        "Предпочитаемый ресурс": ["Preferred source", "Bevorzugte Quelle"],
        "СОХРАНИТЬ УСТАНОВКУ": ["SAVE INSTALLATION", "INSTALLATION SPEICHERN"],
        "Имя в чате": ["Chat name", "Chat-Name"],
        "Отображаемое имя": ["Display name", "Anzeigename"],
        "СОХРАНИТЬ ИМЯ": ["SAVE NAME", "NAMEN SPEICHERN"],
        "ЛОКАЛЬНЫЕ ДАННЫЕ": ["LOCAL DATA", "LOKALE DATEN"],
        "ОБНОВИТЬ": ["REFRESH", "AKTUALISIEREN"],
        "ЗАПУСТИТЬ СБОРКУ": ["LAUNCH BUILD", "ZUSAMMENSTELLUNG STARTEN"],
        "УСТАНОВИТЬ АРХИВ": ["INSTALL ARCHIVE", "ARCHIV INSTALLIEREN"],
        "ПРОФИЛЬ": ["PROFILE", "PROFIL"],
        "РАЗДЕЛИТЕЛЬ": ["SEPARATOR", "TRENNER"],
        "Прокрутка страницы": ["Page scrolling", "Seite scrollen"],
        "Прокрутить вверх": ["Scroll up", "Nach oben scrollen"],
        "Прокрутить вниз": ["Scroll down", "Nach unten scrollen"]
    };

    const nodes = new WeakMap();
    const attributes = ["placeholder", "title", "aria-label"];
    let language = "ru";
    let observer;

    function indexFor(lang) {
        return lang === "de" ? 1 : 0;
    }

    function translate(source) {
        if (language === "ru") return source;
        const trimmed = source.trim();
        const entry = dictionary[trimmed];
        if (!entry) return source;
        const leading = source.slice(0, source.indexOf(trimmed));
        const trailing = source.slice(source.indexOf(trimmed) + trimmed.length);
        return leading + entry[indexFor(language)] + trailing;
    }

    function applyTextNode(node) {
        const current = node.nodeValue || "";
        if (!current.trim()) return;
        const previous = nodes.get(node);
        const source = previous && current === previous.last ? previous.source : current;
        const target = translate(source);
        nodes.set(node, { source, last: target });
        if (current !== target) node.nodeValue = target;
    }

    function applyElement(element) {
        for (const attribute of attributes) {
            if (!element.hasAttribute(attribute)) continue;
            const key = `attr:${attribute}`;
            const current = element.getAttribute(attribute) || "";
            const state = element[key];
            const source = state && current === state.last ? state.source : current;
            const target = translate(source);
            element[key] = { source, last: target };
            if (current !== target) element.setAttribute(attribute, target);
        }
    }

    function apply(root = document.body) {
        if (!root) return;
        if (root.nodeType === Node.TEXT_NODE) {
            applyTextNode(root);
            return;
        }
        if (root.nodeType !== Node.ELEMENT_NODE && root.nodeType !== Node.DOCUMENT_NODE) return;
        if (root.nodeType === Node.ELEMENT_NODE) applyElement(root);
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT);
        let node;
        while ((node = walker.nextNode())) {
            if (node.nodeType === Node.TEXT_NODE) applyTextNode(node);
            else applyElement(node);
        }
    }

    window.anthologyLocalization = {
        setLanguage(value) {
            language = value === "en" || value === "de" ? value : "ru";
            document.documentElement.lang = language;
            apply();
            if (!observer && document.body) {
                observer = new MutationObserver(records => {
                    for (const record of records) {
                        if (record.type === "characterData") applyTextNode(record.target);
                        for (const added of record.addedNodes) apply(added);
                    }
                });
                observer.observe(document.body, { subtree: true, childList: true, characterData: true });
            }
        }
    };
})();
