# Desktop surfaces: архитектура и ручная проверка

## Архитектура

Core содержит контракт `IDesktopIntegration` и чистую геометрию clipping.
Модель Fence и формат fences.json не менялись. HWND не попадают в модель или JSON.
Infrastructure/Interop содержит все Win32 и Shell COM вызовы.
App содержит offscreen WPF renderer, координатор с DispatcherTimer и UI-привязки.
Code-behind MainWindow не содержит логики desktop.

На каждую группу создаётся одно небольшое native layered child window
непосредственно под существующим `SHELLDLL_DefView`. Его desktop parent
должен быть Progman либо существующий WorkerW текущего Explorer.
Не используются SetParent WPF-окна, скрытые сообщения 0x052C,
fullscreen overlay, topmost окна, подмена shell или внедрение DLL.

При создании native child thread DPI context временно совпадает с host;
после вызова прежний context восстанавливается. Manifest объявляет совместимость
с Windows 10 и PerMonitorV2. Surface получает готовый premultiplied bitmap
через UpdateLayeredWindow, поэтому нет WPF HWND в чужом процессе.

При изменении сохранённых групп координатор сравнивает модель, геометрию и DPI.
Неизменённые bitmap не перерисовываются каждый tick. Потерянные HWND обнаруживаются
по class/PID/IsWindow; старые ресурсы освобождаются, новый host ищется раз в 2 секунды.
Ошибки подключения не мешают редактору групп и сохранению JSON.

### Границы надёжности

Native child/layered windows — документированный Win32 механизм.
Однако привязка к окнам Explorer использует его наблюдаемую внутреннюю иерархию:
Microsoft не предоставляет стабильного публичного API для пользовательского child
в SHELLDLL_DefView. Поэтому невозможно обещать совместимость со всеми будущими сборками.
При неподходящей иерархии приложение показывает Disconnected и продолжает повторный поиск.
Оно не изменяет стили или порядок окон Explorer и не создаёт WorkerW принудительно.

Источники:

- [Win32 child и layered windows](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features).
- [UpdateLayeredWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-updatelayeredwindow).
- [SetParent и ограничения DPI](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setparent).
- [Shell.ToggleDesktop](https://learn.microsoft.com/en-us/windows/win32/shell/shell-toggledesktop).

## Запуск

```powershell
cd path\to\DesktopOrganizer
& ./.tools/dotnet/dotnet.exe run --project src/DesktopOrganizer.App
```

Для отдельной папки тестовых данных:

```powershell
& ./.tools/dotnet/dotnet.exe run --project src/DesktopOrganizer.App -- --StorageDirectory "$PWD/.tools/desktop-manual"
```

## Checklist

1. Убедитесь, что статус — `Desktop integration: Connected`.
   Раскройте Desktop diagnostics: Progman, DefView, SysListView32 и Host должны быть ненулевыми.
   WorkerW может быть 0, если desktop view размещён непосредственно под Progman.
2. Создайте Fence. Назовите его `Работа`, задайте X=120, Y=80, Width=450, Height=350,
   нажмите «Применить». Нажмите «Показать рабочий стол» или Win+D.
   На desktop должна быть видна рамка с названием и прозрачным фоном.
3. Верните редактор через taskbar. Измените X/Y, имя и размеры, примените.
   Снова покажите desktop: положение, название и размер должны обновиться.
4. Создайте вторую группу в другом месте. На desktop должны быть две surfaces.
   Удалите вторую — она должна исчезнуть немедленно.
5. Откройте обычное приложение поверх области Fence: оно должно перекрывать Fence.
   В Alt+Tab и taskbar допускается окно редактора, но отдельного Fence быть не должно.
6. Проверьте Win+D дважды: Fence остаётся частью desktop, остальные приложения
   скрываются и возвращаются. Проверьте обычное сворачивание редактора.
7. В новом interactive milestone поверхность принимает мышь для header/resize/collapse.
   Проверка жестов описана в MILESTONE-INTERACTIVE.md. Позиции иконок не изменяются.
8. Закройте DesktopOrganizer: surfaces должны исчезнуть. Запустите снова с той же
   папкой данных: сохранённые Fences и их параметры должны восстановиться.
9. Проверьте координаты вне desktop: они сохраняются, но невидимые surfaces
   не создаются; частично видимые обрезаются. Число Surfaces может быть меньше числа групп.

### Перезапуск Explorer — отдельная ручная проверка

Этот сценарий в автоматизированной проверке не выполнялся.
Если можно безопасно закрыть окна проводника и нет активных файловых операций:

1. Оставьте DesktopOrganizer работающим с сохранённой группой.
2. Откройте Диспетчер задач (Ctrl+Shift+Esc), выберите «Проводник Windows» и «Перезапустить».
3. Приложение не должно завершиться. При длительном отсутствии shell статус будет Disconnected.
4. После восстановления desktop подождите несколько секунд: статус должен стать Connected,
   а surfaces — восстановиться. Новые HWND могут отличаться от прежних.
5. Проверьте лог на повторный discovery, создание surfaces и отсутствие необработанного исключения.

Также отдельно проверить: background slideshow/WorkerW, скрытые desktop icons,
виртуальные рабочие столы, несколько мониторов и разные масштабы DPI.

## Read-only диагностика

Пока приложение работает:

```powershell
& ./.tools/dotnet/dotnet.exe run --project tools/DesktopOrganizer.Diagnostics
```

Вывод содержит HWND, Parent, Root, Caption, PID, Visible, Child, Popup, AppWindow и Bounds.
Для Fence ожидается Child=true, Popup=false, AppWindow=false;
Parent — найденный DefView, Root — desktop Progman/WorkerW.

Снимки **реальных** пикселей поверхностей:

```powershell
& ./.tools/dotnet/dotnet.exe run --project tools/DesktopOrganizer.Diagnostics -- --capture-directory "$PWD/.tools/desktop-captures"
```

Сначала покажите desktop. Диагностика не прячет foreground windows;
если они перекрывают Fence, именно они окажутся в снимке.
Ограничение raster/capture — 16 млн пикселей на surface.
Снимки остаются локальными. Логи — в `Logs` выбранной папки данных.

## Результат проверки 1 октября 2026

- Build: 0 ошибок, 0 предупреждений.
- Tests: 50 пройдено, 0 не пройдено, 0 пропущено.
- Реальный Explorer: PID=13024; Progman=0x1015E; WorkerW=0x0;
  DefView=0x10168; SysListView32=0x1016A; selected Host=0x10168.
- Native surface видима на реальном desktop; подтверждено снимком экранных пикселей.
- Название и bounds обновлены до «Работа на Desktop», X=120, Y=80, Width=450, Height=350.
- Проверены две surfaces, удаление второй, освобождение всех HWND при закрытии.
- После перезапуска JSON восстановлен и создана новая surface с теми же параметрами.
- Проверено поведение после Shell.ToggleDesktop и перекрытие обычным приложением.
- Отдельных Fence среди top-level окон нет; native диагностика подтверждает child/non-popup/non-appwindow стили.
- Не проверены: реальный перезапуск Explorer, WorkerW layout, mixed DPI,
  несколько мониторов, virtual desktops и ручной осмотр Alt+Tab/taskbar.
- Desktop icons не перечисляются и не перемещаются. Surface визуально накладывается
  на icon layer. Прозрачность для мыши относилась к первой версии desktop milestone;
  interactive surfaces теперь принимают ввод и по-прежнему не являются контейнером иконок.
