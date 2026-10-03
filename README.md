# DesktopOrganizer

Windows-приложение на C# / .NET 10 / WPF / MVVM для организации рабочего стола.
Группы отображаются в desktop subtree Explorer. Реализованы CRUD, drag за заголовок,
resize восьми краёв/углов, collapse и JSON persistence. Добавлена организация
настоящих desktop icons: вход/выход, grid, движение с группой, resize relayout.

## Сборка и запуск

Нужны Windows и .NET 10 SDK. global.json выбирает SDK 10.0.100 или новее в ветке 10.0.
При первом restore требуется NuGet. Из корня проекта:

~~~powershell
dotnet build DesktopOrganizer.sln
dotnet test DesktopOrganizer.sln
dotnet run --project src/DesktopOrganizer.App
~~~

С установленным в проекте SDK:

~~~powershell
& ./.tools/dotnet/dotnet.exe build DesktopOrganizer.sln
& ./.tools/dotnet/dotnet.exe test DesktopOrganizer.sln
& ./.tools/dotnet/dotnet.exe run --project src/DesktopOrganizer.App
~~~

Готовый exe: src/DesktopOrganizer.App/bin/Debug/net10.0-windows/DesktopOrganizer.App.exe.
Перед пересборкой выберите «Выход» в меню трея, чтобы приложение освободило DLL.

## Системный трей

Значок DesktopOrganizer находится в области уведомлений рядом с часами (Windows может
поместить его под стрелку скрытых значков). Крестик и Alt+F4 скрывают главное окно:
Fences, работа с настоящими значками и автоматическое сохранение продолжаются.
Один клик ЛКМ по значку показывает **то же окно** и активирует его; свёрнутое окно
восстанавливается. ПКМ открывает «Открыть DesktopOrganizer» и «Выход».

«Выход» дожидается окончания текущей операции, возвращает временно скрытые значки
на рабочий стол, восстанавливает свойства Explorer, удаляет значок трея и завершает
процесс. Состояние collapse и назначения сохраняются: при следующем запуске
значки свёрнутых Fences снова скрываются. Ошибка восстановления отменяет выход
и показывает сообщение; если Explorer отсутствует, журнал возврата сохраняется.
Закрытие окна не записывает лишний снимок и не перезаписывает повреждённые данные.
Дизайн главного окна не изменён; автоматический запуск Windows не добавлен.

Проверки и ограничения: [milestone трея](docs/MILESTONE-TRAY.md).

## Использование

Создайте группу кнопкой «Новая группа». Нажмите «Показать рабочий стол» и разместите
Fence за свободную часть заголовка. За края и углы меняется размер; кнопка справа
сворачивает/разворачивает группу. Height хранит нормальную высоту.

Перенесите настоящий ярлык/файл рабочего стола в тело Fence. Explorer выполняет
обычный drag; release/WinEvent запускает проверку финальной позиции, assignment,
немедленный layout и verified persistence. Таймер 250 мс остаётся fallback наблюдения.
Иконка остаётся в Explorer: доступны обычный выбор
и запуск. Перенос за пределы группы удаляет назначение, сохраняя новую позицию.
Удаление Fence также оставляет иконки на desktop.

Группа перемещает свои иконки и перераскладывает их при resize. При недостатке
места размер ограничивается/высота растёт, чтобы все иконки помещались; scrolling
отсутствует. В свёрнутом Fence assigned icons временно перемещаются за пределы всех
мониторов, сохраняя assignment и offsets. Expand возвращает их на сохранённые места.
Кнопка в desktop header запускает отдельный immediate path: icons и surface
обновляются до проверки/записи JSON, без ожидания общего polling.

«Упорядочить значки автоматически» в меню рабочего стола → «Вид» должно быть выключено.
Приложение показывает объяснение, если настройка мешает позиционированию.
«Выровнять значки по сетке» временно отключается при первом управляемом размещении
и восстанавливается при штатном закрытии: иначе Explorer округляет координаты.
Статус в UI показывает изменение. При восстановлении сетки Explorer может округлить
последние позиции; следующий запуск снова восстановит сохранённые offsets.

Параметры можно точно задавать в редакторе кнопкой «Применить». Не применённый текст
остаётся черновиком. При desktop gesture основной снимок модели обновляется сразу,
редактор — до 10 раз/с, JSON — один раз после завершения. Escape/потеря capture
или ошибка записи возвращает предыдущую модель, surface и позиции иконок.

