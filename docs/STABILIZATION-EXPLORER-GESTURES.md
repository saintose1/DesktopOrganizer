# Explorer gestures: проверка по реальному видео

3 октября 2026. Только marquee composition, целый selected group и post-drop flash.
**Milestone ещё не закрыт:** physical acceptance A–D пока не получен.

## Актуальный icon-drag fix (3 октября, 11:41)

Пользователь подтвердил отсутствие мигания при marquee, но сообщил мигание при
icon drag. Новый лог 11:32:33–11:33:33 содержит обычные desktop drops без наших
SetRedraw/SelectAndPositionItems. Поэтому единственную причину нельзя приписать
нашему post-drop bulk: постоянное отключение LVS_EX_DOUBLEBUFFER в предыдущем
fix затронуло также native Explorer icon/OLE rendering. Отдельно найден лишний
Shell redraw при assignment-only drop: MovedTargets=0, BulkCalls=0, но redraw=1.

Исправления:

- Idle/icon presses/OLE/positioning снова используют двойную буферизацию.
  При readonly Shell hit-test пустого фона до forwarded native DOWN выключается
  только LVS_EX_DOUBLEBUFFER. Layered/key/bg/textBg/parent/Z-order не меняются,
  full redraw нет. На release/cancel буферизация возвращается. Новый press
  завершает предыдущий outline scope, чтобы быстрый следующий icon drag не
  унаследовал его. Alpha marquee остаётся выключенной во время самого selection.
- Final enumeration и pure layout выполняются на STA до redraw lease. Если
  targets=0, нет SetRedraw(false/true). Для настоящего move PIDLs/grid готовятся
  перед коротким false → один bulk → true. Gesture/capture проверяются до чтения
  и повторно перед move. Durable save/readback/rollback contract сохранён.
- Assignment-only native contract покрыт unit test. Настоящий Explorer probe
  подтверждает zero-target counters=0 и отсутствие SetRedraw в timeline.

Debug/Release build: 0 warnings/errors; 207/207 tests в каждой конфигурации.
Native five-item probe: layout@10,164 ms, SetRedraw(false)@14,242–15,302 ms,
bulk@15,303–18,796 ms, true@18,797–18,913 ms. Lease ~4,671 ms вместо прежних
~18–26 ms с enumeration/layout внутри. Assignment-only: 0 bulk/0 redraw вместо
0 bulk/1 redraw. Изменяемые targets: 1 bulk/1 redraw как раньше, без второго write.
Наши composition RedrawWindow: 0; это не подсчёт внутренних Explorer/DWM paints.
Smoke `.tools/icons-smoke/20261003-114102`: genuine icons assignment/grid,
Fence drag/resize/collapse/expand/collapsed restart/drop-out/persistence проходят;
все 54 исходные позиции восстановлены. Native properties: idle styles=0x14894C30,
selection=0x14884C30, after=0x14894C30, backing ex/bg/textBg неизменны, shutdown
возвращает original. API/adapter replay не подтверждает физический mouse/OLE flash.

**Новый scoped режим пока требует visual acceptance.** Проверить обычные клики,
marquee 4–5 icons, desktop→desktop, desktop→Fence, Fence→desktop, single/multi
drag, double click и быстрые Fence gestures. Одного успешного переноса по API
недостаточно, чтобы объявить отсутствие видимого мигания.

## Предыдущий marquee fix (3 октября, 11:19)

Пользователь подтвердил: обычные desktop click и Ctrl+click больше не вызывают
мигание. Marquee продолжал мигать. В свежем логе 11:13:54–11:14:03 по-прежнему
две composition RedrawWindow на gesture: BeginNativeSelection возвращал native
wallpaper backing, который перекрывает нижний Fence body; EndNativeSelection
возвращал прозрачный keyed ListView. Fence исчезал и появлялся, хотя его HWND и
bitmap не пересоздавались. Ранее подготовленный outline candidate был только
диагностическим и не использовался основной App.

