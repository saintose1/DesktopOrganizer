# Milestone: плавность и настоящие desktop icons

Реализация готова к ручному acceptance. Проверены unit tests и настоящие Win32/Shell
API, приложение запущено и перезапущено. Настоящие mouse/OLE gestures нельзя считать
подтверждёнными: доступный инструмент UI автоматизации адресует только top-level
окна; Progman/DefView/desktop child HWND не возвращаются в его списке.

## Причина лагов и изменения

Ранее preview запускал ObservableCollection/selection/editor bindings и обратную
синхронизацию surfaces. Resize растеризовал весь WPF bitmap, выделяя примерно
1 МБ на update; пиковое CPU время достигало 20,23 мс. JSON записывался после gesture
и не был причиной лагов. Подробные измерения: PERFORMANCE.md.

Native WM_MOUSEMOVE обрабатывается сразу; 16-мс timer служит резервом capture/release
за пределами child HWND. Искусственной задержки перемещения нет. На gesture
кешируются DPI, work areas и icon metrics; desktop discovery выключен до окончания.
Модель заменяется одним immutable record, редактор обновляется до 10 раз/с.
HWND и DrawingVisual переиспользуются. GDI рисует фон и рамку, WPF — только header;
byte buffer кешируется. JSON сохраняется после gesture.

## Explorer integration

Host остаётся проверенным SHELLDLL_DefView. Для Fence используются два WS_CHILD HWND:
layered фон с WS_EX_TRANSPARENT и рамка с SetWindowRgn, исключающим содержимое.
Header/границы принимают мышь; тело пропускает её настоящему SysListView32.
Sibling order восстанавливается после Shell batches, включая изменение сетки.
Эти HWND принадлежат desktop subtree; обычные приложения находятся выше.

DesktopIconService читает index/text/position/bounds через документированные ListView
messages. POINT/LVITEM/text лежат в VirtualAllocEx PAGE_READWRITE; используются
WriteProcessMemory/ReadProcessMemory и минимальные process rights. Process handle
закрывается SafeProcessHandle, память освобождается после завершения синхронных
messages. Чтение выполняет STA worker; ошибка одной иконки не ломает список.
Приложение и Explorer должны иметь одинаковую разрядность.

Shell IFolderView2 даёт PIDL, display name и canonical desktop absolute parsing identity.
Позиция/имя сопоставляются с ListView; изменившиеся/неоднозначные элементы пропускаются
и читаются снова. JSON хранит parsing identity и offsets, индекс не является ключом.
Переименование/перенос файла меняет identity; угадывание по display name исключено.
Недоступные назначения сохраняются, не перемещая другую иконку с похожим названием.