## Данные и логирование

Данные: %LOCALAPPDATA%/DesktopOrganizer/fences.json.
Поля: Id, Name, X, Y, Width, Height, IsCollapsed, Icons.
Назначение содержит StableId (Shell desktop absolute parsing name), DisplayName,
OffsetX/OffsetY. Индекс ListView используется только при чтении текущего снимка.
Старые JSON без Icons/IsCollapsed загружаются автоматически.

Запись атомарная: temp-файл в той же папке, flush, replace. Повреждённые данные
переносятся в fences.json.corrupt-*. При невозможности чтения/backup изменения
блокируются. На одну папку данных разрешён один экземпляр приложения.
Не запускайте несколько экземпляров с разными папками для одного рабочего стола:
они будут управлять одним Explorer и настройкой сетки.

parked-icons.json заранее хранит нормальные return positions assigned icons.
desktop-presentation.json сохраняет original ListView properties до включения
прозрачности: они восстанавливаются при штатном закрытии, а после crash — при
следующем запуске с проверкой HWND/PID. Collapsed icons остаются offscreen для restart.
Чтобы вернуть их без Fence, закройте App и выполните:

~~~powershell
& ./.tools/dotnet/dotnet.exe tools/DesktopOrganizer.Diagnostics/bin/Debug/net10.0-windows/DesktopOrganizer.Diagnostics.dll --restore-positions "$env:LOCALAPPDATA/DesktopOrganizer/parked-icons.json"
~~~

ILogger<T> / Serilog: %LOCALAPPDATA%/DesktopOrganizer/Logs/desktop-organizer-YYYYMMDD.log.
Ротация ежедневно и по 10 МБ, до 14 файлов. В Debug в конце жеста пишется агрегат
Mean/Max update time, icon reposition time, explicit repaint count, bitmap presents
и update rate, без сообщения на каждый frame. Collapse/expand отдельно измеряет
latency до native positioning/repaint, исключая persistence и readback.

Изолированная папка для проверки:

~~~powershell
& ./.tools/dotnet/dotnet.exe run --project src/DesktopOrganizer.App -- --StorageDirectory "$PWD/.tools/manual-check"
~~~

Папка изолирует JSON и логи; Explorer и позиции настоящих иконок остаются общими.

## Архитектура

~~~text
DesktopOrganizer.sln
src/
  DesktopOrganizer.Core/
    Models/                   Fence, DesktopIconAssignment
    Desktop/                  contracts, coordinates, interaction geometry, grid/matching
    Storage/                  IFenceStore
  DesktopOrganizer.Infrastructure/
    Interop/                  discovery, native surfaces/input, remote reader, Shell COM, STA worker
    Storage/                  JsonFenceStore
    DependencyInjection.cs
  DesktopOrganizer.App/
    Desktop/                  header renderer, synchronization, post-drop coordinator, performance
    ViewModels/               MainWindowViewModel, ObservableObject
    Commands/                 RelayCommand
    Views/                    MainWindow
tests/DesktopOrganizer.Tests/
tools/
  DesktopOrganizer.Diagnostics/
  DesktopOrganizer.Benchmarks/
docs/
  MILESTONE-ICONS.md
  PERFORMANCE.md
~~~

Core независим от Windows/WPF. Infrastructure зависит от Core; App собирает DI
через Generic Host. Mouse input, rendering, модель и persistence разделены.
Code-behind окна содержит инициализацию представления и DataContext.
Все Win32/Shell COM вызовы изолированы в Infrastructure/Interop.