Теперь основной режим сохраняет одинаковые ListView properties на весь период,
пока существуют Fence: WS_EX_LAYERED/color key остаются, LVS_EX_DOUBLEBUFFER
выключен. Explorer сам рисует native outline selection, без alpha fill. Флаг
устанавливается до жеста один раз при создании первой surface, не на mousemove
или при press/up. Begin/end только отмечают gesture state, не выполняют native
style writes и RedrawWindow. Это исключает позднюю смену режима после первого
alpha marquee frame. При removal последнего Fence/shutdown/crash recovery
восстанавливаются journaled original properties. Глобальные Windows настройки
не меняются; fake icons/custom selection rendering нет.

Компромисс: contour вместо современной полупрозрачной fill. Документированный
LVS_EX_DOUBLEBUFFER включает и alpha marquee; alpha blended color-key пиксели
не остаются равны ключу. Это точечный обход ограничений keyed foreign ListView,
не обещание полноценной alpha-composition Explorer с нашими background HWND.
Legacy alpha/guard оставлены только для A/B stage 4/5, новый основной режим stage 6.

Проверки новой реализации:

- Debug/Release build 0 errors/warnings; 206/206 tests в каждой конфигурации.
- Реальный property probe: idle = begin = end (ex=0x80000, styles=0x14884C30,
  bg=0x30201), 0 дополнительных composition RedrawWindow и Fence bitmap presents.
  Repeat begin/end, disable во время selection и shutdown незавершённого selection
  восстанавливают original ex=0, styles=0x14C94C30, bg/textBg=CLR_NONE.
- `.tools/rendering-smoke/20261003-111844`: empty/10 реальных icons, 60 drag +
  60 resize updates каждого Fence, collapse/expand/restart и concurrent hover.
  Все 54 original positions и исходные свойства Explorer восстановлены.
  С 10 icons: drag mean 10,604 ms, resize 15,128 ms; hide 7,279–11,929 ms,
  restore 10,862–14,573 ms. Это API replay, не physical smoothness PASS.
- Input/Z-order API probe: 8 ListView raises / 2 Fence, 0 repaint/bitmap presents;
  header/grip interactive, body transparent, original input после shutdown.
- App запущена с сохранённой пользовательской группой; startup log подтверждает
  `stable native outline marquee=true`. Пользовательские JSON не заменялись.
- **Физическое отсутствие мигания ещё не подтверждено.** Повторить большую marquee
  рядом с Fence и через body, затем multi-drag in/out и быстрый Fence drag/resize.

## Дополнение: мигание при пустом клике и неполная группа (3 октября, 10:35)

Свежий пользовательский лог 10:19:17–10:19:20 показал на каждом обычном клике
Composition.native.begin → RedrawWindow.composition.ListView → release →
Composition.native.end → ещё один RedrawWindow. Причина: native marquee guard
включался на DOWN, даже без selection drag. Теперь MarqueeCompositionGate ждёт
положительный Shell background hit-test и движение за системный drag threshold.
Клик, jitter, поздний результат после UP и icon drag не переключают composition.
Настоящие MOVE/UP продолжают проходить к Explorer, custom marquee не добавлялся.
В логе реального нового запуска 10:35:39–10:35:46 повторные click traces уже не
содержат Composition.native.begin/end и двух composition repaint. Это подтверждает
удаление переключений на click; визуальная оценка остаётся за реальным экраном.

Причина потери multi-selection установлена реальным native probe: выбраны ListView
indices [0,1,2,3], прежний GetSelectedItem(start)/start=index+1 вернул только [1,3].
GetSelection(false) / IShellItemArray возвращает полный набор [0,1,2,3] одним Shell
selection snapshot. Stable IDs читаются GetDisplayName(SIGDN_DESKTOPABSOLUTEPARSING),
начальные позиции — GetItemPosition; COM references и строки освобождаются в finally.
Для пустого selection Explorer возвращает ERROR_NOT_FOUND: это обрабатывается как
пустой set, не ошибка. Final drop target остаётся общим для группы, отдельные bounds
не используются. Сетка уже умеет увеличивать Height узкого Fence для всех items.

Native --selection-enumeration PASS: selected snapshot=4, press snapshot=4,
initial positions=4, настоящий hit anchor входит в set, empty selection=0.
Исходные selection/focus восстановлены; позиции не менялись. Добавлено 7 unit cases:
пять gate scenarios и два narrow-Fence whole-group in/out с разным DPI.
Debug/Release: 206/206 tests, 0 warnings/errors.
Повторные GUI/API checks: .tools/icons-smoke/20261003-103408,
.tools/rendering-smoke/20261003-103522. Drag/resize/collapse/expand/restart проходят;
после каждого восстановлены все 54 позиции и original Explorer styles/colors.
Это API replay и проверка настоящего selected-set reader, не physical OLE acceptance.

