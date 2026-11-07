# Инструкции для работы с DesktopOrganizer

## Границы текущего этапа

- Текущий milestone: синхронная анимация существующего Fence и настоящих Explorer icons.
  Разрешены групповые Shell positioning только на время transition,
  без общего редизайна, рефакторинга или замены штатного Explorer input.
- Не создавать WPF-копии иконок; сохранять штатные клики и OLE drag Explorer.
- Авто-сортировка только для новых файлов по маскам Fence; привязка только при drag заголовка.
  Не добавлять scrolling, контекстные меню Fence и другие новые desktop behaviors без запроса владельца.
  Контекстное меню трея содержит только открытие приложения и выход.
- Не создавать полноэкранный overlay, не подменять Explorer, не внедрять DLL.
- Не посылать undocumented сообщения Progman для создания WorkerW.
- Не добавлять фиктивные реализации будущих доменных сервисов.

## Архитектура

- Core не зависит от App, Infrastructure, WPF или Windows API.
- Infrastructure зависит от Core; реализации внешних сервисов размещать здесь.
- App зависит от Core и Infrastructure; состав DI-контейнера находится в App.
- Соблюдать MVVM: состояние и UI-поведение размещать во ViewModel,
  в code-behind оставлять только обязанности представления.
- Использовать конструкторную DI; не создавать глобальный service locator.
- Логировать через `ILogger<T>`, не добавлять секреты и персональные данные в логи.
- Освобождать ресурсы и завершать сервисы при закрытии приложения.
- Модель Fence должна оставаться неизменяемой и валидироваться при создании.
- Сначала сохранять новый снимок групп, затем обновлять состояние ViewModel.
  При ошибке записи сохранять прежние данные и показывать сообщение в UI.
- Исключение для desktop interaction: показывать live preview в модели без записи,
  сохранять один снимок после отпускания мыши; при ошибке записи или отмене
  возвращать модель и поверхность к состоянию до начала жеста.
- Height всегда означает нормальную развёрнутую высоту, IsCollapsed задаёт отображение.
- Промежуточная высота анимации принадлежит только presentation, не модели и JSON.
  Иконки используют тот же progress/easing, что и Fence, без отдельного timer/polling.
  Offscreen parking не включать в видимую траекторию. Показывать glyph только когда
  он полностью помещается в текущем content; иначе оставлять/возвращать в parking.
  Максимум один bulk Shell positioning и один targeted redraw на изменившийся кадр.
  Не переключать SetRedraw(false/true) на кадрах. При постоянно включённом redraw
  завершать icon transaction одним SetRedraw(true), включая failure/cleanup:
  collapse — при уходе последнего glyph в parking, expand — на финальном кадре.
  Не пересоздавать HWND, Shell identities или bitmaps на каждом кадре.
- Кадры collapse/expand обслуживать по composition cadence, без фиксированного sleep/timer loop.
  DWM wait выполняется вне UI, native HWND — на UI; в очереди не более одного animation frame.
  Кешировать DIB, DPI, геометрию и cloned Shell PIDLs на весь переход.
  Пустой Fence не должен вызывать Explorer repaint на кадрах.
  Не менять window region чужого ListView на кадрах или в конце transition.
  Поддерживать восстановление region только из recovery journal предыдущей реализации.
- Desktop collapse/expand: positioning → native presentation/repaint → verify → persist.
  При ошибке вернуть прежнее представление. Return journal вооружать заранее
  при startup/drop/end gesture, не на visual critical path.
- Разделять чистую геометрию Core, native mouse input Infrastructure и renderer App.
- Привязки иконок хранить по Shell parsing identity; индекс ListView не является постоянным ID.
- Использовать документированный Shell IFolderView2 для записи позиций и SetRedraw(true)
  для завершения Shell redraw. ListView messages — чтение и свойства rendering,
  не альтернативная запись координат.
- Remote memory должна быть только для данных, с минимальными правами и освобождением
  в finally/Dispose после завершения синхронных сообщений. Не освобождать указатель по timeout,
  пока Explorer ещё может его использовать. Не выделять executable memory.
- Тело Fence пропускает мышь к Explorer; заголовок и рамка принимают ввод.
- На каждый mouse move не запускать discovery, JSON, полную растеризацию Fence и editor feedback.
- Наблюдение post-drop и Shell positioning выполняются вне UI-потока; новые move batches вытесняют старые.
- Сетка Explorer временно отключается только при реальном позиционировании,
  исходное состояние восстанавливается при штатном завершении Shell session.
- Не перезаписывать повреждённые или недоступные для чтения данные без сохранения исходного файла.
- Все Win32 и Shell COM вызовы размещать только в Infrastructure/Interop.
- Native child windows создавать сразу с проверенным parent, без SetParent WPF-окон.
- Проверять HWND, class, PID и parent chain перед использованием; не уничтожать чужие окна.
- Native HWND и DispatcherTimer обслуживать на UI-потоке и освобождать при закрытии.
- Rendering снизу вверх: wallpaper → native layered body → настоящий прозрачный
  Explorer ListView → layered header/border. Не вырезать body вокруг иконок.
- Не рисовать рамку BitBlt в родительский GDI backing store. Drag перемещает
  два стабильных собственных HWND одним DeferWindowPos batch.
- На visual update объединять старую/новую геометрию и icon damage в один HRGN,
  клипируя отдельно. Offscreen parking не должен давать fullscreen damage.
- Свойства Explorer менять обратимо, сохранять original properties до изменения
  в desktop-presentation.json. Восстанавливать после shutdown/next startup после crash
  только при совпадении проверенных HWND/PID. Не заменять чужой layered renderer.
- Не выдавать тесты геометрии или контрактов за проверку реального Explorer.
- Программный replay настоящих API не выдавать за проверку реальных mouse/OLE gestures.

## Проверки

- После изменения кода выполнять `dotnet build DesktopOrganizer.sln`.
- Для локального SDK доступна команда
  `& ./.tools/dotnet/dotnet.exe build DesktopOrganizer.sln`.
- Исправлять ошибки и предупреждения; не подавлять их без обоснования.
- Запускать `dotnet test DesktopOrganizer.sln` (или локальный SDK).
- Для GUI-проверок использовать отдельную папку через `--StorageDirectory`.
- Проверять desktop-видимость на реальном экране, а не только статус Connected.
- Не перезапускать Explorer автоматически ради тестов; фиксировать непроверенные сценарии.
- При добавлении значимого поведения добавлять соответствующие тесты.
- Обновлять README и ROADMAP при изменении структуры или функциональности.
- Не коммитить bin, obj, локальные SDK, пакеты, логи и секреты.
