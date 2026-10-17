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
