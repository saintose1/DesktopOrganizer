# Branding

- `dog-original.png`: неизменённый пользовательский branding PNG.
- `application.ico`: EXE, окно и taskbar; восемь PNG frames внутри ICO.
- `tray.ico`: упрощённая собака, прозрачный фон, без зерна и мелких рамок.
- `tray-preview.png`: увеличенное превью упрощённого знака.

Оба ICO: 16/20/24/32/48/64/128/256px, 32-bit alpha. Генерация из корня:

```powershell
dotnet run --project tools/DesktopOrganizer.Branding
```

Pipeline использует WPF bitmap encoder и векторные формы для малой tray icon.
ApplicationIcon в csproj встраивает ICO в EXE; Window.Icon — WPF resource.
Tray ICO копируется в output/publish, загружается LoadImage и освобождается
DestroyIcon при выходе. Содержимое Explorer icons не относится к этому pipeline.
