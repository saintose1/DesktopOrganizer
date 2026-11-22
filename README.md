# DesktopOrganizer

[![Build](https://github.com/saintose1/DesktopOrganizer/actions/workflows/build.yml/badge.svg)](https://github.com/saintose1/DesktopOrganizer/actions/workflows/build.yml)
· MIT License · [Русская версия ниже](#возможности)

**English.** An open-source desktop organizer for Windows 10/11 in the spirit of Fences: group your real
desktop icons into translucent panels that collapse, expand on hover, snap to screen edges and to each
other, and automatically collect new files by name rules (`*.png; *.lnk; Steam*`). Icons stay real
Explorer items — clicks, double-clicks, drag and drop work natively; no DLL injection, no overlay.
The interface is available in English and Russian (Settings → Language).

- **Install:** download `DesktopOrganizer-<version>-setup.exe` (per-user, no admin rights) or the portable
  zip from [Releases](https://github.com/saintose1/DesktopOrganizer/releases). No .NET runtime required.
- **Uninstall / recovery:** icons of collapsed groups carry the Windows *Hidden* attribute while the app
  runs. *Exit* from the tray menu and the uninstaller restore them; after a crash run
  `DesktopOrganizer.App.exe --RestoreHidden=true`. The list is kept in `%LOCALAPPDATA%\DesktopOrganizer\hidden-icons.json`.
- **Build:** `dotnet build DesktopOrganizer.sln` and `dotnet test` (.NET 10 SDK);
  `scripts/publish.ps1` makes the portable zip and, with Inno Setup 6, the installer.

Windows-приложение для организации настоящих значков рабочего стола в группы **Fences**.
Группы отображаются непосредственно на desktop Explorer, сохраняются между запусками
и продолжают работать при скрытом главном окне.

## Возможности

- Создание, переименование и удаление Fences.
- Drag за заголовок, resize за края/углы, collapse/expand прямо на desktop.
- Hover expand свёрнутой группы с возвратом ручного состояния после ухода курсора.
- Настоящие Explorer icons: штатные клики, double-click, multi-select, multi-drag
  и перенос значков или выделенной группы внутрь/наружу Fence. Это не WPF-копии.
- Назначенные значки раскрываются вместе с Fence: входят со стороны нижней кромки и доезжают до своих ячеек.
- Кастомизация шрифта, размера, bold/italic, выравнивания и цвета заголовка;
  независимые цвета/прозрачность header, body и border, live preview и color picker.
- Длительности collapse/expand в Settings. Fence и значки используют общий animation
  progress. Раскрытие и сворачивание позиционируют значки выбранной группы;
  пользовательские данные не записываются на каждом кадре.
- UI **Forest** (0.4.0): страницы «Обзор», «Группы», «Правила», «Настройки»; тёмная и светлая темы
  (переключение в Настройках, по умолчанию как в Windows); баннер и мшистые значки категорий.
  Подробнее: [docs/UI-FOREST.md](docs/UI-FOREST.md).
- Страница «Правила»: все маски авто-сортировки списком, добавление/удаление и предпросмотр совпадений на рабочем столе.
- Кнопка «Лесной стиль» в редакторе группы применяет тёмно-зелёный вид к выбранной группе.
- Свёрнутые группы держат значки скрытыми до раскрытия. После ручного сворачивания
  быстрый повторный клик доступен, пока курсор остаётся на заголовке. Опция
  «Всегда скрывать значки свёрнутых групп» скрывает их и в этом случае.
- Трей: крестик скрывает окно, один клик показывает его. «Выход» сохраняет состояние,
  возвращает временно скрытые значки и корректно освобождает ресурсы.
- Autostart для текущего пользователя через HKCU Run, без прав администратора.
- Привязка Fence при перетаскивании: к краям экрана и к соседним Fence (Alt — без привязки).
- Авто-сортировка: в «Оформление группы → Авто-сортировка» задаются маски имён
  (`*.png; *.lnk; Steam*`); новые файлы на рабочем столе сами попадают в первую подходящую Fence.
- Значки свёрнутых Fence скрываются атрибутом Hidden, если Windows
  разрешает менять атрибут. Значки без этого доступа возвращаются за экран после drop.
  Аварийное восстановление без запуска программы: `DesktopOrganizer.App.exe --RestoreHidden=true`.
- «Группы → Исправить общие ярлыки» переносит назначенные общие `.lnk` после подтверждения.
  Привязки сохраняются, оригиналы остаются в `shared-shortcut-backups` внутри папки данных.
  Перенос выполняется при наличии прав на исходный ярлык и свободного имени на личном рабочем столе.
- «Группы → Добавить поиск» создаёт перемещаемую кнопку поиска на рабочем столе.
  Она ищет ярлыки рабочего стола и меню «Пуск». Enter открывает результат, Esc закрывает окно.
- «Настройки → Обратная связь» открывает форму сообщения об ошибке в [GitHub Issues](https://github.com/saintose1/DesktopOrganizer/issues/new?template=bug_report.yml).
- Окно настроек создаётся только при открытии и освобождается при закрытии в трей (меньше памяти).
- Установщик без прав администратора и portable zip собираются `scripts/publish.ps1` и GitHub Actions.
- Язык интерфейса: русский или английский (Настройки → Язык), по умолчанию — язык Windows.
- JSON persistence, безопасная запись, обработка повреждённых данных,
  логирование и восстановление подключения после потери Explorer.

## Требования

- Windows 10/11 x64 с рабочим столом Explorer; приложение WPF, только для Windows.
- .NET 10 SDK для сборки (`global.json` допускает стабильные SDK 10.0.100 и новее в ветке 10.0).
- .NET 10 Desktop Runtime для запуска готовой framework-dependent сборки.
- При первом восстановлении зависимостей нужен доступ к NuGet.
- В меню desktop **Вид → Упорядочить значки автоматически** должно быть выключено.

## Сборка и тесты

Откройте PowerShell в корне клонированного репозитория:

```powershell
dotnet restore DesktopOrganizer.sln
dotnet build DesktopOrganizer.sln -c Debug --no-restore
dotnet build DesktopOrganizer.sln -c Release --no-restore
dotnet test DesktopOrganizer.sln -c Debug --no-build
dotnet test DesktopOrganizer.sln -c Release --no-build
```

Для `v0.1.0` проверены обе конфигурации: **0 ошибок/предупреждений,
303 теста проходят в каждой**. Unit tests не заменяют проверку настоящих mouse/OLE
жестов, визуальной плавности и конкретной конфигурации Explorer.

После точечных исправлений Explorer integration проверены Debug/Release и **360 тестов
в каждой конфигурации**. Проверки на настоящем Explorer включают создание desktop item,
удаление/уплотнение, rename, изоляцию обычного перемещения и восстановление состояния.
Границы этих API-проверок и ручной checklist: [Explorer stabilization](docs/STABILIZATION-EXPLORER-20261017.md).
Отдельно воспроизведён native OLE reflow скрытых значков и добавлен адресный возврат
вытолкнутых items в прежнюю парковку: [Native OLE parking](docs/FIX-NATIVE-OLE-PARKING.md).
Для короткой post-drop вспышки ускорены чтение текущих позиций и обработка отпускания мыши;
исчезновение видимого flash ещё требует физической проверки. Для чёрных областей с
Wallpaper Engine удалён layered/color-key режим Explorer ListView; используется его
штатная прозрачность и native input. Проверки и ограничения:
[Native wallpaper composition](docs/FIX-WALLPAPER-NATIVE-COMPOSITION.md).
Возврат вытолкнутых Explorer значков в parking сделан устойчивым (без «застывания»),
реакция на drop ускорена; idle-опрос Explorer снижен с 250 ms до 2 s:
[Post-drop flash](docs/FIX-DROP-FLASH-REDRAW-HOLD.md). Значки свёрнутых Fence дополнительно
скрываются атрибутом Hidden (журнал `hidden-icons.json`, «Выход» возвращает их). Физическая проверка ожидается.

## Запуск

```powershell
dotnet run --project src/DesktopOrganizer.App -c Release
```

После сборки запускайте EXE напрямую или создайте на него обычный ярлык Windows:

```powershell
& ./src/DesktopOrganizer.App/bin/Release/net10.0-windows/DesktopOrganizer.App.exe
```

Запуск сразу в трее:

```powershell
& ./src/DesktopOrganizer.App/bin/Release/net10.0-windows/DesktopOrganizer.App.exe --StartInTray=true
```

На странице **Fences** создайте группу и настройте внешний вид. Позицию и размер
меняйте мышью на desktop. Перенесите значки в её тело: header/рамка принимают
ввод Fence, content пропускает штатный ввод Explorer. На странице **Settings**
доступны autostart, hover expand и длительности анимации.

Закрытие окна сохраняет процесс в трее. Для полного завершения выберите
**ПКМ по значку трея → Выход**; перед заменой сборки завершайте приложение так же.
Автозапуск привязан к текущему EXE: после перемещения приложения включите его заново.
Установщик: `DesktopOrganizer-<версия>-setup.exe` из GitHub Releases или `scripts/publish.ps1`.

## Данные и приватность

По умолчанию группы, settings, recovery journals и логи находятся в
`%LOCALAPPDATA%\DesktopOrganizer`. Для проверки без изменения основного профиля:

```powershell
dotnet run --project src/DesktopOrganizer.App -- --StorageDirectory "$env:TEMP/DesktopOrganizer-manual-check"
```

GitHub используется как **приватный backup исходников**. Пользовательские
`fences.json`, settings, логи, dumps, `.tools`, SDK/cache и результаты проверок
исключены из Git. При необходимости отдельно сохраните папку профиля пользователя.

## Архитектура

- `DesktopOrganizer.Core` — immutable models, validation, чистая геометрия.
- `DesktopOrganizer.Infrastructure` — JSON, Windows settings, Win32/Shell COM interop.
- `DesktopOrganizer.App` — WPF/MVVM, DI, presentation, страницы и tray lifecycle.
- `tests/DesktopOrganizer.Tests` — unit/contract tests.
- `tools/` — исходники диагностических, benchmark и branding utilities;
  их локальные outputs не входят в repository.

Приложение не подменяет Explorer, не внедряет DLL и не создаёт полноэкранный overlay.

## Ограничения и checkpoints

Иерархия Explorer зависит от Windows; неизвестная конфигурация может давать
Disconnected. Shell API не гарантирует атомарный compositor commit между процессами.
Настоящие значки при анимации появляются/скрываются целиком, без fade или частичного
обрезания; частота зависит от нагрузки Explorer. Mixed-DPI и полный logout/login
с autostart требуют проверки на конкретном компьютере.

- `v0.1.0` — checkpoint с Lavender HUD, customization, tray и animation polish.
- `pre-ui-redesign-stable` — сохранённая точка возврата до редизайна интерфейса.
- `pre-explorer-stabilization-20261013` — локальная точка возврата перед исправлением
  wallpaper composition, удаления элементов и изоляции desktop drag.
- [Roadmap](ROADMAP.md), [Forest UI](docs/UI-FOREST.md), [прежний UI Lavender HUD](docs/UI-DESIGN-MAGICPATH.md),
  [animation pipeline / checklist](docs/FENCE-ANIMATION-PIPELINE.md),
  [tray lifecycle](docs/MILESTONE-TRAY.md), [Explorer rendering](docs/MILESTONE-RENDERING.md).

Milestone reports в `docs/` сохранены как история разработки; описание старого UI
в них относится к соответствующему этапу.
