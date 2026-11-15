# Builds SysPulse and its installer.
#   powershell -ExecutionPolicy Bypass -File build.ps1
# Output: dist\app\SysPulse.exe (self-contained, no .NET needed)
#         dist\SysPulse-Setup-<version>.exe
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Fresh shells may not have the toolchain on PATH yet
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')

[xml]$proj = Get-Content SysPulse.csproj
$version = ($proj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { $version = '1.0.0' }
Write-Host "Building SysPulse $version" -ForegroundColor Cyan

if (Test-Path dist) { Remove-Item -Recurse -Force dist }

# Self-contained single file: runs on any 64-bit Windows 10/11 without installing .NET
dotnet publish SysPulse.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -o dist\app -nologo
# Deliberately NOT using EnableCompressionInSingleFile: it unpacks assemblies into RAM
# (~270 MB vs ~175 MB working set), and the installer's LZMA2 shrinks the raw exe better anyway.
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

& $iscc /Qp "/DAppVersion=$version" installer\SysPulse.iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

Get-ChildItem dist -File | ForEach-Object { "{0}  ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB) }
