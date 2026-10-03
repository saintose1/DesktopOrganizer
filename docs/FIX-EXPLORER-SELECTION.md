# Explorer selection rectangle: input fix

2 октября 2026 года. Scope: только штатный mouse input; rendering, assignment,
Core Fence, JSON и desktop icon positioning не изменены.

## Причина

ExplorerIconLayer делает SysListView32 layered с color-key background,
чтобы настоящие icons рисовались поверх единого Fence background.
По документированным правилам layered-window hit testing color-key pixels
пропускают mouse input. Пустой pixel поэтому минует SysListView32 и попадает
в его родитель SHELLDLL_DefView. Тот не запускает native ListView selection loop.
Клик по непрозрачным pixels самих icons продолжает работать.

Это не лишний SetCapture у Fence: DesktopFenceInput вызывает его только после
попадания в header/collapse/resize. Body был disabled, frame region уже исключал
content. Исправлять только их HTTRANSPARENT недостаточно для color-key holes
в чужом Explorer HWND. HTTRANSPARENT также не является универсальным механизмом
перенаправления в окна другого потока.

Первое исправление через асинхронный Raw Input handoff пользователь проверил:
рамка по-прежнему не запускалась. В коде этот путь не исключал доставку исходного
нажатия DefView, зависел от позднего GetGUIThreadInfo/capture и обрывал forwarding
при любом capture. Это допускает конкурирующие DOWN/UP и преждевременную отмену
до входа в selection loop. Без новой проверки мышью нельзя утверждать, какой
именно interleaving происходил на этой версии Explorer. Повторное исправление
устраняет саму конкуренцию: один получатель и одна последовательность событий.

В повторном исправлении появилась регрессия: callback возвращал ненулевой результат
для WM_MOUSEMOVE. WH_MOUSE_LL тем самым подавлял не только доставку сообщения, но
и реальное перемещение системного курсора. PostMessage в ListView не перемещает
курсор Windows. Текущая версия никогда не подавляет и не пересылает WM_MOUSEMOVE:
движение проходит через CallNextHookEx сразу, до проверки HWND и capture.
Заменяется только пропущенный background DOWN; дальнейшее движение и release
обрабатывает настоящий Explorer capture.

Предыдущая реальная native проверка --input-hit-smoke (в этот раз не запускалась):

| Область | Получатель WindowFromPoint |
|---|---|
| Пустой desktop до lease | SysListView32 0x10188 |
| Header при Fence | DesktopOrganizer.FenceSurface 0xDC0946 |
| Content при Fence | SHELLDLL_DefView 0x10182 |
| Resize edge при Fence | DesktopOrganizer.FenceSurface 0xDC0946 |
| Пустой desktop при Fence | SHELLDLL_DefView 0x10182 |
| После освобождения lease | снова SysListView32 0x10188 |

Прямой WM_NCHITTEST собственному frame: header/edge = HTCLIENT (1),
content = HTTRANSPARENT (-1). Это readonly запрос, не симуляция жеста.
Значения HWND являются результатами диагностики, не константами приложения.

## Исправление

1. Собственный background имеет WS_DISABLED + WS_EX_TRANSPARENT.
2. WM_NCHITTEST frame проверяет именно screen point из lParam; content не
   объявляется HTCLIENT. Настоящий window region по-прежнему header + borders.
3. ExplorerBackgroundMouse устанавливает WH_MOUSE_LL на отдельном потоке с
   собственным message loop. HWND, class, PID, parent chain, visibility, bounds,
   capture и menu ownership проверяются до перенаправления. Eligible только
   background press, который иначе попадёт в проверенный DefView.
4. На callback исходный DOWN подавляется только после успешного добавления
   единственной замены в FIFO delivery queue. Координаты берутся из hook packet,
   не из позднего GetCursorPos/GetMessagePos. Header/resize, icon pixels, обычные
   приложения и уже захваченные жесты не перехватываются.
5. WM_MOUSEMOVE всегда проходит без перенаправления и подавления, даже пока
   background gesture активен. После захвата ListView получает настоящие MOVE/UP.
   Если очень быстрый UP пришёл до захвата, его копия завершает очередь ListView;
   исходный UP также проходит Windows и сохраняет нормальное состояние кнопки.
   Список Explorer сам рисует rectangle, выбирает items и управляет capture.
6. Ctrl/Shift передаются с заменённым DOWN и ранним UP. Флаги ЛКМ этих сообщений
   берутся из самих DOWN/UP, без зависимости от позднего async polling. Их очередь
   сохраняет порядок; искусственного таймера и обработки MOVE в ней нет.
7. Смена capture на другое окно или другая кнопка отменяет routing через
   WM_CANCELMODE; исходное событие нового владельца проходит. Отмена не создаёт
   искусственный UP/drop. Потеря HWND, ошибка очереди или закрытие дают fail open.
8. В callback нет Shell COM, JSON, logging или синхронных cross-process messages.
   Delivery и обычная desktop activation выполняются после callback, на отдельном
   потоке. Фокус не переводится при уже завершённом/отменённом жесте.
9. Raw Input receiver больше не пересылает mouse messages; он только наблюдает
   release для существующего post-drop reconciliation. При disconnect/shutdown
   hook снимается, поток останавливается; отменяется только наша начатая доставка.

Почему потребовался hook: color-key holes находятся в чужом ListView, а не в
нашем frame. HTTRANSPARENT frame не исправляет их hit testing; поздний Raw Input
не умеет отменить исходное событие. WH_MOUSE_LL даёт документированную возможность
заменить конкретное событие до его доставки. Hook выполняется в нашем процессе,
без DLL injection или subclassing Explorer. Fullscreen input HWND отсутствует.

