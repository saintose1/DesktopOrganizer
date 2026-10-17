# Roadmap

## Стабилизация Explorer — октябрь 2026

- [x] Локальная точка возврата `pre-explorer-stabilization-20261013`, пользовательские данные сохранены отдельно.
- [x] Активация прозрачности ListView после создания/render/Z-order собственных поверхностей; обратимое восстановление исходных свойств.
- [x] Восстановление сброшенных Explorer background/styles без записи позиций и без repaint исправного состояния.
- [x] Shell create/delete/rename notifications; удаление только после подтверждения отсутствия, уплотнение только изменившейся группы.
- [x] Неполные snapshots не заменяют последний пригодный cache и не сбрасывают assignments; transient read не завершает живую Shell session и не восстанавливает grid посреди drag.
- [x] Drop позиционирует только изменившиеся группы; обычный outside drop не пишет Fence JSON и не reposition другие Fences.
- [x] Debug/Release: 0 warnings/errors, 350 tests; actual Shell/API smoke и восстановление всех 65 исходных позиций.
- [ ] Подтверждение реальными mouse/OLE gestures с Wallpaper Engine: новый ярлык, обычный drag рядом с expanded/collapsed Fences, multi-drag и hover.
- Отчёт и оставшиеся ручные проверки: [Explorer stabilization](docs/STABILIZATION-EXPLORER-20261017.md).

## Приватный checkpoint v0.1.0

- [x] README приведён к текущему Lavender HUD UI, customization, tray/autostart и animation pipeline.
- [x] Пользовательское состояние, логи, dumps, SDK/cache и временные outputs исключены из Git.
- Существующий `pre-ui-redesign-stable` сохраняется как точка возврата до UI.
- Commit/tag `v0.1.0` предназначены для приватного source backup, без installer или публичных binaries.

## Быстрый hover Fence

- [x] Искусственные задержки временного раскрытия и сворачивания убраны (0 мс); один переход через Dispatcher без native timer floor.
- [x] Повторные mouse move не перезапускают неизменившийся deadline.
- [x] Сохранены защита активных gestures и ручное collapsed state.

## Анимация collapse/expand

- [x] Плавная высота stable native surfaces с ease-in/out, без изменения нормальной высоты модели.
- [x] Независимые длительности раскрытия/сворачивания 0–3 с в Settings; 0 отключает анимацию.
- [x] Сохранение в settings.json, совместимость с прежними настройками без новых полей.
- [x] Настоящие icons и Fence используют один DWM progress/easing; один bulk/targeted redraw на изменившийся кадр.
- [x] Локальный slide/reveal и parking по целым glyph bounds, без изменения region чужого ListView.
- [x] Убран неустойчивый animation viewport; совместимость с legacy recovery journal сохранена.
- [x] Collapse завершает Shell hide при уходе последнего glyph; в последних кадрах только Fence height, без отложенного Shell finish.
- [x] Prepared Shell identities/coordinates, без SetRedraw(false) и JSON на кадрах; persistence после verification.
- [x] Убран повторный SetRedraw(true) на кадрах: одно завершение на transition, без двойного Explorer repaint.
- [x] Тесты easing, длительностей, сохранения, Dispatcher completion/cancellation и ошибок renderer.
- [ ] Физическая проверка плавности hover/кнопки, icon gestures и Win+D после анимации.
- [x] Animation cadence от DWM при скрытом WPF окне; coalescing очереди и абсолютное cubic easing.
- [x] Cached height-only native presentation: без WPF rasterization и роста DIB; пустой Fence без Explorer repaint.
- [x] Измерение 0,15/0,25/0,4 с, FPS/интервалов, ULW/region времени и количества repaint.
- [x] Тесты coalescing, ошибок clock, поздних callbacks и абсолютной геометрии при дробных координатах/DPI.

## Новый UI — Lavender HUD / MagicPath

- [x] Приватный MagicPath-проект, референс и dog branding на canvas.
- [x] Прототипы Fences и Settings, сборка и осмотр превью.
- [x] Исходные branding/reference assets сохранены в проекте.
- [x] Пользовательское согласование Lavender HUD.
- [x] Перенос в WPF: navigation, reusable theme resources, MVVM Settings.
- [x] Настоящий current-user autostart с чтением Windows state; GUI enable/readback/disable.
- [x] Multi-size application/tray ICO и подключение к EXE/window/taskbar/tray.
- [x] Debug/Release: 0 warnings/errors, 223 tests в каждой конфигурации. UI create/edit/restart, Settings, resize, hide/show проверены.
- [ ] Финальная физическая проверка tray single click, Win+D, OLE multi-drag и всех desktop жестов после UI.

## Трей перед редизайном UI

