# Системный трей и checkpoint перед редизайном

Используется документированный Shell_NotifyIcon с NOTIFYICON_VERSION_4.
Невидимый собственный top-level HWND принимает NIN_SELECT (один клик),
NIN_KEYSELECT и WM_CONTEXTMENU; родителя Explorer у него нет. Native menu
освобождается до обработки команды выхода. TaskbarCreated повторно регистрирует
значок после восстановления Explorer. Все P/Invoke остаются в Infrastructure/Interop.

App использует OnExplicitShutdown. TrayLifetimeViewModel отделяет скрытие окна
от завершения процесса; TrayApplicationService работает с единственным MainWindow
из DI. Hide убирает окно из taskbar/Alt+Tab и не останавливает desktop integration.

Перед выходом завершается активный Fence gesture/collapse и post-drop reconciliation.
Уже завершённые изменения сохранены через существующий ViewModel, дополнительного
перезаписывания fences.json нет. Coordinator останавливает наблюдение и проверенно
возвращает реальные иконки из offscreen parking, не изменяя assignment/collapse.
Затем существующий Dispose восстанавливает rendering properties и Shell grid flags.
Если Explorer отсутствует, сохранённый return journal остаётся для восстановления.
При ошибке позиционирования выход отменяется и наблюдение возобновляется.

## Ручная проверка

1. Запустить приложение; найти DesktopOrganizer под стрелкой скрытых значков.
2. Создать Fence. Нажать крестик: окно исчезает, Fence остаётся на desktop.
3. Переместить/resize/collapse Fence с закрытым главным окном.
4. Один раз нажать ЛКМ по значку: открывается прежнее окно с актуальными данными.
5. Повторить скрытие/открытие; если окно свёрнуто, оно восстанавливается.
6. ПКМ: проверить оба пункта меню и открытие; проверить Win+D и штатные icon gestures.
7. Свернуть Fence с назначенными значками; выбрать «Выход» в трее.
   Процесс и own HWND исчезают, значки возвращаются на desktop, Explorer работает штатно.
8. Запустить снова: положение, размеры, assignments и collapse восстанавливаются.

Unit tests политики lifetime и native callback/API checks не заменяют физический
клик по значку в notification area. Explorer автоматически ради проверки не перезапускается.
Windows управляет тем, находится значок непосредственно рядом с часами или под стрелкой.

## Результаты проверки 3 октября 2026

- Debug / Release: сборка без warnings/errors; 211/211 tests, 0 skipped.
- GUI: крестик скрывает главное окно; оно исчезает из списка видимых окон,
  процесс продолжает работать. После native NIN_SELECT callback тот же HWND
  появляется снова. Восстановление минимизированного окна проверено.
- Shell_NotifyIconGetRect подтверждает регистрацию реального значка. Контекстное
  меню с обоими пунктами осмотрено на экране. Native команда выхода завершает
  процесс и удаляет Fence HWND; лог подтверждает восстановление свойств Explorer.
- Real Shell API smoke: group assignment, движение, resize, collapse/expand,
  graceful возврат parked icons, пустой return journal после verify,
  сохранённый collapse/assignment и восстановление после restart — PASS.
  Все 54 исходные desktop positions восстановлены. Это API/adapter replay,
  не ручной OLE drag.
- Физический single-click именно по значку трея, Alt+Tab, Win+D и восстановление
  после реального Explorer restart в этом прогоне не подтверждены.

Checkpoint: отдельный git commit и аннотированный tag `pre-ui-redesign-stable`.
Он включает текущую стабилизацию Explorer и трей; bin/obj, SDK, логи и пользовательские
JSON в git не входят. Редизайн начинается отдельным этапом.
