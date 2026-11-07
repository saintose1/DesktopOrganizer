# Builds a self-contained win-x64 release (no .NET runtime needed on the target PC) and, when
# Inno Setup 6 is installed, the per-user installer. Outputs go to ./artifacts (ignored by Git).
#   powershell -ExecutionPolicy Bypass -File scripts/publish.ps1
param([string]$Version = "")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dotnet = if (Test-Path "$root/.tools/dotnet/dotnet.exe") { "$root/.tools/dotnet/dotnet.exe" } else { "dotnet" }
if (-not $Version) { $Version = ([xml](Get-Content "$root/Directory.Build.props")).Project.PropertyGroup.Version }
$out = Join-Path $root "artifacts/publish/DesktopOrganizer"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

& $dotnet test "$root/DesktopOrganizer.sln" -c Release
if ($LASTEXITCODE -ne 0) { throw "Tests failed; nothing published." }
& $dotnet publish "$root/src/DesktopOrganizer.App/DesktopOrganizer.App.csproj" -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

$zip = Join-Path $root "artifacts/DesktopOrganizer-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out/*" -DestinationPath $zip
"Portable: $zip"

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    & $iscc "/DAppVersion=$Version" "/DSourceDir=$out" "/O$(Join-Path $root 'artifacts')" "$root/installer/DesktopOrganizer.iss"
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }
} else { "Inno Setup 6 not found: installer skipped (portable zip is ready)." }