Пользователь затем сообщил: мигание осталось. Поэтому отсутствие composition
switch в trace не объявляется окончательным визуальным исправлением.
Внесён второй точечный fix: EnsureStacking собирает только действительно неверно
расположенные собственные HWND; один DeferWindowPos batch с NOREDRAW, без GDI erase,
без перестановки уже правильно расположенных body. Раньше одна поднятая Explorer
ListView запускала SetWindowPos для всех body и header. EVENT_OBJECT_REORDER
запускает коррекцию сразу, без ожидания release/readback/poll; обычные icon location
events не запускают этот обход. Повторный SetForegroundWindow не выполняется,
если desktop root уже foreground. Добавлены агрегированные stacking trace points.

Реальный input/Z-order probe PASS: 8 подъёмов настоящего ListView, две Fence;
header выше icons, body ниже; 0 дополнительных repaint и 0 bitmap presents;
header/grip интерактивны, content transparent, исходный desktop input target возвращён.
Повторный GUI/API smoke .tools/icons-smoke/20261003-104816 PASS, все 54 позиции
восстановлены. Финальные Debug/Release build/tests: 0 warnings/errors, 206/206.
В реальном App-логе 10:39:40 также есть physical user drop: selected=4,
assignment proposal=4, bulk=4, один Shell redraw completion; последующий Fence drag
и collapse/expand с четырьмя icons выполняются. Native click pixels после второго fix
ещё требуют пользовательской проверки; API Z-order probe не выдаётся за mouse acceptance.

Короткая ручная проверка новой сборки:
1. Повторить обычные клики на пустом desktop: Fence не мигает; trace не содержит
   Composition.native.begin/end или RedrawWindow.composition для клика.
2. Marquee выбрать 4–6 items, отпустить курсор внутри небольшого Fence:
   назначаются все items; при необходимости Fence увеличивает высоту.
3. Вынести весь set наружу; повторить in/out, collapse/expand и Fence drag/resize.

Предоставленное пользователем видео разобрано: 47 секунд, 1920×1080/60 fps.
Оно подтверждает, что прежние API/contract checks не означали правильного rendering
реальных mouse/OLE gestures. Пользовательское fences.json не заменялось тестовым.

## Marquee: известный дефект и незавершённая диагностика

Изменяемый чужой HWND: SysListView32 0x10154, Explorer PID 13008.
Parent — SHELLDLL_DefView 0x10152; Progman 0x10148; WorkerW в этой конфигурации 0.
Это диагностический снимок, HWND/PID не зашиты в код.

ExplorerIconLayer добавляет WS_EX_LAYERED, выключает LVS_EX_TRANSPARENTBKGND и
задаёт color-key background RGB(1,2,3). Alpha marquee смешивается с backing:
пиксели перестают точно совпадать с ключом и становятся видимым тёмным прямоугольником.
Наш body использует premultiplied bitmap: компоненты меньше alpha, прозрачные строки
очищаются. Признаков неинициализированных transparent pixels в коде не найдено.

Прежний native guard возвращал original ListView properties перед selection. Это
проверяло свойства, но не реальные pixels. На ПК работают Wallpaper Engine и Windhawk;
чёрный parent/WM_PRINTCLIENT backing остаётся гипотезой до физического A/B.
Нельзя объявить восстановление свойств окончательным исправлением видео.

Найден дополнительный input bug: WindowFromPoint для прозрачного image/label padding
настоящей иконки возвращает DefView. Это ошибочно считалось пустым desktop и включало
marquee composition с двумя лишними repaint ListView при icon drag. Теперь общий
selection task выполняет настоящий readonly LVM_HITTEST и Shell selected enumeration
до forwarded DOWN. Только отсутствие hit item включает native guard. Icon press
не переключает composition. MOVE никогда не подавляется, настоящий UP проходит Windows;
собственная рамка selection не рисуется.

