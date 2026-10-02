# Профилирование desktop interaction

Историческое измерение до rendering stabilization. Актуальная архитектура,
измерения с 10 реальными иконками и latency находятся в
[MILESTONE-RENDERING.md](MILESTONE-RENDERING.md).

Измерения на этой Windows-сессии 1 октября 2026 года, .NET SDK 10.0.401, Debug.
Для каждого сценария 20 прогревочных и 240 измеряемых updates, одна настоящая
desktop surface, открытый WPF-редактор. Это CPU replay, а не FPS, input latency
или субъективная оценка настоящего mouse drag. Результаты зависят от нагрузки.

## До изменений

| Участок | Mean, мс | Max, мс | Alloc/update, байт |
|---|---:|---:|---:|
| GetArea + GetScale | 0,005 | 0,009 | 3 840 |
| Полная WPF rasterization 450×350 | 1,340 | 6,137 | 638 720 |
| Drag pipeline + WPF layout | 1,635 | 7,925 | 146 311 |
| Resize pipeline + WPF layout | 4,336 | 20,234 | 1 050 008 |

Mouse preview менял ObservableCollection, выделение и editor bindings. Collection
feedback снова вызывал синхронизацию surfaces, проверку host/DPI и rendering.
Resize растеризовал весь фон 450×350+ через WPF и загружал bitmap в layered HWND.
При hover/feedback выполнялись лишние геометрические/текстовые операции.
JSON уже сохранялся после завершения gesture: причиной лагов запись не была.

## После изменений

Последний прогон включает принудительную UpdateWindow GDI-перерисовку двух
native child HWND, затем Window.UpdateLayout. В обычном приложении GDI paint
выполняет message pump; основной редактор получает preview максимум 10 раз/с.

| Участок | Mean, мс | Max, мс | Updates/sec | Alloc/update, байт |
|---|---:|---:|---:|---:|
| GetArea + GetScale | 0,001 | 0,040 | 439 721 | 0 |
| Кешированный header 450×36 | <0,001 | <0,001 | 7 121 662 | 0 |
| Native drag + GDI paint | 0,396 | 1,044 | 2 526,6 | 120 |
| Native resize + GDI paint | 2,998 | 7,317 | 333,6 | 7 565 |

Прогон без иконок изолирует rendering/input synchronization. Отдельный real-Explorer
smoke проверяет совместное движение трёх настоящих иконок, но его Task.Delay(8)
задаёт тестовую траекторию, а не ограничение производственного mouse input.
Новый pipeline не создаёт HWND/visual tree на mouse move; лёгкий bitmap header
при изменении ширины остаётся WPF, byte buffer и DrawingVisual переиспользуются.
DPI/monitor work areas кешируются на gesture, desktop discovery выключен до его
завершения, модель меняется одним immutable record replacement.

## Воспроизведение

Сначала закройте DesktopOrganizer. Из корня:

```powershell
& ./.tools/dotnet/dotnet.exe run --project tools/DesktopOrganizer.Benchmarks
```

Это временная настоящая desktop surface; JSON и пользовательские иконки не меняются.
До-метрики были записаны перед заменой pipeline; старый pipeline не дублируется
в производственном коде ради benchmark.

В Debug Interaction performance содержит Mean, Max и реальное число Preview
updates/sec по завершении одного gesture. Этот агрегат измеряет синхронную часть
native update/coordinator enqueue, без асинхронного Shell worker/GDI paint и DWM.
Он не заменяет визуальный тест быстрого движения мыши.

## Живые события мыши в запущенном приложении

В GUI-проверке в 20:35 зарегистрированы реальные desktop gestures, два назначенных
ярлыка, DPI 150%: 289 updates, Mean 0,710 мс, Max 1,418 мс, 188,6 updates/sec;
82 updates, Mean 0,858 мс, Max 1,726 мс, 272,6 updates/sec. Это Debug агрегат
синхронной части update, без Shell worker/DWM. Логи в .tools/icons-ui-smoke/Logs.
Факт ввода подтверждён логами; субъективная плавность и настоящий OLE drop
по-прежнему требуют ручного подтверждения.
