# Интерактивные desktop Fences

## Реализация

Core: неизменяемый Fence с IsCollapsed. Height — нормальная высота до collapse,
не уменьшенная высота заголовка. FenceInteractionGeometry вычисляет hit testing,
drag/resize и ограничение по реальным work areas мониторов (с учётом промежутков).
FenceHeaderLayout задаёт слоты действий; renderer и mouse input используют общий layout.
Контракт interaction передаёт Started, Preview, Completed, Cancelled без HWND в Core.

Infrastructure/Interop: DesktopFenceInput обслуживает жест существующего native HWND.
Поверхность — enabled WS_CHILD | WS_VISIBLE, WS_EX_LAYERED | WS_EX_NOACTIVATE |
WS_EX_TOOLWINDOW под проверенным SHELLDLL_DefView. MA_NOACTIVATE предотвращает
активацию. Нет fullscreen overlay, внедрения, reparent WPF или вмешательства в icons.
При нажатии сохраняются стартовая позиция курсора и исходная модель; координаты
вычисляются как исходная модель + полный delta, а не сумма округлённых mouse moves.
Позиции cursor/host/monitor читаются в одинаковом thread DPI context Explorer;
контекст WPF восстанавливается. Размер hit areas масштабируется по DPI монитора.

Используются SetCapture/ReleaseCapture. Для non-activating child другого процесса
обычного background capture недостаточно при быстром выходе курсора из окна:
таймер 16 ms дополняет WM_MOUSEMOVE, читая курсор и ЛКМ только для уже начатого
на нашем HWND жеста с принадлежащим ему capture. Нет глобальных mouse hooks.
Потеря capture, Escape, уничтожение surface и закрытие приложения отменяют preview.
Managed исключения не проходят через native WindowProc; ошибки логируются.

App: renderer рисует bitmap offscreen, header/button/hover feedback; coordinator
обновляет существующий HWND. Для перемещения достаточно SetWindowPos, без нового
bitmap. Resize/name/collapse/hover/DPI приводят к обновлению bitmap того же HWND.
ViewModel обновляет общий список сразу, блокирует конкурирующие CRUD-команды
на время gesture, сохраняет один snapshot после завершения. Ошибка записи
возвращает исходную модель/поверхность и показывает сообщение. JSON остаётся прежним.
Отмена и жест без изменений не записывают файл. Atomic persistence не изменена.

## Запуск и ручная проверка

В PowerShell из корня проекта:

```powershell
& ./.tools/dotnet/dotnet.exe run --project src/DesktopOrganizer.App
```

Для отдельного состояния:

```powershell
& ./.tools/dotnet/dotnet.exe run --project src/DesktopOrganizer.App -- --StorageDirectory "$PWD/.tools/interactive-manual"
```

1. Статус Connected. Создайте группу и нажмите «Показать рабочий стол».
2. ЛКМ за свободную часть header: двигайте медленно и быстро. В начале не должно быть
   скачка. Проверьте, что курсор не теряет группу при выходе за исходный прямоугольник.
3. Верните редактор: X/Y уже новые, «Применить» не требуется. Снова покажите desktop.
4. Resize последовательно слева/справа/сверху/снизу и за все четыре угла.
   Проверьте соответствующие курсоры, live feedback и сохранение противоположной границы.
5. Уменьшите до минимума: 180 × 90 при 100%, 225 × 112.5 при 125%, 270 × 135 при 150%.
   Растер округляет видимые границы до целых пикселей; нормальные значения модели точные.
6. Сворачивайте кнопкой справа: только header (36 DIP), drag не начинается от кнопки.
   Разворачивайте: прежняя высота восстанавливается. Свернув, переместите и измените ширину.
7. Оставьте collapsed. Закройте редактор и запустите снова с той же папкой:
   X/Y/Width/Height/IsCollapsed должны совпасть. Expand должен вернуть нормальную высоту.
