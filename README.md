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
Перед пересборкой закройте приложение, чтобы оно не удерживало DLL.

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
Существующий Raw Input receiver передаёт пропущенный background press обратно
в настоящий ListView; Explorer сам выполняет capture/selection. Глобальный mouse
hook, fullscreen input overlay и собственная рамка выделения не используются.
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

## Проверки и ограничения

2 октября 2026 года, SDK 10.0.401:

- Debug/Release build: 0 ошибок, 0 предупреждений; 158 unit tests проходят.
- Настоящий Explorer: прочитаны 52 иконки, проверены перемещение/восстановление
  трёх ярлыков, назначение, grid, перенос с группой, resize, collapse и вынос наружу.
- Header/resize hit возвращает собственный Fence HWND. Color-key background
  hit возвращает DefView; raw background handoff направляет press в ListView.
  Native WM_NCHITTEST content возвращает HTTRANSPARENT.
- [Исправление Explorer selection rectangle](docs/FIX-EXPLORER-SELECTION.md):
  причина, HWND hit path, routing и ручной checklist. Физический selection/OLE
  drag после этого исправления пока не подтверждён.
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
