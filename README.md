# Anthology Launcher Next

Новая платформа лаунчера для S.T.A.L.K.E.R. Anomaly Anthology. Она живёт отдельно от текущего Python-лаунчера и пока не участвует в пользовательских обновлениях.

## Что уже работает

- современное desktop-приложение на WPF + Blazor Hybrid;
- главная с запуском модпака, оригинальной Anomaly и `Lan_anthology.bat`, новости и встроенное видео;
- библиотека с тремя разделами старого лаунчера: проекты разработчиков, проекты модмейкеров и альтернативные решения;
- настройки Anomaly: DX11–DX8, AVX, debug, звук, prefetch, user.ltx, карта теней и игровой Relay Chat;
- Community API с лентой, голосованиями, приёмом баг-репортов и SignalR-чатом;
- подписанные ECDSA-манифесты, SHA-256, безопасные относительные пути;
- загрузка с приоритетных зеркал, продолжение HTTP-загрузки и автоматическое переключение источника;
- прямые HTTPS-источники (включая GitHub/Google Drive при наличии прямой ссылки) и публичные ссылки Яндекс Диска через официальный API;
- транзакционная установка файлов с резервными копиями, журналом и ручным откатом последнего обновления;
- рабочий центр обновлений: ручной выбор доверенного ключа, проверка manifest, загрузка, безопасная распаковка и установка;
- отдельная вкладка установки: пути игры/MO2, updater-канал и подписанные комплектные пакеты из `App\InstallMedia`;
- баг-репорты с обязательным воспроизводимым шаблоном, HTTPS-ссылкой и небольшими вложениями до 5 МБ;
- запуск `Chernobyl Relay Chat.exe` строго из выбранного корня игры;
- Реальный чат как отдельный канал сразу под «Разработчику» и предварительное подключение при включённом параметре «Реальный чат всегда»;
- встроенный менеджер MO2: выбор профиля и запуска, включение модов, изменение порядка с резервной копией и скрытый запуск через runtime MO2/USVFS;
- раздел «Информация» с системными требованиями, описанием оригинала, модпака и читаемыми карточками сюжетов;
- обычный EXE/MSI Setup из стабильной комплектной папки `App\Setup`;
- portable-настройки в `E:\AnthologyLauncherNext\Data` без автоматического подключения старой сборки;
- отдельный CLI-релизер для ключей, воспроизводимых ZIP-пакетов и подписанных манифестов.

Это рабочая alpha-версия, а не готовый публичный релиз. Учётные записи, постоянная база сообщества, админ-панель, self-update и финальный production deployment ещё находятся в следующих этапах.

## Структура

```text
AnthologyLauncherNext/
├── src/Anthology.Launcher/        Windows-приложение
├── src/Anthology.Community.Api/   новости, чат, опросы, баг-репорты
├── src/Anthology.Update.Core/     проверка, загрузка и установка обновлений
├── src/Anthology.Releaser/        инструменты разработчика сборки
├── src/Anthology.Contracts/       общие сетевые контракты
└── tests/                         автоматические тесты
```

Подробности: [архитектура](docs/ARCHITECTURE.md), [протокол обновлений](docs/UPDATE-PROTOCOL-V1.md), [план разработки](docs/ROADMAP.md).

## Локальный запуск

Нужны Windows 10/11, .NET SDK 10 и WebView2 Runtime.

```powershell
cd E:\AnthologyLauncherNext\Source
dotnet restore Anthology.Next.slnx
dotnet build Anthology.Next.slnx -c Release
dotnet test Anthology.Next.slnx -c Release --no-build

$env:ASPNETCORE_URLS = "http://127.0.0.1:5249"
dotnet run --project src/Anthology.Community.Api
```

В другом терминале:

```powershell
$env:ANTHOLOGY_COMMUNITY_API = "http://127.0.0.1:5249"
$env:ANTHOLOGY_GAME_ROOT = "D:\Games\Anomaly-1.5.3-Anthology"
dotnet run --project src/Anthology.Launcher
```