8. Повторите restart с развёрнутым состоянием.
9. Win+D дважды, затем обычное приложение поверх Fence: surface остаётся на desktop,
   обычное приложение её перекрывает. Fence отсутствует в Alt+Tab/taskbar.
10. Во время drag нажмите Escape: исходная позиция возвращается, JSON не меняется.
11. На нескольких мониторах перенесите Fence между ними, в том числе при отрицательном
    origin и разном DPI. Попытайтесь увести полностью за пределы экранов или в промежуток:
    часть заголовка должна оставаться доступной на одном из настоящих экранов.
12. Повторите при реальных 125/150%, а не только по unit tests геометрии.

Проверка Explorer restart вручную — по MILESTONE-DESKTOP.md. Незавершённый gesture
отменяется; после повторного discovery surfaces восстанавливаются из модели.

## Результаты и пределы проверки

1 октября 2026: build успешен (0 ошибок, 0 предупреждений), tests 82/82,
0 пропущено. TRX: tests/DesktopOrganizer.Tests/TestResults/interactive-milestone.trx.
Проверены чистая геометрия всех 8 resize directions, DPI 100/125/150%, доступность header
при monitor gaps/negative origins, live model preview без частых записей, rollback,
collapse и restart из JSON, обратная совместимость и повреждённый JSON.

Приложение реально запущено, создан Fence, выполнены Show Desktop и повторный запуск.
Read-only native диагностика подтверждает visible child surface под host DefView
0x10168 (root Progman 0x1015E), отсутствие Popup/AppWindow и реальное изображение header.
HWND временные и могут поменяться. Текущий экран — 100%.

UI automation computer-use показывает только top-level окна и не адресует native
child surfaces на desktop. Поэтому реальная проверка мышью drag/8 resize/collapse,
restart после жестов, Win+D клавиатурой и mixed DPI пока требует пользователя.
Unit tests и status Connected не являются доказательством этих ручных сценариев.
Полное acceptance milestone до этой проверки не подтверждено.

Тело Fence также принимает мышь; иконки под ним не организуются/не перемещаются,
доступ к ним внутри поверхности пока отсутствует. В collapsed режиме resize только
по горизонтали; для изменения высоты сначала expand. Legacy/manual размеры ниже
минимума остаются допустимы для совместимости; минимум применяется к мышиному resize.
Привязка к внутренней иерархии Explorer сохраняет ограничения предыдущего milestone.

Win32 reference: [SetCapture](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setcapture),
[child/layered windows](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features).

## Созданные и изменённые файлы этого milestone

Созданы:

- src/DesktopOrganizer.Core/Desktop/FenceInteractionGeometry.cs
- src/DesktopOrganizer.Core/Desktop/FenceHeaderLayout.cs
- src/DesktopOrganizer.Infrastructure/Interop/DesktopFenceInput.cs
- tests/DesktopOrganizer.Tests/FenceInteractionGeometryTests.cs
- docs/MILESTONE-INTERACTIVE.md

Изменены:

- src/DesktopOrganizer.Core/Models/Fence.cs
- src/DesktopOrganizer.Core/Desktop/DesktopContracts.cs
- src/DesktopOrganizer.Infrastructure/Storage/JsonFenceStore.cs
- src/DesktopOrganizer.Infrastructure/Interop/NativeMethods.cs
- src/DesktopOrganizer.Infrastructure/Interop/Win32DesktopHost.cs
- src/DesktopOrganizer.App/Desktop/DesktopIntegrationService.cs
- src/DesktopOrganizer.App/Desktop/FenceBitmapRenderer.cs
- src/DesktopOrganizer.App/ViewModels/MainWindowViewModel.cs
- src/DesktopOrganizer.App/Views/MainWindow.xaml
- tests/DesktopOrganizer.Tests/MainWindowViewModelTests.cs
- tests/DesktopOrganizer.Tests/JsonFenceStoreTests.cs
- README.md
- AGENTS.md
- ROADMAP.md
- docs/MILESTONE-DESKTOP.md