### A/B инструмент

При закрытом App:

~~~powershell
& ./.tools/dotnet/dotnet.exe tools/DesktopOrganizer.Benchmarks/bin/Debug/net10.0-windows/DesktopOrganizer.Benchmarks.dll --composition-ab
~~~

Инструмент не назначает и не перемещает иконки. Original ListView properties
восстанавливаются после переключения/закрытия; меняется visibility только своих HWND.

| Режим | Слои | Native guard |
|---|---|---|
| 0 | Исходный Explorer, ни одного Fence HWND | Выключен |
| 1 | Только ListView layered/color key; наши HWND скрыты | Выключен |
| 2 | Только body, исходный ListView; key не включается даже временно | Выключен |
| 3 | Только header/border, исходный ListView | Выключен |
| 4 | Полный Fence/ListView | Выключен |
| 5 | Полный Fence, исправленный input path | Включен |

В каждом режиме: кнопка режима → «Показать рабочий стол» → большая marquee через
светлый участок обоев. Записать, в каких режимах появляется чёрная область. В режиме
2 body может быть закрыт исходным ListView: это проверка влияния HWND, не новый renderer.
Кнопка captures сохраняет реальные кадры в .tools/composition-ab. Результат A/B пока
не получен. Вариант HWND siblings DefView под Progman проверялся: HWND visible, но
header/body не были видны в реальных pixels. Этот вариант исключён из product path.
Benchmarks apphost получил manifest с поддержкой layered child, совпадающий с App.

## Почему multi-drag назначал только часть группы

Резервный ApplyMovedIcons выбирал Fence по центру каждой final Bounds. Explorer group
сохраняет расстояния: несколько items оказывались снаружи и не назначались. Selection
читалась только после drag threshold; поздний hit-test исходной press position и
ошибочная классификация padding могли пропускать snapshot и включать per-item fallback.

Теперь в момент press начинается один shared Shell task. IFolderView2.GetSelection
возвращает полный IShellItemArray; Shell item даёт parsing identity. Начальные
позиции читаются GetItemPosition с актуальным ListView origin. Snapshot копируется,
sequence token отвергает поздние результаты. Click/marquee/cancel не становятся drop.

Если hit item принадлежал selected set, dragged IDs — весь set; иначе один hit item.
Release point определяет одну последнюю открытую Fence в её body, включая padding.
Header/collapsed Fence не принимают assignment. Все доступные IDs назначаются target
независимо от final Bounds отдельных items. Drop наружу снимает assignment со всего
captured set. Drop в другое приложение его не снимает. Per-item fallback удалён.

## Flash и visual transaction

Прежний путь: Explorer finish → два чтения positions → layout/recovery file →
SetRedraw(false) → bulk → SetRedraw(true). До freeze оставалось окно промежуточного
display; retries могли повторять reposition. Ошибочно распознанный background press
добавлял два полных composition RedrawWindow. Damage counter их не учитывал.

Новый dedicated post-drop path после release и завершения Explorer capture:

1. Selection уже захвачена; UI копирует immutable Fences, scales и monitors.
2. Один STA job: SetRedraw(false) → final Shell snapshot → pure Core group layout
   → один SelectAndPositionItems для changed targets → SetRedraw(true) в finally.
3. Readback при включённом redraw; повторяются только чтения, не bulk.
4. UI continuation даёт Dispatcher возможность показать visual commit.
5. Recovery journal и один durable fences.json save, затем model/VM feedback.

В callback layout нет HWND/UI/дополнительных COM/file I/O. Lease освобождается и при
исключениях. Save failure/conflict возвращает прежние positions; recovery может делать
дополнительный batch — это не успешный drop. JSON остаётся существующим синхронным
store на UI, после native redraw и отдельного continuation. Async storage API не вводится.
Наш дополнительный damage repaint drop подавляется. Assignment-only change не
растеризует surface заново. Polling не создаёт assignment и не повторяет finished drop.

| Операция успешного drop | До | После |
|---|---|---|
| Bulk после нашего layout | 1, при retry до 3 | Максимум 1; 0 если targets не меняются |
| Shell redraw completion | По числу batch | 1 в native reconciliation job |
| Composition RedrawWindow при press по padding icon | 2, ошибочный native guard | 0 при корректном Shell hit |
| Дополнительный damage repaint assignment | Уже подавлялся предыдущей правкой | 0 |
| Per-item reconciliation | Возможен при пропущенном snapshot | Удалён |
| fences.json save | Один на proposal | Один на group proposal; без изменения модели 0 |

