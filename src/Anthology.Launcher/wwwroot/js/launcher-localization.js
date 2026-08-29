(() => {
    const dictionary = {
        "Основная навигация": ["Main navigation", "Hauptnavigation", "Główna nawigacja", "Navigation principale", "Navegación principal", "主导航", "メインナビゲーション"],
        "Главная": ["Home", "Start", "Strona główna", "Accueil", "Inicio", "主页", "ホーム"],
        "Новости": ["News", "Neuigkeiten", "Aktualności", "Actualités", "Noticias", "新闻", "ニュース"],
        "Библиотека": ["Library", "Bibliothek", "Biblioteka", "Bibliothèque", "Biblioteca", "模组库", "ライブラリ"],
        "Информация": ["Information", "Informationen", "Informacje", "Informations", "Información", "信息", "情報"],
        "Сообщество": ["Community", "Community", "Społeczność", "Communauté", "Comunidad", "社区", "コミュニティ"],
        "Обновления": ["Updates", "Updates", "Aktualizacje", "Mises à jour", "Actualizaciones", "更新", "アップデート"],
        "Баг-репорт": ["Bug report", "Fehlerbericht", "Zgłoszenie błędu", "Rapport de bug", "Informe de error", "错误报告", "バグ報告"],
        "Установка": ["Installation", "Installation", "Instalacja", "Installation", "Instalación", "安装", "インストール"],
        "Настройки": ["Settings", "Einstellungen", "Ustawienia", "Paramètres", "Ajustes", "设置", "設定"],
        "КОМАНДНЫЙ ЦЕНТР": ["COMMAND CENTER", "KOMMANDOZENTRALE", "CENTRUM DOWODZENIA", "CENTRE DE COMMANDE", "CENTRO DE MANDO", "指挥中心", "コマンドセンター"],
        "КАТАЛОГ": ["CATALOG", "KATALOG", "KATALOG", "CATALOGUE", "CATÁLOGO", "目录", "カタログ"],
        "СПРАВОЧНИК": ["GUIDE", "HANDBUCH", "PRZEWODNIK", "GUIDE", "GUÍA", "指南", "ガイド"],
        "СВЯЗЬ": ["COMMUNICATION", "KOMMUNIKATION", "KOMUNIKACJA", "COMMUNICATION", "COMUNICACIÓN", "交流", "コミュニケーション"],
        "СБОРКА": ["BUILD", "ZUSAMMENSTELLUNG", "KOMPILACJA", "VERSION", "COMPILACIÓN", "构建", "ビルド"],
        "СИНХРОНИЗАЦИЯ": ["SYNCHRONIZATION", "SYNCHRONISIERUNG", "SYNCHRONIZACJA", "SYNCHRONISATION", "SINCRONIZACIÓN", "同步", "同期"],
        "ПОДДЕРЖКА": ["SUPPORT", "SUPPORT", "WSPARCIE", "ASSISTANCE", "SOPORTE", "支持", "サポート"],
        "РАЗВЁРТЫВАНИЕ": ["DEPLOYMENT", "BEREITSTELLUNG", "WDROŻENIE", "DÉPLOIEMENT", "DESPLIEGUE", "部署", "展開"],
        "КОНФИГУРАЦИЯ": ["CONFIGURATION", "KONFIGURATION", "KONFIGURACJA", "CONFIGURATION", "CONFIGURACIÓN", "配置", "構成"],
        "Добро пожаловать в Зону": ["Welcome to the Zone", "Willkommen in der Zone", "Witamy w Zonie", "Bienvenue dans la Zone", "Bienvenido a la Zona", "欢迎来到禁区", "ゾーンへようこそ"],
        "ЛЕНТА": ["FEED", "FEED", "AKTUALNOŚCI", "FIL", "FUENTE", "动态", "フィード"],
        "Новости Anthology": ["Anthology News", "Anthology-Neuigkeiten", "Aktualności Anthology", "Actualités Anthology", "Noticias de Anthology", "Anthology 新闻", "Anthology ニュース"],
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
        "Помощник Anthology": ["Anthology Assistant", "Anthology-Assistent"],
        "РАЗРАБОТЧИК": ["DEVELOPER", "ENTWICKLER"],
        "МОДЕРАТОР": ["MODERATOR", "MODERATOR"],
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
        "Шаги для воспроизведения": ["Steps to reproduce", "Schritte zum Reproduzieren", "Kroki do odtworzenia", "Étapes de reproduction", "Pasos para reproducir", "复现步骤", "再現手順"],
        "Ожидаемый результат": ["Expected result", "Erwartetes Ergebnis", "Oczekiwany rezultat", "Résultat attendu", "Resultado esperado", "预期结果", "期待される結果"],
        "Версия сборки": ["Build version", "Build-Version"],
        "Характеристики ПК": ["PC specifications", "PC-Spezifikationen"],
        "Ссылка на полный пакет файлов": ["Link to the full file package", "Link zum vollständigen Dateipaket"],
        "Мелкие вложения": ["Small attachments", "Kleine Anhänge"],
        "ДОБАВИТЬ ФАЙЛ": ["ADD FILE", "DATEI HINZUFÜGEN"],
        "ОТПРАВИТЬ БАГ-РЕПОРТ": ["SEND BUG REPORT", "FEHLERBERICHT SENDEN"],
        "Краткий заголовок": ["Short title", "Kurzer Titel", "Krótki tytuł", "Titre court", "Título breve", "简短标题", "短いタイトル"],
        "Ситуация, локация и что произошло": ["Situation, location, and what happened", "Situation, Ort und Ereignis", "Sytuacja, lokacja i przebieg zdarzenia", "Situation, lieu et événement", "Situación, ubicación y qué ocurrió", "情况、地点和发生的事情", "状況、場所、起きたこと"],
        "Полный текст ошибки или фрагмент лога": ["Full error text or log excerpt", "Vollständiger Fehlertext oder Protokollauszug", "Pełny tekst błędu lub fragment logu", "Texte complet de l’erreur ou extrait du journal", "Texto completo del error o fragmento del registro", "完整错误文本或日志片段", "完全なエラー文またはログの抜粋"],
        "Фактический результат": ["Actual result", "Tatsächliches Ergebnis", "Rzeczywisty rezultat", "Résultat réel", "Resultado real", "实际结果", "実際の結果"],
        "Версия и дата сборки": ["Build version and date", "Build-Version und Datum", "Wersja i data kompilacji", "Version et date de la build", "Versión y fecha de compilación", "构建版本和日期", "ビルドのバージョンと日付"],
        "PC Specs и файл подкачки": ["PC specs and page file", "PC-Daten und Auslagerungsdatei", "Specyfikacja PC i plik stronicowania", "Configuration du PC et fichier d’échange", "Especificaciones del PC y archivo de paginación", "电脑配置和页面文件", "PC仕様とページファイル"],
        "HTTPS-ссылка на полный пакет": ["HTTPS link to the full package", "HTTPS-Link zum vollständigen Paket", "Link HTTPS do pełnego pakietu", "Lien HTTPS vers le paquet complet", "Enlace HTTPS al paquete completo", "完整文件包的HTTPS链接", "完全なパッケージへのHTTPSリンク"],
        "Контакт для ответа": ["Contact for a reply", "Kontakt für Rückfragen", "Kontakt do odpowiedzi", "Contact pour la réponse", "Contacto para la respuesta", "回复联系方式", "返信用の連絡先"],
        "＋ ПРИЛОЖИТЬ МЕЛКИЕ ФАЙЛЫ": ["＋ ATTACH SMALL FILES", "＋ KLEINE DATEIEN ANHÄNGEN", "＋ DOŁĄCZ MAŁE PLIKI", "＋ JOINDRE DE PETITS FICHIERS", "＋ ADJUNTAR ARCHIVOS PEQUEÑOS", "＋ 添加小文件", "＋ 小さなファイルを添付"],
        "ОТПРАВИТЬ РАЗРАБОТЧИКУ": ["SEND TO DEVELOPER", "AN ENTWICKLER SENDEN", "WYŚLIJ DO DEWELOPERA", "ENVOYER AU DÉVELOPPEUR", "ENVIAR AL DESARROLLADOR", "发送给开发者", "開発者に送信"],
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
        "Один отчёт — одна конкретная проблема, которую разработчик сможет повторить.": ["One report — one specific problem the developer can reproduce.", "Ein Bericht – ein konkretes Problem, das der Entwickler reproduzieren kann.", "Jedno zgłoszenie — jeden konkretny problem, który deweloper może odtworzyć.", "Un rapport — un problème précis que le développeur peut reproduire.", "Un informe: un problema concreto que el desarrollador pueda reproducir.", "一份报告只描述一个开发者能够复现的具体问题。", "1件の報告には、開発者が再現できる具体的な問題を1つだけ記載してください。"],
        "Не пишите только «у меня вылетело» или «почините». Сначала кратко назовите проблему, затем укажите место и условия, перечислите действия по порядку и приложите подтверждения. Если проблема относится к стороннему моду, установке или настройке, прямо напишите об этом.": ["Do not write only “it crashed” or “fix it”. Name the problem briefly, specify the place and conditions, list the actions in order, and attach evidence. If it concerns a third-party mod, installation, or configuration, say so clearly.", "Schreiben Sie nicht nur „Absturz“ oder „bitte beheben“. Benennen Sie das Problem kurz, nennen Sie Ort und Bedingungen, führen Sie die Schritte der Reihe nach auf und fügen Sie Nachweise bei. Betrifft es einen Fremdmod, die Installation oder Konfiguration, schreiben Sie das ausdrücklich.", "Nie pisz tylko „gra się wyłączyła” albo „naprawcie”. Krótko nazwij problem, podaj miejsce i warunki, wypisz czynności po kolei i dołącz dowody. Jeśli problem dotyczy zewnętrznego moda, instalacji lub konfiguracji, napisz to wprost.", "N’écrivez pas seulement « le jeu a planté » ou « corrigez ». Nommez brièvement le problème, précisez le lieu et les conditions, énumérez les actions dans l’ordre et joignez des preuves. S’il concerne un mod tiers, l’installation ou la configuration, indiquez-le clairement.", "No escribas solo «se cerró» o «arréglenlo». Resume el problema, indica el lugar y las condiciones, enumera los pasos y adjunta pruebas. Si se relaciona con un mod de terceros, la instalación o la configuración, indícalo claramente.", "不要只写“游戏崩溃了”或“请修复”。请先简要说明问题，再注明地点和条件，按顺序列出操作并附上证据。如果问题与第三方模组、安装或设置有关，请明确说明。", "「クラッシュした」「直して」だけではなく、問題を簡潔に示し、場所と条件、操作手順、証拠を添付してください。外部Mod、インストール、設定に関する問題なら明記してください。"],
        "ЗАПОЛНИТЕ ПО ПОРЯДКУ": ["COMPLETE IN THIS ORDER", "IN DIESER REIHENFOLGE AUSFÜLLEN", "WYPEŁNIJ W TEJ KOLEJNOŚCI", "REMPLISSEZ DANS CET ORDRE", "COMPLETA EN ESTE ORDEN", "请按顺序填写", "この順番で記入してください"],
        "Заголовок:": ["Title:", "Titel:", "Tytuł:", "Titre :", "Título:", "标题：", "タイトル："],
        "что сломалось и где.": ["what broke and where.", "was und wo nicht funktioniert.", "co i gdzie nie działa.", "ce qui ne fonctionne pas et où.", "qué falló y dónde.", "什么功能在什么位置出现了问题。", "どこで何が壊れたか。"],
        "Ситуация:": ["Situation:", "Situation:", "Sytuacja:", "Situation :", "Situación:", "情况：", "状況："],
        "сюжет, локация, NPC и важные настройки.": ["story, location, NPCs, and important settings.", "Handlung, Ort, NPCs und wichtige Einstellungen.", "fabuła, lokacja, NPC i ważne ustawienia.", "scénario, lieu, PNJ et paramètres importants.", "historia, ubicación, PNJ y ajustes importantes.", "剧情、地点、NPC和重要设置。", "ストーリー、場所、NPC、重要な設定。"],
        "Шаги:": ["Steps:", "Schritte:", "Kroki:", "Étapes :", "Pasos:", "步骤：", "手順："],
        "действия от загрузки сейва до появления ошибки.": ["actions from loading the save until the error appears.", "Aktionen vom Laden des Spielstands bis zum Auftreten des Fehlers.", "czynności od wczytania zapisu do wystąpienia błędu.", "actions depuis le chargement de la sauvegarde jusqu’à l’erreur.", "acciones desde cargar la partida hasta que aparece el error.", "从载入存档到错误出现的操作。", "セーブを読み込んでからエラーが出るまでの操作。"],
        "Результат:": ["Result:", "Ergebnis:", "Rezultat:", "Résultat :", "Resultado:", "结果：", "結果："],
        "что должно было произойти и что произошло.": ["what should have happened and what actually happened.", "was geschehen sollte und was tatsächlich geschah.", "co powinno się wydarzyć i co wydarzyło się faktycznie.", "ce qui devait se produire et ce qui s’est réellement produit.", "qué debía ocurrir y qué ocurrió realmente.", "预期结果和实际结果。", "期待した結果と実際に起きたこと。"],
        "Доказательства:": ["Evidence:", "Nachweise:", "Dowody:", "Preuves :", "Pruebas:", "证据：", "証拠："],
        "полный лог, небольшой конфиг или HTTPS-ссылка на сейв и видео.": ["full log, a small config, or an HTTPS link to the save and video.", "vollständiges Protokoll, kleine Konfiguration oder HTTPS-Link zu Spielstand und Video.", "pełny log, mały plik konfiguracyjny albo link HTTPS do zapisu i filmu.", "journal complet, petit fichier de configuration ou lien HTTPS vers la sauvegarde et la vidéo.", "registro completo, configuración pequeña o enlace HTTPS a la partida y al vídeo.", "完整日志、小型配置文件，或存档和视频的HTTPS链接。", "完全なログ、小さな設定ファイル、またはセーブと動画へのHTTPSリンク。"],
        "Окружение:": ["Environment:", "Umgebung:", "Środowisko:", "Environnement :", "Entorno:", "运行环境：", "環境："],
        "версия Anthology, вариант сборки, CPU, GPU, RAM и файл подкачки.": ["Anthology version, build variant, CPU, GPU, RAM, and page file.", "Anthology-Version, Build-Variante, CPU, GPU, RAM und Auslagerungsdatei.", "wersja Anthology, wariant kompilacji, CPU, GPU, RAM i plik stronicowania.", "version d’Anthology, variante, processeur, GPU, RAM et fichier d’échange.", "versión de Anthology, variante, CPU, GPU, RAM y archivo de paginación.", "Anthology版本、构建类型、CPU、GPU、内存和页面文件。", "Anthologyのバージョン、ビルド種別、CPU、GPU、RAM、ページファイル。"],
        "МОИ ОБРАЩЕНИЯ": ["MY REPORTS", "MEINE MELDUNGEN", "MOJE ZGŁOSZENIA", "MES RAPPORTS", "MIS INFORMES", "我的报告", "自分の報告"],
        "Ответы команды Anthology": ["Replies from the Anthology team", "Antworten des Anthology-Teams", "Odpowiedzi zespołu Anthology", "Réponses de l’équipe Anthology", "Respuestas del equipo Anthology", "Anthology团队的回复", "Anthologyチームからの返信"],
        "Здесь сохраняется переписка по вашим отчётам. Когда разработчик ответит, изменит статус или закроет запрос, обновление появится в этой карточке.": ["Conversations about your reports are saved here. When a developer replies, changes the status, or closes the request, the update will appear on this card.", "Hier werden Unterhaltungen zu Ihren Meldungen gespeichert. Wenn ein Entwickler antwortet, den Status ändert oder die Anfrage schließt, erscheint die Aktualisierung in dieser Karte.", "Tutaj zapisywana jest rozmowa dotycząca zgłoszeń. Odpowiedź dewelopera, zmiana statusu lub zamknięcie pojawią się na tej karcie.", "Les échanges concernant vos rapports sont enregistrés ici. Toute réponse, modification de statut ou fermeture apparaîtra sur cette fiche.", "Aquí se guarda la conversación de tus informes. Las respuestas, cambios de estado o cierres aparecerán en esta tarjeta.", "这里会保存报告的对话。开发者回复、更改状态或关闭请求后，更新会显示在此卡片中。", "報告に関するやり取りはここに保存されます。開発者の返信、状態変更、終了はこのカードに表示されます。"],
        "НОВОЕ": ["NEW", "NEU", "NOWE", "NOUVEAU", "NUEVO", "新建", "新規"],
        "В РАБОТЕ": ["IN PROGRESS", "IN BEARBEITUNG", "W TOKU", "EN COURS", "EN PROCESO", "处理中", "対応中"],
        "НУЖЕН ОТВЕТ ИГРОКА": ["PLAYER REPLY NEEDED", "ANTWORT DES SPIELERS NÖTIG", "OCZEKIWANIE NA GRACZA", "RÉPONSE DU JOUEUR REQUISE", "SE NECESITA RESPUESTA", "等待玩家回复", "プレイヤーの返信待ち"],
        "РЕШЕНО": ["RESOLVED", "GELÖST", "ROZWIĄZANE", "RÉSOLU", "RESUELTO", "已解决", "解決済み"],
        "ЗАКРЫТО": ["CLOSED", "GESCHLOSSEN", "ZAMKNIĘTE", "FERMÉ", "CERRADO", "已关闭", "終了"],
        "ОБЩИЙ СЕРВЕР": ["SHARED SERVER", "GEMEINSAMER SERVER", "WSPÓLNY SERWER", "SERVEUR COMMUN", "SERVIDOR COMÚN", "共享服务器", "共有サーバー"],
        "Адрес сервера": ["Server address", "Serveradresse", "Adres serwera", "Adresse du serveur", "Dirección del servidor", "服务器地址", "サーバーアドレス"],
        "Один адрес используется для чата, голосований и баг-репортов. Для локальной проверки оставьте http://127.0.0.1:5249; для работы с другими игроками укажите опубликованный HTTPS-адрес команды.": ["One address is used for chat, polls, and bug reports. Keep http://127.0.0.1:5249 for local testing; to work with other players, enter the team's published HTTPS address.", "Eine Adresse wird für Chat, Umfragen und Fehlerberichte verwendet. Für lokale Tests bleibt http://127.0.0.1:5249; für andere Spieler tragen Sie die veröffentlichte HTTPS-Adresse des Teams ein.", "Jeden adres obsługuje czat, ankiety i zgłoszenia błędów. Do testów lokalnych pozostaw http://127.0.0.1:5249; aby pracować z innymi graczami, wpisz opublikowany adres HTTPS zespołu.", "Une même adresse est utilisée pour le chat, les sondages et les rapports de bugs. Conservez http://127.0.0.1:5249 pour les tests locaux ; pour les autres joueurs, indiquez l’adresse HTTPS publiée par l’équipe.", "Una sola dirección se usa para el chat, las encuestas y los informes de errores. Mantén http://127.0.0.1:5249 para pruebas locales; para trabajar con otros jugadores, indica la dirección HTTPS publicada del equipo.", "聊天、投票和错误报告共用同一个地址。本地测试请保留http://127.0.0.1:5249；与其他玩家联机时，请填写团队发布的HTTPS地址。", "チャット、投票、バグ報告には同じアドレスを使用します。ローカルテストではhttp://127.0.0.1:5249のままにし、他のプレイヤーと利用する場合はチームが公開したHTTPSアドレスを入力してください。"],
        "СОХРАНИТЬ СЕРВЕР": ["SAVE SERVER", "SERVER SPEICHERN", "ZAPISZ SERWER", "ENREGISTRER LE SERVEUR", "GUARDAR SERVIDOR", "保存服务器", "サーバーを保存"],
        "Прокрутка страницы": ["Page scrolling", "Seite scrollen"],
        "Прокрутить вверх": ["Scroll up", "Nach oben scrollen"],
        "Прокрутить вниз": ["Scroll down", "Nach unten scrollen"]
    };

    const nodes = new WeakMap();
    const attributes = ["placeholder", "title", "aria-label"];
    let language = "ru";
    let observer;

    function indexFor(lang) {
        return ({ en: 0, de: 1, pl: 2, fr: 3, es: 4, zh: 5, ja: 6 })[lang] ?? 0;
    }

    function translate(source) {
        if (language === "ru") return source;
        const trimmed = source.trim();
        const entry = dictionary[trimmed];
        if (!entry) return source;
        const leading = source.slice(0, source.indexOf(trimmed));
        const trailing = source.slice(source.indexOf(trimmed) + trimmed.length);
        return leading + (entry[indexFor(language)] || entry[0] || trimmed) + trailing;
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
            language = ["ru", "en", "de", "pl", "fr", "es", "zh", "ja"].includes(value) ? value : "ru";
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