SendMessage для DOWN не используется: ListView может войти в modal loop.
Распаковка/упаковка координат знаковая, с проверкой 16-bit mouse-message диапазона;
координаты вне него не оборачиваются в другую точку.

Interactive: header, collapse button, resize edges/corners.
Mouse-transparent: body и content; вне Fence нет наших input HWND.
Renderer/Z-order и persistence не менялись.

Документированные основы:
[layered window hit testing](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features),
[WM_NCHITTEST / HTTRANSPARENT](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-nchittest),
[LowLevelMouseProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc),
[mouse capture](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-mouse-input).

## Проверки и ограничения

- Debug и Release build: 0 warnings/errors; 174/174 tests, 0 skipped.
- 28 routing/coordinate cases покрывают собственный background gesture до/после
  capture Explorer, быстрый release, чужой capture/новую кнопку, modifiers,
  disconnect, signed coordinates и native hook packet layout. Отдельная регрессия
  проверяет, что MOVE проходит при любом capture и в течение серии перемещений.
  Это не tests Explorer.
- --input-routing-lifecycle проверяет настоящие создание/освобождение hook и
  message loop с нулевыми desktop handles: любые пользовательские события проходят
  без вмешательства. Ни Explorer properties, ни focus, ни icon positions не меняются.
- После разрешения пользователя старая копия закрыта штатно через Computer Use.
  Исправленная копия запущена с отдельным --StorageDirectory и одним пустым Fence.
  Проверены Connected, настоящий child HWND на desktop и снимок его видимых pixels
  при DPI 150%. До запуска сохранены исходные позиции настоящих icons для возврата
  после проверки. Пользовательское fences.json не изменялось.
- Тестовая копия закрыта штатно; Explorer properties восстановлены. Readonly
  сравнение до/после подтвердило, что все 54 исходные позиции icons совпадают.
  Затем запущен обычный пересобранный App: 4 прежние группы, прежние 8 assignments,
  прежнее collapsed state и статус Connected. Рабочий стол оставлен открытым.
- Computer Use не предоставляет адресуемое окно рабочего стола/desktop child HWND
  для drag. Пользователю предложена физическая проверка selection; до его ответа
  selection rectangle и multiple-icon OLE drag остаются неподтверждёнными.
  Не объявлять manual acceptance пройденным.

Ранее проверенные native hit targets приведены выше для объяснения rendering path,
а не как свежая проверка нового router. Core/JSON/assignment/rendering не изменены.
Неизвестные HWND/capture и невозможные coordinates не перенаправляются. Поведение
конкретного Explorer modal selection loop требует ручного подтверждения. Windows
может отключить low-level hook при зависании его потока; dedicated thread убирает
из callback UI/JSON/Shell задержки, но это платформенное ограничение остаётся.

## Изменённые файлы

- Infrastructure/Interop/ExplorerBackgroundMouse.cs — scoped hook, dedicated loop,
  ordered delivery, capture/cancel policy и cleanup.
- Infrastructure/Interop/DesktopIconEvents.cs — raw receiver только для наблюдения;
  отдельный lifecycle router, logging и повторное подключение.
- Infrastructure/Interop/Win32DesktopHost.cs — logger и сообщения о сбое/retry input.
- Infrastructure/Interop/NativeMethods.cs — documented hook/message-loop interop.
- Infrastructure/Interop/DesktopDiagnostics.cs — lifecycle probe и readonly
  диагностика положения курсора, capture, выбранных icons и состояния ЛКМ.
- Tests/BackgroundMouseRouteTests.cs — routing/modifier/coordinate/native layout cases.
- Tools/Benchmarks/Program.cs — запуск безопасного lifecycle probe.
- Tools/Diagnostics/Program.cs — readonly input state и отдельный локальный backup
  позиций для обратимой GUI-проверки.
- README.md, ROADMAP.md, этот отчёт.

## Manual checklist

Закрыть старую копию обычным способом и открыть существующий desktop shortcut.
Он запускает пересобранный Debug exe. Альтернативно из корня проекта:

~~~powershell
& ./.tools/dotnet/dotnet.exe run --project src/DesktopOrganizer.App --no-build
~~~

1. На desktop вне Fence зажать ЛКМ и протянуть стандартную рамку Explorer.
   Курсор должен свободно двигаться во время удержания кнопки и после отпускания.
2. Выделить 3–5 icons, затем перенести выделенную группу обычным Explorer drag.
3. Повторить около Fence и начиная с пустого content pixel внутри него.
4. Проверить header drag, все resize edges/corners и collapse button.
5. Double click icon, group/icon drag in/out, collapse/expand и restart.
   Сохранённые assignments/positions и обычные Explorer действия должны остаться.
6. Проверить Ctrl/Shift selection, отмену через Escape и клики в другом приложении.

Безопасная проверка native hook lifecycle без переключения desktop/focus:

~~~powershell
& ./.tools/dotnet/dotnet.exe tools/DesktopOrganizer.Benchmarks/bin/Debug/net10.0-windows/DesktopOrganizer.Benchmarks.dll --input-routing-lifecycle
~~~

Native hit probe запускать только при закрытом App:

~~~powershell
& ./.tools/dotnet/dotnet.exe tools/DesktopOrganizer.Benchmarks/bin/Debug/net10.0-windows/DesktopOrganizer.Benchmarks.dll --input-hit-smoke
~~~

Она включает настоящую presentation lease и затем восстанавливает её.
Не считать PASS этого probe проверкой физической рамки выделения.