Это количества нашего кода, а не paint/DWM frames чужого процесса. Уже состоявшийся
OLE repaint не отменяется нашим кодом. Отсутствие видимого flash требует B/C/D мышью.

## Timestamps и измерения

ExplorerInteractionTrace запускается до selection job. Один bounded timeline после
gesture: selection read, drag start, raw release, delivery EVENT_SYSTEM_DRAGDROPEND,
layout, SetRedraw(false/true), SelectAndPositionItems, composition/damage RedrawWindow,
Fence update и persistence. Mouse moves, identities/paths не логируются. Aggregate:
selected count, hash dragged identity, target Fence ID, assigned count, batches/redraw.

Native Shell API probe с 5 items: false begin 1,517 ms; false end 1,851 ms;
layout 12,222 ms; bulk 16,199–20,107 ms; true 20,108–20,421 ms. Lease около 18,6 ms.
Один bulk/один redraw; все 54 исходные positions восстановлены. Это API probe,
не измерение physical drop latency или доказательство отсутствия flash.

## Rendering architecture сохранена

Wallpaper → собственный layered body (disabled, WS_EX_TRANSPARENT) → настоящий
Explorer SysListView32 с reversible color-key lease → layered header/border HWND.
Parent наших HWND — проверенный DefView. Frame region исключает content; header,
button и resize принимают input, body нет. Drag использует стабильные HWND и один
DeferWindowPos batch. Сохранены настоящие Explorer items, parking collapse, mouse/OLE.
Fake icons/custom marquee/fullscreen overlay/injection не добавлялись.

Color-key/native-marquee guard пока без pixel acceptance. Окончательный rendering
fix должен быть выбран по физическому A/B, не по статусу Connected.

## Проверки

- SDK 10.0.401; Debug/Release build: 0 errors/warnings.
- 206 unit tests Debug/Release: 0 failed/skipped. Геометрия/snapshot/порядок/rollback
  контрактов — не имитация реального Explorer.
- Native planned transaction: 5 real items, 1 bulk/1 redraw; все original positions возвращены.
- Actual API/adapter smoke: 3 настоящих shortcuts, assignment/grid/move/resize,
  collapse/expand/collapsed service restart/drop out/persistence.
  .tools/icons-smoke/20261003-011913. Test selection с настоящими IDs подана в adapter:
  это не physical selection capture/OLE.
- API rendering: empty/10 real icons, по 60 drag/resize updates, 6 hide/show с concurrent
  hover/timer, collapsed restart. .tools/rendering-smoke/20261003-011950.
  Drag mean 9,361 ms, resize mean 11,463 ms. Native hide 7,662–12,346 ms,
  restore 11,851–20,291 ms. Не mouse-driven PASS.
- После API проверок восстановлены все 54 positions, grid flags и original styles/colors;
  presentation journal удалён. Explorer не перезапускался.
- Computer Use управляет App/diagnostics, но не предоставляет desktop child HWND как
  drag target. A–D реальной мышью ожидают проверки пользователя.

## Manual acceptance

### Уточнение пользователя и устранённое переключение при marquee, 3 октября

Обычный click больше не вызывает мигание, Ctrl+click тоже. Остался именно marquee.
`BeginNativeSelection` возвращает original ListView wallpaper backing и вызывает
RedrawWindow, `EndNativeSelection` возвращает color-key backing и делает вторую
перерисовку. Между ними Explorer рисует wallpaper поверх нашего нижнего body:
это объясняет временное исчезновение/возвращение Fence, даже когда HWND и его
bitmap не пересоздаются. Просто убрать guard нельзя: alpha fill смешивается с
color key, получаются непрозрачные тёмные пиксели вместо wallpaper.

