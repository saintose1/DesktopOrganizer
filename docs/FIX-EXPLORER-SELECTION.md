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

Реальная native проверка --input-hit-smoke на текущем Explorer:

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
3. Существующий Raw Input receiver получает реальное физическое background
   нажатие и его queued screen position. ExplorerBackgroundMouse проверяет
   HWND/PID/parent, client bounds, native capture и WindowFromPoint.
4. Только пропущенный press с target=проверенный DefView пересылается через
   PostMessage(WM_LBUTTONDOWN) в настоящий SysListView32, с client coordinates
   и текущими Shift/Ctrl flags. Нажатия на icon pixels, Fence grip, другом
   приложении или при существующем capture не дублируются.
5. До обнаружения capture также передаётся короткий реальный move/up tail:
   это важно для быстрого click/release, когда Explorer ещё не обработал DOWN.
   Как только native capture начался, handoff заканчивается, все дальнейшие
   события Explorer получает штатно. Мы не вызываем SetCapture для selection.
6. Raw registration не использует RIDEV_NOLEGACY: обычные события Explorer
   сохраняются. Mouse hook, fullscreen input window, injection/subclassing,
   собственная selection rectangle и fake icons отсутствуют.

SendMessage для DOWN не используется: ListView может войти в modal loop и
заблокировать приложение. Распаковка/упаковка координат знаковая, с проверкой
16-bit mouse-message диапазона; координаты вне него не оборачиваются в другую точку.
При disconnect receiver/route удаляются вместе с остальными notifications.

Interactive: header, collapse button, resize edges/corners.
Mouse-transparent: body и content; вне Fence нет наших input HWND.
Renderer/Z-order и persistence не менялись.

Документированные основы:
[layered window hit testing](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features),
[WM_NCHITTEST / HTTRANSPARENT](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-nchittest),
[mouse capture](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-mouse-input).

## Проверки и ограничения

- Debug build: 0 warnings/errors; 158/158 tests.
- Release build: 0 warnings/errors; 158/158 tests.
- 12 новых tests проверяют background handoff, native capture ownership,
  быстрый release, отмену/новый press и signed coordinates. Это tests политики
  routing, а не имитация Explorer или подтверждение физического OLE.
- --input-hit-smoke проверил реальные HWND, native header/edge/content hits
  и восстановление hit target после освобождения presentation lease.
- App запущен в отдельном .tools/input-ui storage с пустым Fence, Connected
  без ошибок. Автоматизация GUI остановлена пользователем через Escape.
  Физические selection rectangle, multiple-icon drag и остальные mouse gestures
  после исправления пока не подтверждены. Не объявлять manual acceptance пройденным.

Запущенная тестовая копия использует .tools/input-ui до её закрытия. Временный
bin/appsettings.json удалён; следующий обычный запуск использует прежнее
%LOCALAPPDATA%\DesktopOrganizer. Исходные пользовательские JSON не изменялись,
тестовый пустой Fence автоматически не перемещал ни одной icon.

Raw Input handoff активен только в текущей desktop session при работающем
receiver. Поведение конкретного Explorer modal selection loop требует ручного
подтверждения; неизвестные HWND/capture и невозможные coordinates не пересылаются.

## Изменённые файлы

- Infrastructure/Interop/ExplorerBackgroundMouse.cs — новый routing/handoff.
- Infrastructure/Interop/DesktopIconEvents.cs — существующий raw receiver.
- Infrastructure/Interop/DesktopFenceInput.cs — точный WM_NCHITTEST.
- Infrastructure/Interop/Win32DesktopHost.cs — transparent body и lParam.
- Infrastructure/Interop/NativeMethods.cs — PostMessage/ScreenToClient/GetMessagePos.
- Infrastructure/Interop/DesktopDiagnostics.cs — readonly input inspection.
- Infrastructure/DesktopOrganizer.Infrastructure.csproj — test-only internals access.
- Tests/BackgroundMouseRouteTests.cs — новые meaningful routing/coordinate cases.
- Tools/Benchmarks/InputHitSmoke.cs, Program.cs — native hit-path verification.
- README.md, ROADMAP.md, этот отчёт.

## Manual checklist

Закрыть тестовую копию обычным способом. Из корня проекта:

~~~powershell
& ./.tools/dotnet/dotnet.exe run --project src/DesktopOrganizer.App --no-build
~~~

1. На desktop вне Fence зажать ЛКМ и протянуть стандартную рамку Explorer.
2. Выделить 3–5 icons, затем перенести выделенную группу обычным Explorer drag.
3. Повторить около Fence и начиная с пустого content pixel внутри него.
4. Проверить header drag, все resize edges/corners и collapse button.
5. Double click icon, group/icon drag in/out, collapse/expand и restart.
   Сохранённые assignments/positions и обычные Explorer действия должны остаться.

Native hit probe запускать только при закрытом App:

~~~powershell
& ./.tools/dotnet/dotnet.exe tools/DesktopOrganizer.Benchmarks/bin/Debug/net10.0-windows/DesktopOrganizer.Benchmarks.dll --input-hit-smoke
~~~

Она включает настоящую presentation lease и затем восстанавливает её.
Не считать PASS этого probe проверкой физической рамки выделения.
