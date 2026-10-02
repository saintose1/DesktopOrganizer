# Визуальная стабильность Fence / Explorer icons

Проверка 2 октября 2026 года: Windows x64, SDK 10.0.401, реальный Explorer,
2560 × 1600, DPI 150%. Новые пользовательские возможности не добавлялись.
Core Fence, формат fences.json и Shell identities сохранены.

## Причины и исправления

### Ghosting и рассинхронизация изображений

Старая интерактивная рамка была обычным GDI child HWND. WM_PAINT копировал её
bitmap через BitBlt, тогда как body был layered window. Перемещение HWND и
Shell items обновлялось независимо; damage покрывал icon cutouts, а не весь
старый/новый след рамки. Прозрачный ListView запрашивал parent background через
WM_PRINTCLIENT. Старые пиксели рамки сохранялись в этом GDI repaint pipeline.
Сценарий с 10 реальными иконками воспроизвёл десятки повторяющихся заголовков.
Исходный снимок: .tools/rendering-smoke/20261002-001801/drag-40.png.

Дополнительно контроль без Fence показал: успешный SelectAndPositionItems меняет
GetItemPosition и LVM_GETITEMPOSITION, но изображение иконки может остаться прежним.
Обычный RedrawWindow, даже всего ListView, это не исправлял. Документированный
IFolderView2.SetRedraw(true) завершает Shell redraw и обновляет реальные иконки.
Контрольные снимки: .tools/motion-capture/after.png и
.tools/motion-capture-shell/after.png. Это наблюдение на текущем Explorer,
не универсальная характеристика всех версий Windows.

Оба собственных HWND теперь используют compositor-owned layered bitmaps.
WM_PAINT только валидирует update region; WM_ERASEBKGND собственной поверхности
не стирает фон. Рамка больше не попадает в GDI backing store родителя.
Drag сохраняет HWND/bitmaps и перемещает body/frame одним BeginDeferWindowPos /
DeferWindowPos / EndDeferWindowPos batch. Resize обновляет native buffers и
маленький WPF header; весь Fence в WPF не растеризуется.

Кадр с assigned icons: одна coalesced Shell пачка → SetRedraw(true) → native
presentation Fence → один synchronous repaint объединённого HRGN. Damage включает
старую/новую геометрию рамки и icon bounds. Каждый rectangle сначала клипируется
к ListView viewport; offscreen parking не превращает region в полный desktop.
Полный host repaint нужен только при включении/восстановлении background lease.
WS_EX_COMPOSITED и искусственный frame delay в production не используются.

### Collapse / expand

Раньше collapse preview менял поверхность до общего QueueAll; перед Shell move
выполнялось сохранение модели/recovery и verified enumeration. Также был обход
Preview для IsCollapsed. Новое изображение header и icons завершалось в разное время.

Теперь desktop button вызывает отдельный CollapseRequested path:

1. Блокируются конфликтующие команды редактора, hover/timer repaint и новый gesture.
2. Вычисляются normal/offscreen positions по существующим assignments.
3. STA worker перемещает настоящие Shell items и завершает Shell redraw.
4. Обновляются body/header и единый damage region.
5. Фиксируется presentation timestamp, затем выполняются readback и persistence.
6. При ошибке positioning/verify/save восстанавливается прежнее представление.

Нормальные return positions заранее записываются в parked-icons.json на startup,
после drop и в конце gesture. Collapse не записывает recovery на critical path.
IsCollapsed и Height остаются прежними полями модели. Expand возвращает
те же offsets; startup collapsed Fence снова паркует assigned icons.

Hover и двухсекундный discovery/reconcile не могут нарисовать прежнюю модель
между presentation и durable commit. Реальный replay отдельно проверяет эту race
для шести переходов. После неудачного сохранения gesture original model снова
синхронизируется с surface и реальными icons.

### Alpha artifact

Старый body был выше ListView и имел прямоугольные alpha/region cutouts вокруг
иконок, чтобы их не закрывать. В этих прямоугольниках показывался untinted wallpaper;
между иконками был полупрозрачный фон. Это и давало различие прозрачности.

Теперь body находится ниже настоящего ListView и не имеет icon cutouts.
ExplorerIconLayer обратимо включает WS_EX_LAYERED / native color-key background,
оставляет double buffering и отключает LVS_EX_TRANSPARENTBKGND, чтобы ListView
не копировал wallpaper/parent поверх body. Text background остаётся CLR_NONE.
Explorer продолжает владеть item drawing, selection, запуском и OLE.

Интерактивная рамка имеет настоящий window region «header + borders».
Это необходимо и для input, и для WS_CLIPSIBLINGS: одного нулевого pixel alpha
недостаточно, чтобы Explorer рисовал в прямоугольнике чужого sibling HWND.

