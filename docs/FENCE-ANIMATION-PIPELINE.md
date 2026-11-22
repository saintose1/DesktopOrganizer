# Collapse/expand: animation pipeline

## Предыдущий этап: плавная поверхность без движения icons

Предыдущий clock — DispatcherTimer с интервалом 1/60 с. Это очередь таймера,
не callback compositor. На каждом изменении высоты общий RenderFence вызывал
Win32DesktopHost.Update → AddDamage → FlushDamage → синхронный RedrawWindow
настоящего SysListView32 (INVALIDATE | ERASE | UPDATENOW). Кроме этого, оба native
DIB заполнялись по всей текущей высоте. WPF header уже кешировался; полный WPF
layout/render не был основной причиной.

Измерения 2026-10-03 на реальном desktop host, без видимого WPF окна и без
симуляции ввода. Baseline: `.tools/animation-cadence/20261003-173007/cadence.json`.
После: `.tools/animation-cadence/20261003-174858/cadence.json`.

| Длительность | До: callback FPS, collapse/expand | После: callback FPS | После: реально новые размеры/с | После: avg/max interval, мс |
|---|---|---|---|---|
| 0,15 с | 42,9 / 35,5 | 230,6 / 242,0 | 217,5 / 209,3 | 4,29/9,54 / 4,13/5,41 |
| 0,25 с | 27,0 / 24,2 | 239,0 / 238,4 | 207,2 / 206,6 | 4,17/5,70 / 4,19/7,24 |
| 0,40 с | 26,9 / 26,9 | 237,6 / 237,6 | 198,0 / 198,0 | 4,19/6,83 / 4,21/7,33 |

До максимальная пауза достигала 47,78 мс; на 0,4 с выполнялись всего 11–12
геометрических обновлений и 11–12 принудительных repaint Explorer. После на
0,4 с: 96 compositor callbacks, 80 новых целочисленных размеров, 160 ULW,
0 Explorer repaint во время кадров, 0 DIB allocations. Повторяющиеся размеры
в начале/конце easing пропускаются, поэтому geometry FPS ниже callback FPS.
Это метрики native submissions, не измерение фактических photons/display scanout.

Отдельный сценарий с 10 настоящими assigned icons проверил 0,15/0,25/0,4 с:
на 0,25 с clock 234–239 FPS, avg 4,18–4,27 мс, max 5,37–6,86 мс;
на 0,4 с clock 235–236 FPS, avg 4,24–4,25 мс, max 7,38–8,00 мс.
На первом коротком expand в тяжёлом диагностическом сценарии max 12,58 мс:
сценарий дополнительно читает геометрию HWND/Explorer. Очередь кадров не растёт.
Artifacts: `.tools/rendering-smoke/20261003-174905`.

## Текущий этап: общая анимация поверхности и настоящих icons

1. Зафиксировать start/target geometry, DPI, desktop area, hover и два существующих HWND.
   Один реальный Shell snapshot даёт stable IDs, start positions и glyph bounds.
   Recovery return positions уже вооружены; JSON на кадрах не читается/не записывается.
2. Подготовить DIB body/frame/header и Shell animation session: cloned PIDLs,
   массивы POINT и преобразование screen ↔ Shell view. Новые bitmap/HWND на кадрах не нужны.
3. Из нормального grid layout вычислить локальную скрытую позицию группы:
   общий сдвиг вверх максимум на высоту glyph, но весь glyph остаётся ниже header.
   Spacing между rows сохраняется. Offscreen parking не интерполируется через экран:
   видимая часть expand начинается локально; строки, которые не помещаются в
   текущий content, остаются в parking. Collapse паркует их по мере сжатия content.
4. DesktopCompositionClock ждёт DWM presentation на выделенном background thread
   через документированный [DwmFlush](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmflush).
   Native HWND обновляются на UI-потоке. Максимум один pending/executing frame,
   включая ожидание Shell commit; дополнительные compositor pulses coalesce.
   Если во время commit пришёл новый pulse, после завершения берётся только
   последний из них, без ожидания ещё одного refresh и без очереди старых кадров.
   Нет отдельного icon timer/polling, fixed FPS sleep loop или зависимости от MainWindow.
5. FenceAnimationFrame содержит raw progress, eased progress и высоту. Одно значение
   eased progress применяется к высоте и позициям значков (expand: cubic ease-out,
   collapse: quadratic ease-out).
   Абсолютные start/target координаты округляются один раз, rounding error не накапливается.
   Expand фиксирует значки этой группы на финальном raw progress; collapse — когда
   вся группа уже попала в parking. Не использовать преждевременно округлённое
   easing как признак конца. Парковочные слоты резервируются и для раскрытых групп,
   поэтому раскрытие и сворачивание не сдвигают значки соседних групп.
