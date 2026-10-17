# Explorer stabilization — 17 октября 2026

Только исправления wallpaper composition, устаревших assignments и изоляции desktop drag.
Lavender HUD, appearance model, анимации, tray и autostart не перерабатывались.
До изменений создан локальный annotated tag `pre-explorer-stabilization-20261013`.
Существующие stable tags и AGENTS.md сохранены.

## Wallpaper / Wallpaper Engine

На реальном desktop воспроизведены два некорректных состояния:

1. Прозрачность ListView включалась до создания, первой presentation и установки Z-order
   собственных layered child HWND. Первый parent backing мог остаться чёрным даже при
   правильных color-key/background свойствах. Активация перенесена после `surface.Update`
   и `EnsureStacking`. Сам HWND, bitmap и Z-order архитектура не заменяются.
2. Возврат Explorer к `LVS_EX_TRANSPARENTBKGND` / `CLR_NONE` при оставшемся нашем layered
   color-key renderer даёт непрозрачный чёрный backing. A/B на Wallpaper Engine:
   рабочий full-key режим — 0% чёрных пикселей вне Fence; несовместимая комбинация — 100%.
   Добавлена проверка owned properties на безопасном idle observation, в том числе до
   Shell enumeration. В исправном состоянии нет записи свойств и repaint.

Color-key устанавливается перед тёмным background. Ошибка активации возвращает исходные
свойства; чужой alpha/color-key renderer не перезаписывается. Recovery journal сохраняется.
Wallpaper settings, окна Wallpaper Engine и Explorer process не изменяются/не перезапускаются.

## Удаление и rename

Ранее наблюдение обновляло snapshot, но не удаляло исчезнувший assignment и не уплотняло
его Fence. Добавлены документированные Shell notifications для Desktop/PublicDesktop
и create/destroy/name-change notifications существующего observer.

Удаление требует двух полных согласованных наблюдений отсутствия плюс Shell DELETE/RMDIR
либо подтверждённый FileNotFound/DirectoryNotFound. Ошибка доступа/ввода-вывода, virtual ID
и неполный snapshot не подтверждают удаление. Проверка filesystem выполняется вне UI
только для отсутствующих кандидатов. Повторное появление отменяет старое DELETE evidence.

После подтверждения пересчитывается только изменившаяся группа. Shell positioning
проверяется до записи JSON. Rename pair переносит identity, сохраняя ownership, порядок
и offsets; цепочки/циклы и конфликт с другой группой обработаны. Новое rename/delete
уведомление инвалидирует незавершённый proposal. Реальные файлы механизм не удаляет.
Восстановленный после окончательного удаления item остаётся обычным desktop item;
самопроизвольного назначения в Fence нет.

## Обычный desktop drag

**Уточнение после реального воспроизведения пользователем:** прежний API smoke не запускал
внутреннюю OLE-нормализацию Explorer и не подтверждал устранение выпадения. Зафиксировано
перемещение всех 52 parked items при сохранении assignments; последующее исправление
parking lifecycle описано в [Native OLE parking](FIX-NATIVE-OLE-PARKING.md).

Найдены связанные опасные пути: drop-план вычислял позиции всех групп, неполный read мог
заменить пригодный identity cache, а исключение чтения завершало живую Shell session.
Её Dispose восстанавливал snap-to-grid, что могло вызвать общий reflow, включая parking.
Повторная инициализация затем могла запустить общий positioning.

Теперь обычный drop вне Fence остаётся read-only. Для drop in/out план ограничен
изменившимися source/destination группами. Позиционный drift других icons не используется
как команда переназначения. Начальный/конечный Shell/ListView count и coordinate origin
проверяются; неполный read сохраняет пригодный cache, readiness и живую Shell session.
Assignment не снимается только из-за временной позиции или пропуска item в snapshot.

Эти пути подтверждены кодом и API replay. Это ещё не доказательство устранения исходного
сценария с настоящим пользовательским OLE drag — требуется ручное подтверждение ниже.

## Проверки

- Debug и Release: build без warnings/errors, все 350 tests проходят в каждой конфигурации.
- Новые regression tests: property ordering/reset/restore/foreign ownership;
  membership/rename/restore/trust boundaries; outside drop и изоляция affected targets.
- `--explorer-stability-smoke`: собственные временные TXT fixtures, отдельный профиль,
  настоящий Explorer. Create, DELETE средней позиции, compact + persistence, rename pair,
  ordinary move при expanded/collapsed группах прошли. Для обычного move: одна его собственная
  Shell batch, ноль посторонних Fence batches, неизменные assignments и Fence JSON.
- Wallpaper samples до surfaces, после surfaces, нового item, ordinary move и graceful exit:
  0% почти чёрных пикселей. После выхода исходные Explorer properties восстановлены.
- `--icons-smoke`: настоящий Shell/API + adapter, групповой drop, content/header hit routing,
  Fence drag/resize, collapse/expand, parking, persistence, restart сервиса и drop-out прошли.
  Проверено при DPI 150%. Все 65 исходных позиций восстановлены после обеих проверок.
- Утилита существующего smoke теперь загружает текущую WPF theme и отказывается запускать
  изолированную проверку одновременно с production DesktopOrganizer.

Локальные скриншоты, журналы, snapshots и резервные копии профиля находятся в ignored
`.tools/`; они не входят в Git. Исходный пользовательский fences.json не заменялся тестовым.
После проверок обновлённый Debug EXE запущен с обычным профилем и `--StartInTray=true`:
tray зарегистрирован, Connected, созданы все 6 surfaces, сохранены 52 assignments.
Содержимое пользовательских fences.json и settings.json совпало с резервной копией;
существующая регистрация автозапуска присутствует и не менялась.

## Ручной checklist

1. С Wallpaper Engine запустить DesktopOrganizer, создать/скачать новый ярлык на desktop.
   Обои видимы до и после, в том числе при создании, drag и collapse Fence.
2. Иметь развёрнутую и свёрнутую группы с assigned icons. Несколько раз переместить обычный
   не назначенный ярлык снаружи. Остальные группы/parking/assignments остаются прежними.
3. Удалить ненужный тестовый ярлык из середины группы: остальные занимают свободный slot.
   Переименовать другой тестовый item: принадлежность сохраняется. Восстановить удалённый:
   появляется обычный desktop item без автоматического назначения.
4. Проверить настоящий single/multi-select и OLE drag внутрь/наружу, double-click,
   Fence drag/resize, кнопочный и hover collapse/expand, Win+D.
5. Выйти через трей, снова запустить: группы/assignments/collapse восстанавливаются,
   обои остаются видимыми. Uninstall проверять только на ненужном тестовом приложении.

Физические mouse/OLE gestures, uninstall, смена обоев/переподчинение WorkerW Wallpaper Engine
и новый Windows logon этой проверкой не подтверждены. При фатальном COM disconnect без
смены валидных HWND может потребоваться перезапуск DesktopOrganizer; такой сбой отдельно
не воспроизводился. При потере проверенного host приложение сохраняет данные и диагностирует
отключение вместо перезапуска Explorer или сброса расположения.