## Окончательная rendering architecture

Снизу вверх внутри одного проверенного SHELLDLL_DefView:

~~~text
Explorer wallpaper / desktop parent
  DesktopOrganizer.FenceBackground — own WS_CHILD | WS_EX_LAYERED, disabled input
  SysListView32 — Explorer-owned items + text, transparent native background
  DesktopOrganizer.FenceSurface — own WS_CHILD | WS_EX_LAYERED, header/borders region
normal application windows — выше desktop subtree
~~~

На этой конфигурации: Progman=0x1015E, WorkerW=0, DefView/host=0x10168,
SysListView32=0x1016A. Это диагностические значения, не константы в коде.
Оба собственных HWND создаются сразу с parent=DefView. Icon pixels принимают
input в ListView; прозрачные области достигают Explorer DefView. Header/border
обрабатывают native mouse input. Background не перехватывает клики.

Layer lease сохраняет original extended/ListView styles и background colors
в desktop-presentation.json до изменения, через temp/flush/replace.
Shutdown восстанавливает их. После crash следующий startup восстанавливает lease
только при совпадении проверенных HWND/PID; новое поколение Explorer использует
свой original state. Существующий foreign layered renderer не перехватывается.

Собственные DIB/HBITMAP остаются в нашем процессе. Fake icon rendering,
DLL injection и новый fullscreen overlay отсутствуют.

