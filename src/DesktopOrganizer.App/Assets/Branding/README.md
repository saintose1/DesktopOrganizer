# Branding

Знак «Лесного» интерфейса (0.4.0) — векторный лист папоротника.

- `application.ico`: лист на тёмно-моховой плитке. EXE, окно, панель задач, ярлык на рабочем столе и установщик.
- `tray.ico`: только лист, без плитки, средне-зелёный — читается на светлой и тёмной панели задач.
  На 16–24 px меньше листочков, но они крупнее и шире.
- `application-preview.png`, `tray-preview.png`: превью 256 и 64 px.
- `dog-original.png`: прежний пользовательский знак, сохранён как исходник; в сборку больше не входит.

Оба ICO: 16/20/24/32/48/64/128/256 px, 32-bit alpha. Генерация из корня:

```powershell
dotnet run --project tools/DesktopOrganizer.Branding
```

Pipeline рисует знак векторно в WPF (`DrawingContext`) и сохраняет PNG frames внутри ICO.
ApplicationIcon в csproj встраивает ICO в EXE; Window.Icon — WPF resource.
Tray ICO копируется в output/publish, загружается LoadImage и освобождается
DestroyIcon при выходе. Если Windows показывает на ярлыке старый значок, это кеш значков
Проводника: он обновится сам или после `ie4uinit.exe -show`.
