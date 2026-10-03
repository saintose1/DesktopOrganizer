# DesktopOrganizer — Lavender HUD: WPF implementation

Согласованный пользователем MagicPath Lavender HUD перенесён в WPF.
Точка возврата до UI: `pre-ui-redesign-stable`, commit `e494aa8`.

## Визуальный источник

Приватный MagicPath project `457094278800355328`.

| Экран | Component | Approved revision |
| --- | --- | --- |
| Fences + navigation | 457094337931677696 | 457097189223706624 |
| Settings | 457097838971731968 | 457097838971731969 |

Перенесены black/lavender palette, sidebar, dog branding, технические подписи,
карточки групп, editor panel, отдельный Settings, спокойные рамки и selected state.
Прототипные метки удалены. Никаких CRT effects поверх текста; текстура только
в предоставленном decorative PNG. Native WindowChrome сохраняет resize/maximize.
При меньшем окне редактор и Settings прокручиваются; минимум 900×600 DIP.

## Файлы / архитектура

- App/Themes/LavenderHud.xaml: brushes, caption/card, button/primary/navigation,
  TextBox, ListBoxItem, startup toggle, scrollbar, focus/hover/disabled states.
- Views/MainWindow.xaml: shell, navigation, WindowChrome; code-behind только
  presentation minimize/maximize/close. FencesView и SettingsView — отдельные views.
- MainWindowViewModel: текущие CRUD commands сохранены; navigation не теряет draft.
- SettingsViewModel: reads/writes через constructor-injected IStartupRegistration,
  refresh при открытии страницы, ошибки и возврат toggle при failed write.
- Infrastructure/Settings/WindowsStartupRegistration: HKCU
  `Software\Microsoft\Windows\CurrentVersion\Run`, value `DesktopOrganizer`.
  Quoted EXE + `--StartInTray=true`, без admin; disable удаляет только свой value.
  Никакого JSON enabled flag. Максимальная длина command — 260 символов.
  [Microsoft Run documentation](https://learn.microsoft.com/windows/win32/setupapi/run-and-runonce-registry-keys).
- App.xaml.cs: DI и необязательный запуск в трее; desktop integration запускается
  независимо от видимости окна.
- Assets/Branding: исходный dog PNG, application.ico, упрощённый tray.ico,
  оба с 16/20/24/32/48/64/128/256 frames. tools/DesktopOrganizer.Branding — pipeline.
- App csproj: EXE ApplicationIcon + WPF resources; tray asset output/publish.
- WindowsTrayIcon: собственный LoadImage HICON, Dispose → DestroyIcon.
- Tests: Settings errors/refresh/quoting/path checks, navigation+drafт+persistence,
  восемь реально декодируемых PNG frames обоих ICO.
- AGENTS/README/ROADMAP обновлены. Core, Fence model, persistence format и
  Explorer desktop rendering/input не менялись.

## Проверки 2026-10-03

- Debug и Release solution build: 0 errors, 0 warnings.
- Debug и Release tests: 223 passed, 0 failed, 0 skipped в каждой конфигурации.
- Реальное WPF-окно при 150% DPI: Fences и Settings осмотрены.
- UI создание группы, редактирование имени, Save, restart: имя и Id восстановлены.
- Resize окна примерно до 907×607 DIP: обе страницы остаются пригодны,
  длинный редактор/Settings scroll вместо выхода controls за окно.
- UI autostart enable: проверен реальный HKCU value с quoted EXE; disable:
  value отсутствует. Исходное значение отсутствовало и восстановлено.
- X из реального GUI: окно скрыто, процесс и tray остаются. Native tray-open
  callback: тот же MainHWND, окно visible, второй экземпляр не создаётся.
- Native tray Exit: процесс штатно завершён; isolated state пережил restart.
- Release `--StartInTray=true`: MainHWND=0 при startup, tray зарегистрирован, Fence создан; tray-open создаёт единственное окно, Exit завершает процесс и восстанавливает Explorer properties.

GUI-проверки выполнялись с отдельным `.tools/ui-check/data`. Данные пользователя
не заменялись тестовыми. Регистрация tray проверена реальным Shell_NotifyIconGetRect;
это не считается физическим кликом на значке или оценкой его пикселей в taskbar.

## Известные ограничения и оставшаяся полировка

- Физический tray click, Win+D и все реальные Explorer/OLE mouse gestures
  после темы не воспроизведены автоматизацией: средство не адресует taskbar/desktop.
  Unit tests и tray callback не выдаются за эти сценарии.
- Реальный logout/login Windows ради проверки Run не выполнялся; команда запуска
  в трее проверяется отдельно. Task Manager / системная политика могут блокировать
  запуск независимо от наличия Run value; toggle отражает нашу запись Run.
- Если приложение переместили или переключили Debug/Release output, нужно включить
  автозапуск заново из используемого EXE. Installer/package не реализован.
- Проверены 150% DPI и compact resize; 100/125% и mixed-DPI требуют ручного осмотра.
- Дальнейшая визуальная полировка: точные typography/spacing и brand на малых
  размерах. Сильные CRT effects/анимации не добавлялись.

## Короткий manual checklist

1. Запустить EXE; создать группу, сменить имя и размеры, сохранить.
2. Открыть Settings, включить autostart, закрыть/открыть страницу, выключить.
3. X → окно скрывается, Fence остаётся. Один tray click → то же окно.
4. Через tray выбрать Exit, запустить снова → данные восстановлены.
5. Проверить drag/resize/collapse Fence, multi-select/multi-drag настоящих icons,
   drag in/out, double click и Win+D.
6. По желанию включить autostart и выполнить настоящий вход в Windows:
   приложение находится в трее, Fences работают, окно открывается по click.