6. Glyph bounds из стартового snapshot определяют видимость каждого item:
   полный glyph внутри текущего content → интерполированная позиция;
   иначе → настоящий offscreen parking. Ни SetWindowRgn, ни layered styles Explorer
   на кадрах не меняются. Не создаются fake icons, WPF overlay или копии glyph.
   При раскрытии значок входит со стороны нижней кромки и скользит вверх в свою
   ячейку. Смещение уменьшается по общему progress, расстояния между рядами
   сохраняются. Значок показывается целиком, когда помещается в текущую область.
   Если группа скрыта ещё и атрибутом Hidden, Shell должен вернуть её элементы
   в представление. Ожидание проверяет число элементов ListView и читает полный
   снимок при его изменении. Резервное чтение учитывает замены без изменения числа.
   Возвращённые значки занимают парковочные позиции до старта общей анимации.
   При подготовке перехода и перед сохранением его результата Explorer заново отдаёт
   координаты и границы значков выбранной группы. Пустой Fence пропускает чтение
   позиций. Индексы берутся из полного снимка и сверяются по Shell identity. При
   изменении состава рабочего стола, индексов, DPI или шага сетки используется
   полное чтение. Выборочный снимок не заменяет список значков для наблюдения за desktop.
7. На изменившийся кадр: максимум один
   [SelectAndPositionItems](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifolderview-selectandpositionitems)
   для изменившихся значков на существующем STA worker; затем height-only native presentation,
   один targeted
   RedrawWindow только для объединённых old/new bounds действительно перемещённых glyph.
   Parking клипируется до видимой desktop области и не даёт fullscreen damage.
   Redraw не выключается. SetRedraw(true) вызывается один раз на transition:
   при уходе последнего glyph в parking для collapse, на финальном кадре expand,
   а не после каждой Position: повторное включение приводило к двойному repaint
   и лишнему синхронному ожиданию Explorer. Collapse не откладывает Shell finish
   до последнего кадра header: это убирает отложенное hide в конце сжатия.
   После parking commit оставшиеся кадры меняют только Fence height.
   Число icons не умножает число COM batches.
   Повторные позиции/геометрия пропускаются; следующий кадр не опережает icon commit.
   Границы старых и новых позиций ищутся по Shell identity в текущем снимке.
   Для очередного кадра не создаётся копия списка всех значков рабочего стола.
   Буферы Shell для PIDL и координат выделяются один раз на переход.
   Вызов получает фактическое число изменившихся значков кадра.
8. Height-only path меняет только нижнюю полосу кешированных DIB: максимум 2 ULW
   и один region собственного header/frame на изменившийся размер. Нет полного WPF
   render или явного SetWindowPos. Небольшие HRGN, передаваемые Windows, выделяются;
   это не bitmap allocations. Пустой Fence сохраняет path без Shell repaint.
9. К финальному кадру collapse все icons уже в parking. Нет снятия маски или
   восстановления region чужого HWND, которое могло бы повторно показать старые
   glyph pixels. Совместимость с recovery journal предыдущего варианта сохранена:
   прежний WindowRegion восстанавливается только при validated HWND/PID.
   Новые recovery journals не требуют восстановления region. Verify → persist/rollback сохранён.
   Промежуточные icon positions и Fence height не меняют durable model. Stagger отсутствует.

Z-order прежний: wallpaper → layered body → настоящий Explorer ListView →
layered header/border. При отсутствии DWM/host действует прежний rollback/recovery path.

Предыдущий reversible ListView viewport оказался визуально неустойчивым: пользователь
подтвердил flash после завершения collapse, а перестановка restore/repaint только
усилила его. Программная проверка region/positions проходила, но не подтверждала
корректность DWM backing pixels. Поэтому весь animation region path удалён, а не
замаскирован задержкой. Текущий путь меняет только позиции Explorer-owned items.

## Измерения совместной анимации

Реальный Explorer, отдельный профиль, 2026-10-03:
`.tools/rendering-smoke/20261003-201219/animation.json` и `latency.json`.
Нативный сценарий меняет layout настоящих shortcuts, затем восстанавливает все
54 исходные позиции и исходные Explorer styles/colors/window region.

| Icons | 0,4 с: callback FPS collapse / expand | Avg interval, мс | Max interval, мс | Bulk batches / Shell completions |
|---|---|---|---|---|
| 1 | 238,6 / 233,1 | 4,18 / 4,28 | 7,29 / 7,06 | 11/1 / 11/1 |
| 5 | 231,4 / 233,6 | 4,31 / 4,28 | 7,16 / 6,86 | 11/1 / 11/1 |
| 12 | 238,1 / 234,5 | 4,19 / 4,26 | 6,89 / 6,52 | 12/1 / 13/1 |