Новый основной вариант (A/B stage 6): только на период background selection убрать
`LVS_EX_DOUBLEBUFFER`, сохранив WS_EX_LAYERED, color key, background/text background
и Z-order. Explorer сам рисует штатную outline marquee; custom rendering нет.
После gesture возвращается double-buffering bit, backing остаётся тем же;
при выключении/shutdown — original properties.
Дополнительных composition RedrawWindow в этой ветке 0 вместо 2;
это не счётчик внутренних Explorer/DWM paints. Во время selection нет современной
полупрозрачной заливки. Исправление требует physical visual acceptance, не только
property probe: реальные pixels/smoothness Explorer не подтверждаются unit/API replay.

`DesktopOrganizer.Benchmarks.exe --outline-composition` проверяет реальные свойства,
повторный begin/end, отсутствие новых Fence bitmap presents, disable в середине
selection и восстановление при shutdown незавершённого selection. Иконки не
перемещает, mouse gestures не синтезирует. Это не тест видимого мигания.

- [ ] A: большая штатная marquee на светлых обоях; App off/no Fence/full Fence.
  Нет чёрного backing. При проблеме A/B 0–5 выше.
- [ ] B: marquee выделяет 4 icons; drag за одну selected icon и release в body Fence.
  Все 4 сразу assigned/grid; движение Fence перемещает все 4.
- [ ] C: вынести эту selection наружу; все 4 обычные desktop icons.
- [ ] D: повторить B/C; нет заметного flash.
- [ ] Single icon, double click безопасного файла, icon in/out, Fence drag/resize,
  collapse/expand/hide/show, Win+D и restart сохраняют поведение.

Не запускать App и A/B/positioning probe одновременно: Explorer общий даже при разных
StorageDirectory. GUI data/artifacts лежат отдельно в игнорируемой .tools.

## Основные изменённые файлы

- Core/Desktop/ExplorerGesture.cs — press snapshot/initial positions, gesture states, native drop contract.
- Core/Desktop/FenceIconLayout.cs — общий group target, body padding, одна раскладка целого set.
- Infrastructure/Interop/DesktopIconEvents.cs — shared selection read, Shell hit classification, lifecycle generation.
- Infrastructure/Interop/DesktopIconService.cs — selected set, final read/layout/bulk под одной redraw lease.
- Infrastructure/Interop/ExplorerIconLayer.cs, ExplorerInteractionTrace.cs — учёт composition repaint и bounded timestamps.
- Infrastructure/Interop/Win32DesktopHost.cs — trace/input composition, защита drop commit, диагностическое разделение слоёв.
- Infrastructure/DependencyInjection.cs — общий trace для Host и Shell service.
- App/Desktop/DesktopIconCoordinator.cs, IconLayoutTransaction.cs — dedicated group reconciliation, async continuation, rollback.
- Tests/ExplorerGestureTests.cs, IconLayoutTransactionTests.cs — snapshot, target, single write, deferred save, native contract.
- Infrastructure/Interop/MarqueeCompositionGate.cs, Tests/MarqueeCompositionGateTests.cs — composition только при реальном background drag.
- Infrastructure/Interop/ShellFolderView.cs — GetSelection/IShellItemArray, полные Shell selected identities без cursor enumeration.
- Infrastructure/Interop/Win32DesktopHost.cs, DesktopIconEvents.cs, ExplorerBackgroundMouse.cs, NativeMethods.cs — selective NOREDRAW stacking, immediate reorder event и устранение повторной foreground activation.
- Infrastructure/Interop/DesktopDiagnostics.cs, Tools/Benchmarks/InputHitSmoke.cs — проверка двух Fence после реальных ListView raises, correct frame selection для hit query.
- Infrastructure/Interop/DesktopDiagnostics.cs, Tools/Diagnostics/Program.cs — обратимые real API probes и counters.
- Tools/Benchmarks/CompositionAB.cs, DesktopSmoke.cs, DesktopOrganizer.Benchmarks.csproj — A/B, честный adapter replay, manifest.
- README.md, ROADMAP.md и этот отчёт — результаты и открытый physical acceptance.

Документированные основы: [layered windows/color key](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features),
[ListView backing/alpha marquee](https://learn.microsoft.com/en-us/windows/win32/controls/extended-list-view-styles),
[GetSelectedItem](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifolderview2-getselecteditem),
[GetSelection](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifolderview2-getselection),
[SetRedraw](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifolderview2-setredraw).