Host: проверенный SHELLDLL_DefView под Progman/WorkerW с SysListView32/FolderView.
Для Fence используются два native WS_CHILD/WS_EX_LAYERED HWND: равномерный
полупрозрачный body ниже ListView и интерактивный header/border выше него.
Настоящий ListView сохраняет items/input; его фон становится прозрачным через
обратимые документированные свойства (color key, double buffering).
Region рамки исключает content; body не имеет вырезов под иконками.
Body имеет WS_EX_TRANSPARENT и WS_DISABLED. WM_NCHITTEST рамки возвращает
HTCLIENT только для header/actions/resize, а для content — HTTRANSPARENT.
Color-key pixels самого layered ListView пропускают mouse hit к DefView.
Для background gesture используется узкий WH_MOUSE_LL router на отдельном потоке:
пропущенный DOWN передаётся в настоящий ListView до доставки DefView.
WM_MOUSEMOVE никогда не подавляется: иначе Windows не двигает курсор. После capture
Explorer получает настоящие MOVE/UP; до capture быстрый UP дополнительно передаётся
в ListView без подавления оригинала. Это устраняет конкуренцию первого DOWN и
регрессию с застыванием курсора в предыдущем router.
Explorer сам выполняет capture/selection; его opaque icon hits, header/borders
и другие приложения проходят обычным путём. Raw Input остаётся наблюдением drop,
без повторной передачи mouse messages. Fullscreen input overlay, собственная
рамка выделения, injection и subclassing чужого окна не используются.
Перед native marquee guard восстанавливает исходные свойства ListView;
после release возвращает Fence composition. Без Fence свойства не меняются.
Видео пользователя от 2 октября показало, что восстановление свойств само по себе
не доказывает правильные пиксели marquee. В этой конфигурации работают Wallpaper
Engine и Windhawk; окончательный rendering fix ещё требует A/B реальной мышью.
Прозрачный участок внутри настоящей иконки теперь определяется Shell hit-test
и не запускает переключение composition как пустой desktop.
WPF растеризует только заголовок; premultiplied bitmaps передаются UpdateLayeredWindow.
Drag не растеризует body/header и перемещает оба HWND одним DeferWindowPos batch.
Fullscreen overlay, DLL injection,
подмена Explorer и undocumented сообщения Progman не используются.
При потере host выполняются повторный discovery и восстановление surfaces.

Чтение ListView выполняется на отдельном STA worker через документированные
LVM_GETITEMPOSITION/LVM_GETITEMTEXTW/LVM_GETITEMRECT и data-only remote buffer.
Shell PIDL даёт stable identity. Запись позиций — документированный
IFolderView2.SelectAndPositionItems + SetRedraw(true), чтобы Explorer обновлял
реальные позиции и их изображение. Затем обновляется Fence и один HRGN repaint
старой/новой геометрии иконок/поверхности, без полного desktop repaint во время жеста.
Новейшая пачка координат заменяет ожидающую, очередь кадров не накапливается.

Multi-drag сохраняет реальный selected set через IFolderView2.GetSelection / IShellItemArray
уже при press: stable identities и initial positions копируются до движения.
Общий target определяется release point; весь set назначается/снимается одним
proposal, независимо от отдельных final bounds. Пока gesture активен, automatic
layout не выполняется. Восстановление assignment по отдельным final bounds удалено.
Post-drop на STA worker: final snapshot → pure Core layout; если targets пуст,
positioning/redraw не выполняются. Иначе короткий SetRedraw(false) → один bulk →
SetRedraw(true). Readback, recovery journal и durable save выполняются
после возобновления redraw, без дополнительного damage repaint. При конфликтующей
финальной записи Explorer proposal отклоняется; успешный drop не повторяет bulk.
Отчёт с причинами, счётчиками, компромиссами и manual acceptance:
[Explorer interaction stabilization](docs/STABILIZATION-EXPLORER-GESTURES.md).

Обычный клик и marquee по пустому desktop не переключают composition ListView и
не вызывают две полные перерисовки. Для выделения по фону Explorer рисует свою
штатную контурную рамку без alpha fill: layered/color-key backing остаётся тем же
на всём протяжении жеста. Клики по значкам, OLE drag и positioning используют
двойную буферизацию; только классифицированный press по пустому фону временно
выключает её до forwarded DOWN. При закрытии исходные свойства возвращаются.
Реальная проверка четырёх selected items обнаружила пропуски прежнего обхода
GetSelectedItem: он возвращал индексы 1,3 вместо 0,1,2,3. Полный IShellItemArray
устраняет эту потерю; маленький Fence принимает весь set и увеличивает высоту сетки.
Восстановление Z-order меняет только собственные HWND с неправильным порядком,
одним DeferWindowPos batch без background erase; оно реагирует на reorder Explorer,
не дожидаясь polling. Повторный desktop click не активирует уже активный desktop root.

Пользователь подтвердил отсутствие мигания при обычном desktop click. Прежнее
мигание marquee связано с временным original wallpaper backing поверх Fence body.
Исправление включено в основной сборке: LVS_EX_DOUBLEBUFFER отключается только
на период выделения по фону. Постоянное отключение было причиной регрессии drag
и отменено. Компромисс —
контурная рамка вместо современной полупрозрачной заливки. A/B stage 6 соответствует
новому режиму, stage 5 воспроизводит прежний guard. `--outline-composition` проверяет
неизменность backing при begin/end (меняется только double-buffering bit),
0 наших composition redraw, отсутствие новых bitmap presents и восстановление
свойств при shutdown. Пользователь подтвердил отсутствие мигания marquee в
предыдущей сборке; icon drag в новом scoped режиме требует physical acceptance.
Property/API probe не объявляется visual PASS.