Для 12 icons на 0,4 с collapse Shell finish выполнен при raw progress 0,666,
занял 0,229 мс; поверхность ещё имела высоту 131 px (header 54 px).
Последняя треть collapse больше не вызывает positioning/repaint Explorer.
Expand завершает Shell на progress 1,000 (0,410 мс в этом измерении).
Проверка явно требует, чтобы collapse hide commit был раньше финальной высоты.

До удаления redundant SetRedraw(true), 12 icons на 0,4 с: 91,4 / 76,9 callbacks/s,
avg 10,63 / 12,95 мс, max 25,61 / 29,02 мс. Artifacts:
`.tools/rendering-smoke/20261003-185451`. Попытка заменить только UPDATENOW на
отложенную invalidation не устранила повторный Shell repaint; итоговый path
сохраняет один синхронный targeted paint и завершает Shell redraw один раз.
Следующий вариант с region (артефакты `20261003-192930`) давал для 12 icons
158,6/166,5 FPS, 32/35 bulk updates, но визуально не прошёл проверку пользователя.
Сейчас для этого же layout достаточно 12–13 bulk updates: integer positions и
видимость меняются только на этих кадрах, остальные обновляют только Fence height.

На 0,15/0,25 с также проверены обе стороны для 1/5/12 items. Это cadence native
submissions, не display scanout и не доказательство отсутствия flicker.
Сценарий читает native geometry во время animation; другие приложения продолжают работать.
Shell positioning/painting ограничивает частоту; совместная анимация может не сохранять
~240 callbacks/s пустого Fence. При дополнительной нагрузке в стресс-сценарии
возможны интервалы более 50 мс; очередь при этом не растёт.
Единый атомарный DWM commit между процессами не гарантируется Windows Shell API.

## Диагностика и проверка

Debug агрегированно логирует transition: frame/geometry counts, FPS, interval
avg/max, coalesced pulses, estimated missed cadence, ULW/region time, Explorer
redraw count, prepared/per-frame DIB allocations и header rasterizations;
строка `Fence animation startup` разделяет подготовку поверхности, подготовку Shell
и время от запуска DWM clock до первой отправки изменившейся геометрии.
отдельно bulk count, Shell/presentation/paint time, Shell finish duration/progress.
Нет строк для каждого frame.
Missed cadence — оценка по median интервалу; coalesced — точный счётчик pulses
при pending/executing frame. Это разные показатели, их нельзя складывать.

`--animation-cadence` у Benchmarks проверяет пустую группу без захвата экрана/мыши.
`--rendering-smoke-background` проверяет shared-progress slide/reveal с 1/5/12 icons,
максимум один bulk/targeted repaint на frame и один Shell completion за transition,
неизменность original region в течение и после transition,
drag/resize через настоящие API, restart и возврат исходных позиций всех desktop items.
Replay API не является проверкой физических mouse/OLE gestures.
Проверка 20 ноября 2026 года: на 18 переходах с 1, 5 и 12 временными элементами
cadence составил 132,7–214,6 FPS. Это частота callback анимации. При длительности
0,4 с Explorer подтвердил несколько промежуточных позиций значка первого ряда
внутри раскрывающегося Fence. Для 12 значков с атрибутом Hidden возврат в Shell
сдвинул первый кадр примерно на 0,9 с. Визуальное восприятие и отсутствие мерцания
требуют отдельной проверки на экране.
Пользователь подтвердил отсутствие flash после удаления region path.
Дополнительное сокращение задержки через ранний hide commit пока проверено
программно на реальном Shell; субъективная задержка требует повторной проверки.

Ручная проверка:

1. В Settings по очереди выставить 0,15, 0,25 и 0,4 с, выйдя из поля для сохранения.
2. На empty Fence и Fence с 1, 5, 10+ icons нажимать collapse/expand; затем проверить
   hover при скрытом главном окне. Icons должны двигаться с body и появляться
   целыми rows по мере доступности места, без полёта из offscreen или следов.
   Повторить collapse несколько раз: после полного закрытия icons не должны
   повторно промигивать. На 0,4 с движение поверхности непрерывное.
3. После transition проверить header drag, resize, click/double-click icons,
   marquee, icon drag in/out, Win+D, hide/show из tray и restart.
4. Субъективная плавность, flicker/ghosting и физические OLE gestures требуют
   отдельного визуального подтверждения. Программные проверки выше их не подтверждают.