Если API недоступен, интерфейс показывает встроенный демонстрационный контент. Лаунчер не изменяет игровые файлы при просмотре интерфейса.

## Первый запуск updater

1. В «Установке» вручную выберите корень Anomaly и папку Mod Organizer 2.
2. Укажите URL/путь подписанного `manifest.json`.
3. Выберите публичный `.pem`-ключ, которому доверяете.
4. Сохраните настройки и нажмите «Проверить» в центре обновлений.
5. Установка начнётся только после отдельного нажатия «Скачать и установить».
6. После успешной установки кнопка «Откатить» восстанавливает файлы предыдущей транзакции.

Для безопасной проверки без настоящей игры выполните `scripts\New-LocalDemo.ps1`. Он создаст отдельный sandbox, локальное зеркало, ключи и manifest. Затем выберите выведенные пути в интерфейсе.

## Создание пакета

```powershell
dotnet run --project src/Anthology.Releaser -- keys generate `
  --private .local/keys/production.pem `
  --public .local/keys/production.pub.pem

dotnet run --project src/Anthology.Releaser -- package create `
  --input C:\build\payload `
  --artifact C:\build\out\anthology.zip `
  --manifest C:\build\out\manifest.json `
  --id anthology-core --name "Anthology Core" --version 2.2.0 `
  --kind Modpack --install-root modpack `
  --private-key .local/keys/production.pem --key-id production-01 `
  --mirror github=https://example.org/anthology.zip `
  --mirror yandex-disk=https://disk.yandex.ru/d/example
```

Закрытый ключ нельзя коммитить или передавать пользователям. В production он должен храниться в менеджере секретов и использоваться только релизером.

## Комплектная установка игры

Папка `App\InstallMedia` может поставляться вместе с EXE и должна содержать `manifest.json`, `install.public.pem` и `packages\*.zip`. Стабильная ссылка внутри подписанного manifest имеет вид `bundle:///packages/anthology-base.zip`.

Комплект создаётся тем же релизером:

```powershell
.\scripts\New-InstallMedia.ps1 `
  -Input C:\build\anthology-base `
  -OutputDirectory C:\build\InstallMedia `
  -PrivateKey C:\secure\install.private.pem `
  -PublicKey C:\secure\install.public.pem `
  -Version 2.2.0
```

В распространяемый комплект копируется только публичный ключ. Приватный ключ остаётся у release owner.

## Обычный Setup

Для классического мастера установки положите `AnthologySetup.exe`, `Setup.exe`
или `AnthologySetup.msi` в `deploy\Setup`. При локальной публикации папка станет
`E:\AnthologyLauncherNext\App\Setup`. Бинарные файлы установщика можно держать в
`Setup\bin` либо `Setup\Payload\bin`; процесс запускается с рабочим каталогом
`Setup`, поэтому относительные пути сохраняются.

## Встроенный MO2

Лаунчер читает профили из `profiles\*\modlist.txt` и варианты запуска из
`ModOrganizer.ini`. Изменение активности и порядка выполняется атомарно, а перед
записью создаётся `modlist.txt.anthology-backup`. Запуск использует официальный
CLI MO2: `-p <profile> run -e <executable>`. Окно Mod Organizer не требуется,
но его runtime и USVFS остаются обязательными для виртуальной файловой системы.

## Изоляция старого проекта

Проект хранится отдельно в `E:\AnthologyLauncherNext`. У него собственная Git-история, исходники, сборки, API и инструменты релизера. Внутри проекта нет старого Python-лаунчера и нет относительных ссылок на прежний репозиторий.

Готовое приложение запускается через `E:\AnthologyLauncherNext\Launch Anthology Next.cmd`. Путь существующей игры намеренно не задан: просмотр интерфейса не запускает и не изменяет Anthology. Подключение игры и нового updater-канала будет отдельным явно контролируемым этапом после тестирования.