Запись: IFolderView2.SelectAndPositionItems с POSITIONITEM и NOSTATECHANGE,
с сохранением selection/focus. Прямой LVM_SETITEMPOSITION не используется:
современный Explorer не всегда сохраняет такие координаты между запусками.
Подход основан на [примере Microsoft Shell](https://devblogs.microsoft.com/oldnewthing/20130318-00/?p=4933)
и [разъяснении Microsoft о сохранении позиций](https://devblogs.microsoft.com/oldnewthing/20211122-00/?p=105948).

Coordinator опрашивает позиции раз в 250 мс вне Fence gesture/нажатой ЛКМ.
Две одинаковые последовательные позиции считаются завершённым перемещением.
Центр bounds внутри expanded body назначает иконку Fence; вне Fence удаляет
assignment, между Fences переносит. При пересечении выигрывает последний Fence.
WPF AllowDrop/ItemsControl с копиями иконок не используется; OLE drag остаётся Explorer.
Grid использует реальные шаг/размеры значков и padding. При недостатке места
Fence растёт или resize ограничивается, scrolling отсутствует.

Во время Fence drag/resize Shell batches не накапливаются: ожидающая пачка заменяется
последней. Собственные координаты обновляют baseline и не выглядят как пользовательский
drop. На завершении сохраняется итог, отмена/ошибка записи возвращает модель и иконки.

## Настройки и ограничения

- «Упорядочить значки автоматически» должно быть выключено; UI показывает причину,
  если настройка препятствует размещению.
- Explorer snap-to-grid отключается при первом управляемом размещении и возвращается
  при штатном Dispose session. UI показывает изменение. Возврат сетки может округлить
  позиции после закрытия; при запуске offsets восстанавливаются. Принудительный выход
  не гарантирует возврат настройки: её можно включить в desktop → «Вид».
- Collapse сохраняет assignments, но настоящие иконки остаются видны на desktop.
- Polling даёт небольшую задержку после drop. Краткое перемещение и возврат между
  опросами может не создать assignment.
- Несколько DesktopOrganizer с разными StorageDirectory одновременно не поддерживаются.
- При зависшем Explorer worker может ждать message/COM, UI не блокируется. Remote
  memory не освобождается раньше окончания вызова; при принудительном выходе небольшой
  незавершённый buffer может остаться до завершения Explorer.
- Mixed DPI/multi-monitor, WorkerW, Explorer restart и настоящий Win+D не проверены.

## Проверенные результаты

1 октября 2026 года, .NET SDK 10.0.401:

- Debug/Release build: 0 warnings/errors; 112 unit tests проходят в обеих конфигурациях.
- Прочитаны 52 настоящие иконки, Auto arrange выключен.
- Настоящие Shell move/restore, три ярлыка: assignment, grid, совместное движение,
  resize в две строки, collapse/expand, восстановление новых компонентов из JSON,
  вынос значка. Все исходные позиции возвращены в finally.
- WindowFromPoint: body → SysListView32 0x1016A; header → собственный HWND, включая
  холодное восстановление с исходно включённой сеткой. Свернувшийся header целиком интерактивен.
- Реальные экранные PNG просмотрены. Текущий DPI scale: 1,50.
- Настоящий App process запущен, закрыт и запущен повторно: две группы и точные
  позиции двух назначенных ярлыков восстановились. Исходные позиции возвращены.
- Host SHELLDLL_DefView 0x10168, Progman 0x1015E; WorkerW отсутствует.
  HWND — результаты сессии, в коде не зафиксированы.

Артефакты: .tools/icons-smoke/20261001-204546, .tools/icons-ui-smoke.
Программный replay API не является проверкой физического mouse/OLE drag.

В запущенном настоящем App в 20:35 зарегистрированы реальные mouse gestures с
двумя назначенными иконками: 289 updates, Mean 0,710 мс, Max 1,418 мс;
82 updates, Mean 0,858 мс, Max 1,726 мс. Это подтверждает живую обработку ввода,
но не является оценкой субъективной плавности или проверкой всех OLE сценариев.
Во время просмотра GUI найден и исправлен transient selection reset WPF ListBox,
который очищал поле имени после immutable item replacement. Добавлен регрессионный тест.

## Точная ручная проверка

Из корня проекта, закройте предыдущие DesktopOrganizer:

~~~powershell
& ./.tools/dotnet/dotnet.exe build DesktopOrganizer.sln
& ./.tools/dotnet/dotnet.exe test DesktopOrganizer.sln
& ./.tools/dotnet/dotnet.exe run --project src/DesktopOrganizer.App
~~~

1. Desktop → ПКМ → «Вид»: выключите «Упорядочить значки автоматически», если включено.
2. Создайте две группы, нажмите «Показать рабочий стол». Быстро двигайте за свободный
   заголовок: без скачка при начале и без необходимости замедлять мышь.
3. Быстро resize за левый/правый/верхний/нижний край и четыре угла. Проверьте курсоры.
4. Возьмите настоящий desktop shortcut/file, отпустите внутри тела первой группы
   под заголовком и вдали от границ. Запрещённого курсора от тела Fence быть не должно.
   Через 0,25–0,75 с значок встанет в grid, счётчик «Иконок» увеличится.
5. Добавьте ещё 2–4 значка. Кликните и запустите безопасный знакомый ярлык двойным
   кликом: открытие должно выполнить Explorer как обычно.
6. Быстро двигайте Fence: значки следуют за ним, относительные позиции сохраняются.
7. Сузьте Fence до нескольких строк grid. Все значки остаются внутри, назначения
   сохраняются; недостаточный размер ограничивается или растёт высота.
8. Вытащите один значок на свободный desktop: счётчик уменьшается, значок остаётся
   там, куда отпущен. Перенесите другой значок между двумя Fences.
9. Collapse оставляет header, expand возвращает нормальную высоту. Настоящие иконки
   при collapse остаются видны на desktop; assignments сохраняются.
10. Закройте приложение и запустите снова. Проверьте bounds, collapse, назначения,
    восстановленные позиции. В fences.json должны присутствовать Icons/offsets.
11. Win+D туда/обратно, обычное окно поверх Fence: desktop layer сохраняется,
    группы не появляются в Alt+Tab/taskbar и не перекрывают normal applications.
12. При возможности повторите при 100/125/150% и на нескольких мониторах.
    Explorer restart проверяйте отдельно вручную, сохранив работу других программ.

Acceptance завершён, когда реальные мышиные сценарии пройдены.
В Debug после gesture смотрите агрегат Interaction performance в Logs;
JSON меняется после отпускания, а не во время движения.

## Созданы файлы

- src/DesktopOrganizer.Core/Models/DesktopIconAssignment.cs; Desktop/DesktopIcons.cs, FenceIconLayout.cs.
- src/DesktopOrganizer.Infrastructure/Interop/DesktopIconService.cs, RemoteListViewBuffer.cs,
  ShellFolderView.cs, NativeStaWorker.cs, DesktopDpiScope.cs, NativeFenceSurface.cs.
- src/DesktopOrganizer.App/Desktop/DesktopIconCoordinator.cs, InteractionPerformance.cs.
- tests/DesktopOrganizer.Tests/DesktopIconLayoutTests.cs, IconPersistenceTests.cs.
- tools/DesktopOrganizer.Benchmarks/DesktopOrganizer.Benchmarks.csproj, Program.cs, DesktopSmoke.cs.
- docs/MILESTONE-ICONS.md, PERFORMANCE.md.

## Изменены файлы

- src/DesktopOrganizer.Core/Models/Fence.cs; Desktop/DesktopContracts.cs, FenceInteractionGeometry.cs.
- src/DesktopOrganizer.Infrastructure/DependencyInjection.cs; Storage/JsonFenceStore.cs;
  Interop/NativeMethods.cs, Win32DesktopHost.cs, DesktopFenceInput.cs, DesktopDiagnostics.cs, DesktopShell.cs.
- src/DesktopOrganizer.App/Desktop/FenceBitmapRenderer.cs, DesktopIntegrationService.cs;
  ViewModels/MainWindowViewModel.cs; Views/MainWindow.xaml.
- tests/DesktopOrganizer.Tests/MainWindowViewModelTests.cs.
- tools/DesktopOrganizer.Diagnostics/Program.cs; DesktopOrganizer.sln; README.md, AGENTS.md, ROADMAP.md.

Benchmark включён в solution. Runtime appsettings.json был временным override в bin
и удалён; обычный запуск снова использует LOCALAPPDATA.
