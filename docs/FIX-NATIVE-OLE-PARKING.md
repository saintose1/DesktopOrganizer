# Native OLE parking — 17 октября 2026

## Подтверждённая причина

Состояние снято сразу после пользовательского drag, до открытия/закрытия Fence:
изменились реальные координаты 53 items — переносимого ярлыка и всех 52 assigned items,
до этого находившихся в offscreen parking. Все assignments и collapsed state сохранились.
Explorer самостоятельно вернул скрытые items на видимую сетку. Это не следы отрисовки.

Безопасный A/B использовал два собственных временных TXT-файла. При настоящем пользовательском
переносе Explorer вернул в видимую область и отрицательную, и положительную offscreen position.
Файлы удалены в finally. Пользовательские файлы не удалялись и позиции не сбрасывались.
Смена стороны парковки не решает проблему. Старый API replay перемещал только один item
и не вызывал это внутреннее поведение настоящего OLE drop.

До исправления сохранён локальный commit `f6aa94e` и annotated tag
`pre-real-ole-parking-fix-20261017`; прежние tags и AGENTS.md не изменялись.

## Исправление

- При press сохраняется последний полный snapshot позиций до пользовательского жеста.
- После реального drop проверяется полный финальный Shell snapshot в существующей transaction.
- Кандидат на возврат: item принадлежит по-прежнему свёрнутой группе, раньше его glyph
  полностью находился вне мониторов, теперь он видим, его stable ID не входит в dragged set.
- Возвращается точная прежняя parking position. Grid и offsets не пересчитываются.
- Отсутствующий/дублирующийся ID, неполный snapshot, отключённый positioning,
  item развёрнутой группы или действительно перетаскиваемой selection не затрагиваются.
- Parking targets объединены с обычным drop layout: максимум один Shell bulk и одно
  завершение redraw при непустом плане. Если Explorer ничего не вытолкнул — ноль записей.
- Обычный outside drop не меняет Fence assignments и не сохраняет fences.json.
  Общего QueueAll, отдельного polling-repair или нового mouse hook нет.

Это обработка подтверждённого изменения состояния parking внутри жизненного цикла drop;
внутренняя нормализация Explorer не отключается и его input/OLE не заменяются.

## Проверки

- Debug/Release build: 0 errors, 0 warnings; 358/358 tests в каждой конфигурации.
- 8 regression cases: выброс 52 items, exact previous coordinates, dragged set exclusion,
  expanded groups, здоровый no-op, неполные snapshots, negative-coordinate monitor и duplicate IDs.
- Actual Shell/API smoke: имитация именно наблюдавшегося изменения parking, адресный возврат
  одним batch, сохранность ordinary drop point, expanded group, assignments и JSON.
  Это adapter replay наблюдавшегося состояния, а не самостоятельный реальный OLE gesture.
- Повторно прошли удаление/уплотнение/rename, group drop in/out, Fence drag/resize,
  collapse/expand, parking/restart и graceful exit. Все 65 исходных позиций восстановлены.
- Wallpaper sample остался видимым на всех стадиях; GUI-профиль проверок изолирован.

## Физическая проверка новой сборки

Пользователь подтвердил: значки больше не остаются на desktop, вручную переключать Fence
не требуется. При этом наблюдается короткая вспышка между native drop и parking commit;
её устранение — следующий локальный rendering polish. Отсутствие видимой вспышки нельзя
вывести из unit tests или readback координат.

## Уменьшение post-drop задержки

Локальный rollback перед polish: `pre-drop-flash-polish-20261017` (`158cc2b`).
В пользовательских traces между release и layout proposal было 48–128 ms, затем
возврат 52 items занимал примерно 6 ms. Полное чтение inventory и очередь фоновой
observation оставляли вытолкнутые Explorer значки видимыми до нашего commit.

- При press отменяется только текущий idle inventory read, не selection или drop.
- Release и повтор после устаревшего read обслуживаются с Input priority.
- Drop заново читает настоящие позиции retained Shell PIDLs, origin, spacing и DPI,
  пропуская повторное чтение имён/текста/границ всех items. Количество Shell/ListView
  items проверяется до/после. При смене inventory, DPI/origin или ошибке PIDL — full read.
- Полный проверочный readback перед durable commit сохранён. Неполный старый read
  не может завершить новый gesture. Пустой план не вызывает positioning/redraw.
- Быстрый snapshot 68 items в actual Shell self-check: около 6–7 ms; fallback при
  создании item около 15 ms. Это не измерение всей физической release-to-present latency.
- Нового redraw lock, mouse hook или overlay нет. Bulk/redraw количество не выросло.

Debug/Release: 0 warnings/errors, 359/359 tests. Новый scheduling regression проверяет,
что отмена idle read не поглощает press/drag и не позиционирует icons. Native self-check
прошёл fast snapshot и full fallback при добавлении/удалении собственного fixture.
Отдельный `--icons-smoke` прошёл drop in/out, drag/resize, collapse/expand/restart и
вернул все 65 исходных позиций. Это Shell/API replay, не физическая проверка flash.

Повторный `--explorer-stability-smoke` остановился на чёрном wallpaper sample после
создания surfaces, до parking-replay стадии; cleanup восстановил все позиции.
Ожидание compositor frame и пробный дополнительный parent redraw это не устранили;
неподтверждённый parent redraw в production не оставлен. A/B composition подтвердил
зависимость от color-key ListView: key-only дал чёрный фон, full-key и native-transparent
в отдельной серии сохранили wallpaper. Это отдельный незакрытый rendering риск,
а не подтверждение устранения проблемы Wallpaper Engine.