Документированные основы:
[IFolderView2.SetRedraw](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifolderview2-setredraw),
[layered windows](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features),
[child update regions](https://learn.microsoft.com/en-us/windows/win32/gdi/child-window-update-region),
[ListView styles](https://learn.microsoft.com/en-us/windows/win32/controls/extended-list-view-styles).
Explorer HWND hierarchy и совместимость с его renderer требуют проверки на других builds.

## Измерения

Baseline: .tools/rendering-smoke/20261002-001801/latency.json.
После: .tools/rendering-smoke/20261002-012402/latency.json.
Readback — та же внешняя контрольная процедура: request toggle → независимое чтение
Explorer positions. Она включает стоимость enumeration, а не только visual path.
По три collapse/expand измерения, 10 assigned icons. Это маленькая выборка,
зависимая от нагрузки и числа items на desktop; результат не является гарантией FPS.

| Контроль | До: mean / max, мс | После: mean / max, мс |
|---|---:|---:|
| Collapse + независимый readback | 41,43 / 54,31 | 27,91 / 28,75 |
| Expand + независимый readback | 43,88 / 54,91 | 34,15 / 37,05 |
| Новый collapse visual path, 7 операций | отдельно не измерялся | 8,45 / 12,79 |
| Новый expand visual path, 7 операций | отдельно не измерялся | 12,79 / 18,09 |

Visual timestamp снимается после Shell positioning + native presentation/repaint,
до внешних debug observers, verify и persistence. Это CPU/Win32 completion latency
от collapse callback, не измерение физической mouse-to-photon/DWM scanout latency.
Полный прежний visual path отдельно не инструментировался; его число не выдумывается.

| Стресс replay: 60 updates назад-вперёд | Mean / max, мс | Icon mean / max, мс | Explicit repaints | Bitmap presents |
|---|---:|---:|---:|---:|
| 10 icons: drag | 8,65 / 10,56 | 3,38 / 4,07 | 61 | 0 |
| 10 icons: resize | 12,29 / 14,54 | 3,43 / 4,09 | 61 | 120 |
| Без assigned icons: drag | 2,58 / 4,41 | 0 / 0 | 60 | 0 |
| Без assigned icons: resize | 4,24 / 6,11 | 0 / 0 | 60 | 120 |

61 учитывает финальный committed update. Repaint count считает явные запросы нашего
damage flush; внутренние WM_PAINT Explorer без injection не подсчитываются.
Bitmap presents — собственные UpdateLayeredWindow, не число WPF renders.
В production нет per-frame logs. Harness pacing/capture не являются production throttling.

## Проверки

- Debug: полный dotnet build с restore — 0 warnings/errors; 146/146 tests.
- Release: build — 0 warnings/errors; 146/146 tests.
- --rendering-smoke: реальные 10 icons, empty Fence, быстрые alternating drag/resize,
  collapse/expand, hover/timer race, shutdown/reinitialization из JSON, offscreen restore.
- --icons-smoke: release path при отключённом polling fallback, assignment/grid,
  resize relayout, model/persistence, collapse/restart/expand, API replay drop-out.
- Исходные Explorer styles/colors после smoke совпали с исходными; lease journal удалён.
- Исходные icon positions восстановлены после native smoke и отдельной GUI-проверки.
- Три отдельных запуска App: два с развёрнутой группой и один с сохранённым
  collapsed fixture. В третьем подтверждены 10 parked icons и только header
  высотой 54 физических pixels. Connected без ошибок. Win+D через реальную
  клавиатуру минимизировал MainWindow, обе desktop surfaces остались видимы;
  реальные экранные снимки просмотрены. Это проверка cold startup из JSON,
  не подтверждение физического клика collapse.
- Просмотрены drag-40/resize-40/collapsed/restart-collapsed/restart-expanded PNG:
  повторяющихся рамок и alpha cutouts нет, новые positions соответствуют модели.

Programmatic API replay не подтверждает физический OLE drag, double click или
субъективную плавность глазами. Эти пункты ниже остаются ручным acceptance.

## Изменённые файлы

- Core/Desktop: DesktopDamage.cs (новый), IconPlacement.cs, DesktopContracts.cs.
- Infrastructure: DependencyInjection.cs; Interop/ExplorerIconLayer.cs (новый),
  NativeFenceSurface.cs, Win32DesktopHost.cs, DesktopFenceInput.cs, NativeMethods.cs,
  DesktopIconService.cs, ShellFolderView.cs, DesktopDiagnostics.cs.
- App/Desktop: FenceVisualTransaction.cs (новый), DesktopIntegrationService.cs,
  DesktopIconCoordinator.cs, InteractionPerformance.cs; ViewModels/MainWindowViewModel.cs.
- Tests: DesktopDamageTests.cs, FenceVisualTransactionTests.cs (новые),
  IconPlacementTests.cs, MainWindowViewModelTests.cs.
- Tools: Benchmarks/RenderingSmoke.cs (новый), DesktopSmoke.cs, Program.cs;
  Diagnostics/Program.cs.
- Документация: AGENTS.md, README.md, ROADMAP.md, PERFORMANCE.md, этот отчёт.

## Ограничения

- Проверен текущий Explorer и 150%. Реальные 100/125%, mixed DPI, WorkerW и restart
  explorer.exe не проверялись; Explorer не перезапускался ради тестов.
- HWND hierarchy не является стабильным публичным Shell contract; неизвестный host
  или уже layered ListView другого renderer дают Disconnected без перехвата.
- Transparency lease относится ко всему Explorer ListView. Color key RGB(1,2,3)
  делает совпадающие пиксели прозрачными; intrinsic bitmap backgrounds самих icons
  сохраняются. Другие desktop utilities/HDR/новые Windows builds требуют проверки.
- Shell items и наши HWND принадлежат разным процессам: Win32 не даёт общей DWM
  transaction. Bulk positioning, presentation ordering и synchronous damage paint
  минимизируют разрыв; гарантировать один hardware scanout на всех системах нельзя.
- После принудительного завершения процесса свойства ListView остаются до следующего
  startup/recovery, а collapsed icons — offscreen. parked-icons.json содержит return
  positions; закрыв App, можно восстановить их Diagnostics --restore-positions.

## Короткий manual checklist

Из корня проекта запустить:

~~~powershell
& ./.tools/dotnet/dotnet.exe run --project src/DesktopOrganizer.App --no-build
~~~

1. Открыть desktop через Win+D. Быстро двигать/resize Fence без assigned icons.
2. Перенести 5–10 обычных desktop icons в Fence. Быстро двигать назад-вперёд и
   менять размер за края/углы: проверять старые footprints прямо во время жеста.
3. Collapse/expand кнопкой header несколько раз: исчезновение/возвращение icons
   должно происходить вместе с поверхностью, offsets и нормальная высота сохраняются.
4. На светлом/тёмном участке wallpaper проверить подписи и пространство между icons:
   нет прямоугольных holes или другой opacity вокруг labels.
5. Double click обычного shortcut; вынос icon из Fence и возврат. После drop icon
   сразу занимает layout position; движение Fence не должно вызывать teleport.
6. Закрыть App в collapsed состоянии и запустить снова. Проверить header, hidden icons,
   затем expand и сохранённые positions. Повторить Win+D и переключение обычных окон.
7. По возможности повторить при 100/125/150% и на нескольких мониторах.

Native probes запускать при закрытом App: они используют настоящий общий desktop,
сохраняют исходные позиции и возвращают их в finally. StorageDirectory изолирует
JSON/логи, но не Explorer. Для fresh debug evidence:

~~~powershell
& ./.tools/dotnet/dotnet.exe tools/DesktopOrganizer.Benchmarks/bin/Debug/net10.0-windows/DesktopOrganizer.Benchmarks.dll --rendering-smoke
& ./.tools/dotnet/dotnet.exe tools/DesktopOrganizer.Benchmarks/bin/Debug/net10.0-windows/DesktopOrganizer.Benchmarks.dll --icons-smoke
~~~
