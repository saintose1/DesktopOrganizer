# Roadmap

## Этап 1 — Каркас

- [x] Solution и проекты Core, Infrastructure, App.
- [x] Ссылки между проектами с независимым Core.
- [x] WPF-окно и основа MVVM.
- [x] Generic Host и Dependency Injection.
- [x] Файловое логирование с ротацией.
- [x] Общие настройки сборки, .gitignore и документация.
- [x] Успешная проверка `dotnet build` (SDK 10.0.401; 0 ошибок, 0 предупреждений).

## Этап 2 — Модель и настройки

- [x] Уточнить сценарий: создание, изменение, удаление, восстановление после перезапуска.
- [x] Модель Fence и правила имени, координат и размеров в Core.
- [x] JSON-хранилище групп в Infrastructure, безопасная замена файла.
- [x] Сохранение повреждённого JSON отдельно и предупреждение в UI.
- [x] Тесты модели, ViewModel и хранения: 41 тест, все проходят.

## Этап 3 — UI групп

- [x] Реализовать создание, переименование и удаление групп.
- [x] Добавить редактирование X/Y/Width/Height и сохранение применённых изменений.
- [x] Добавить команды ViewModel и проверки ввода.
- [x] Проверить сценарий в WPF: две группы, изменение, удаление, перезапуск и повреждённый JSON.
- [ ] Добавить настройку внешнего вида и визуальное размещение групп.
- [ ] Проверить клавиатурную навигацию, DPI и несколько мониторов.

## Этап 4 — Интеграция с рабочим столом

- [x] Исследовать native child/layered windows вместо reparent WPF или fullscreen overlay.
- [x] Интерфейс Core, изолированный Win32 discovery/host в Infrastructure/Interop.
- [x] Проверка HWND/class/PID/parent, диагностика Progman/WorkerW/DefView/icon list.
- [x] Отдельная desktop surface для каждого видимого Fence и live-синхронизация.
- [x] Автоматический повторный discovery раз в 2 секунды и восстановление surfaces в коде.
- [x] Проверка реального desktop, Show Desktop, создания/изменения/удаления и перезапуска приложения.
- [x] Manual checklist и read-only диагностика HWND/стилей/реальных экранных пикселей.
- [x] Сборка без ошибок/предупреждений, все 50 тестов проходят.
- [ ] Проверить восстановление после реального перезапуска Explorer.
- [ ] Проверить конфигурацию WorkerW, mixed DPI и несколько мониторов.
- [x] Чтение/организация иконок вынесены в отдельный запрошенный milestone этапа 6.

## Этап 5 — Интерактивные desktop Fences

- [x] Drag за header без пересоздания HWND и bitmap при перемещении.
- [x] Resize всех краёв/углов, стандартные курсоры, минимум 180 × 90 DIP.
- [x] Общая геометрия header actions для rendering и hit testing.
- [x] Collapse/expand, IsCollapsed в JSON, Height хранит нормальную высоту.
- [x] Live preview в модели; одна запись после interaction; rollback при отмене/ошибке.
- [x] Мониторные work areas, отрицательные координаты, защита доступности заголовка.
- [x] Build: 0 ошибок/предупреждений; 82 теста, включая geometry DPI и persistence.
- [x] Запуск, реальная desktop surface, native parent/стили и восстановление после запуска.
- [ ] Подтвердить мышью drag, все 8 resize handles, collapse/expand и restart после жестов.
- [ ] Реальные 125/150%, несколько мониторов, Win+D и Explorer restart во время gesture.
- [x] Пользователь запросил следующий отдельный milestone desktop icons.

## Этап 6 — Производительность и настоящие desktop icons

Функциональность реализована, программные проверки реального Explorer проходят.
Ручной acceptance ещё не закрыт; checklist в docs/MILESTONE-ICONS.md.

- [x] Измерения исходного pipeline и отчёт docs/PERFORMANCE.md.
- [x] Кеши DPI/геометрии, лёгкий native drag, WPF rasterization только заголовка.
- [x] Прозрачное тело и region hole; input тела достигает SysListView32.
- [x] Настоящий ListView reader, data-only remote memory, Shell stable identities.
- [x] Позиционирование через IFolderView2 на STA worker, coalescing move batches.
- [x] Post-drop assignment/removal/transfer без перехвата штатного OLE Explorer.
- [x] Grid, совместное движение, resize relayout, offsets и JSON migration.
- [x] Временное отключение Explorer snap-to-grid и штатное восстановление настройки.
- [x] Debug Mean/Max/Updates/sec после gesture, без per-pixel logging.
- [x] 112 unit tests: assignment, matching, transforms, grid, resize, migration, rollback, selection regression.
- [x] Реальный Explorer при 150%: три ярлыка, move/resize/collapse/restore/removal;
  исходные позиции после smoke восстановлены.
- [x] Полный повторный запуск App, две группы и две настоящие иконки восстановлены.
- [ ] Реальные быстрые mouse gestures, восемь resize handles, OLE drop и запуск иконки.
- [ ] Win+D, реальные 100/125%, mixed DPI, WorkerW и Explorer restart.

## Этап 7 — Визуальная стабильность и синхронизация

- [x] Воспроизведён ghosting старого GDI frame с 10 настоящими icons.
- [x] Body/frame — стабильные layered HWND; uniform body ниже Explorer icon layer.
- [x] Обратимая прозрачность ListView, recovery исходных properties после crash.
- [x] Shell SetRedraw(true), DeferWindowPos собственных HWND, один clipped damage HRGN.
- [x] Dedicated desktop collapse/expand: positioning/repaint → verify → persist, rollback.
- [x] Return journal заранее, без JSON/recovery записи в visual critical path.
- [x] Агрегированные Debug update/icon/repaint/latency metrics, без frame logging.
- [x] Debug/Release: 0 warnings/errors; 146 тестов зелёные.
- [x] Actual Explorer API replay: empty/10 icons, drag/resize, collapse/expand, restart;
  исходные позиции и ListView properties восстановлены после проверки.
- [x] Просмотрены реальные снимки: single current frame, uniform alpha, icons следуют за Fence.
- [ ] Подтвердить физические быстрые gestures, OLE in/out, double click и субъективную
  синхронность на мониторе по docs/MILESTONE-RENDERING.md.
- [ ] Другие Explorer builds, реальные 100/125%, mixed DPI, WorkerW, crash/restart Explorer.

## Этап 8 — Подготовка к выпуску

Текущее точечное исправление input: docs/FIX-EXPLORER-SELECTION.md.
Native hit-test диагностика и 158 unit tests проходят; физическую Explorer
рамку выделения и перенос нескольких icons необходимо подтвердить вручную.

- [ ] Добавить интеграционные проверки и диагностику.
- [ ] Проверить производительность и доступность UI.
- [ ] Подготовить публикацию, установщик и инструкции обновления.