- [x] Постоянный native notification icon, один клик для существующего MainWindow.
- [x] Крестик скрывает окно; desktop integration продолжает работать.
- [x] Native menu «Открыть DesktopOrganizer» / «Выход».
- [x] Graceful exit: завершение операций, возврат parked icons, освобождение Explorer properties.
- [x] Unit tests lifetime; manual checklist в docs/MILESTONE-TRAY.md.
- [x] Подготовлено проверенное состояние для stable commit и tag `pre-ui-redesign-stable`.
- [x] Редизайн главного UI по согласованному MagicPath Lavender HUD.

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

Текущий stabilization: docs/STABILIZATION-EXPLORER-GESTURES.md.

- [x] Explicit Explorer gesture state; captured actual Shell selected set и общий group drop target.
- [x] Multi-drag in/out: весь dragged set, один affected layout proposal и commit.
- [x] Shared pre-DOWN selected set/initial positions; Shell hit-test различает пустой desktop и прозрачное место внутри icon.
- [x] Стабильный ListView backing во время marquee; без Fence свойства не меняются.
- [ ] Подтвердить native marquee pixels физическим жестом: A/B 0–6; новый основной режим — native outline.
- [x] Final read/layout/bulk внутри короткой STA redraw lease; readback/journal/save после redraw.
- [x] Удалены per-item fallback и повторные bulk writes при успешном post-drop; timestamps включают composition repaint.
- [x] Native lease/property и five-item positioning probes с восстановлением.
- [x] Обычный пустой desktop click не переключает composition; marquee guard только после drag threshold.
- [x] Selective NOREDRAW Z-order batch; immediate reorder correction, без повторной foreground activation.
  Native probe: 8 ListView raises / 2 Fence, 0 extra repaint/bitmap presents; обычный click подтверждён пользователем.
- [x] Пользователь подтвердил: обычный пустой desktop click больше не вызывает мигание.
- [x] Убрано переключение wallpaper/color-key backing при marquee в основной сборке.
  Native outline активен на период background selection; 0 наших composition redraw.
  Tradeoff — отсутствие полупрозрачной заливки selection rectangle.
- [x] Property probe: меняется только double-buffering bit; repeat/cancel/disable/shutdown restore.
- [x] API regression нового режима: empty/10 icons, drag/resize/collapse/restart;
  все 54 позиции и исходные свойства восстановлены. Debug/Release: 0 warnings/errors, 206 tests.
- [x] Пользователь подтвердил: marquee больше не мигает при постоянном outline.
- [x] Исправлена регрессия icon drag от постоянного отключения двойной буферизации:
  buffer включён для icon/OLE/positioning; scoped outline готовится до native DOWN.
- [x] No-op/assignment-only drop: 0 bulk, 0 redraw; изменяемый layout — 1 bulk/1 redraw.
  Freeze только на positioning, без enumeration/layout. Реальный probe ~4,7 ms.
- [x] Debug/Release 0 warnings/errors; 207 tests. API smoke: assignment/grid,
  drag/resize/collapse/restart/drop-out; все 54 исходные позиции восстановлены.
- [ ] Physical acceptance новой сборки: marquee без регрессии, single/multi icon
  drag desktop/Fence in/out без видимого flash, обычные клики.
- [x] GetSelection/IShellItemArray вместо пропускающего items обхода GetSelectedItem;
  реальный probe: 0,1,2,3 → полный set из 4, anchor и initial positions, пустой set без ошибки.
- [x] Regression: узкий Fence принимает 6 spread-out items целиком, высота растёт для всех rows; общий drop out.
- [x] Debug/Release: 0 warnings/errors; 206 tests в каждой конфигурации.
- [x] Actual API rendering regression: empty/10 icons, drag/resize/collapse/restart;
  полный snapshot 54 icons и original properties восстановлены после проверки.
- [ ] Manual acceptance: native marquee pixels, group OLE in/out, blink, double click,
  collapse/restart и быстрые реальные Fence gestures. Milestone не закрыт до подтверждения
  сценариев A–D реальной мышью. API replay не считается этим acceptance.

Текущее точечное исправление input: docs/FIX-EXPLORER-SELECTION.md.
Повторное исправление: async Raw Input handoff заменён узким WH_MOUSE_LL router,
с маршрутизацией только пропущенного DOWN до DefView. После сообщения пользователя
о застывании курсора удалено подавление WM_MOUSEMOVE/UP; captured Explorer получает
настоящие события. Debug/Release build без warnings/errors, 174 unit tests и
headless native hook lifecycle проходят. Отдельная GUI-копия и реальная surface
проверены; Explorer рамка выделения и multiple-icon drag проверяются пользователем.

- [ ] Добавить интеграционные проверки и диагностику.
- [ ] Проверить производительность и доступность UI.
- [ ] Подготовить публикацию, установщик и инструкции обновления.