## Проверки и ограничения

3 октября 2026 года, SDK 10.0.401:

- После stabilization Explorer gestures: Debug/Release build — 0 ошибок и
  предупреждений; 207 unit tests в каждой конфигурации. Native probe selected set:
  все 4 items, anchor и 4 initial positions; пустой set обрабатывается без ошибки.
  Native probe: 5 настоящих
  items — 1 bulk position / 1 redraw completion; redraw lease около 18,6 ms.
  Rendering API regression с 10 icons
  и пустым Fence проходит; после него восстановлены все 54 исходные позиции.
  Видео разобрано; A/B слоёв подготовлен. Исправление всех трёх дефектов пока
  нельзя считать подтверждённым: marquee/group OLE/blink требуют acceptance по
  [текущему отчёту](docs/STABILIZATION-EXPLORER-GESTURES.md).
- После исправления застывания курсора: Debug/Release build — 0 ошибок,
  0 предупреждений; 174 unit tests проходят.
- Настоящий Explorer: прочитаны 52 иконки, проверены перемещение/восстановление
  трёх ярлыков, назначение, grid, перенос с группой, resize, collapse и вынос наружу.
- Header/resize hit возвращает собственный Fence HWND. Color-key background
  hit возвращает DefView; background router направляет gesture в ListView.
  Native WM_NCHITTEST content возвращает HTTRANSPARENT.
- [Исправление Explorer selection rectangle](docs/FIX-EXPLORER-SELECTION.md):
  причина, HWND hit path, routing и ручной checklist. Headless native probe
  проверяет только создание/освобождение router; физический selection/OLE
  drag проверяется отдельно. Исправленная копия запущена с отдельным StorageDirectory,
  Connected/1 surface; реальная desktop surface просмотрена при 150% DPI.
  Средство Computer Use не адресует desktop child HWND: проверка самого mouse
  gesture выполняется пользователем, а не выдаётся за успешный GUI replay.
- Приложение запущено, закрыто и запущено снова: две группы и точные позиции
  двух иконок восстановились из JSON. Пользовательские позиции после проверки
  возвращены. Реальные экранные снимки просмотрены; текущий DPI — 150%.
- [Актуальный rendering report](docs/MILESTONE-RENDERING.md): пустой Fence и
  10 реальных иконок, 60 drag + 60 resize updates для каждого, native restart,
  collapse/expand, восстановление оригинальных ListView styles/colors.
- С 10 иконками: drag ~8,65 мс, resize ~12,29 мс на update; collapse visual path
  ~8,45 мс. API replay не заменяет проверку мышью/мониторной latency.

Автоматизация мыши не адресует desktop child HWND. Программный replay API не
подтверждает настоящий OLE drag, запуск значка двойным кликом или субъективную
плавность. Эти пункты, Win+D, все handles, реальные 100/125% и mixed DPI, Explorer
restart требуют проверки по [актуальному checklist](docs/MILESTONE-RENDERING.md).
Полный milestone не объявляется вручную подтверждённым до прохождения checklist.

Иерархия Explorer не является стабильным публичным контрактом. При неизвестной
конфигурации показывается Disconnected. Windows x64 требует приложение той же
разрядности для чтения ListView. Shell parsing identity переживает изменение
индекса, но переименование/перемещение файла меняет путь: старое назначение
сохраняется без угадывания по display name; вернувшийся по тому же identity файл
будет сопоставлен снова. Неоднозначные identities пропускаются.

Зависший Explorer может задержать worker, UI остаётся отзывчивым. Remote buffer
не освобождается, пока сообщение использует указатель. При принудительном
завершении процесса настройка сетки может остаться выключенной, а небольшой
незавершённый remote buffer — до завершения Explorer. При штатном закрытии ресурсы
и настройка освобождаются/восстанавливаются.

Исторические отчёты: [группы](docs/MILESTONE-GROUPS.md),
[desktop](docs/MILESTONE-DESKTOP.md), [interaction](docs/MILESTONE-INTERACTIVE.md).
Дальнейшие этапы: [ROADMAP](ROADMAP.md).
