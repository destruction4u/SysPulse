# Builds SysPulse (desktop app) and SysPulse Agent (service), and both installers.
#   powershell -ExecutionPolicy Bypass -File build.ps1
# Output: dist\app\SysPulse.exe, dist\agent\SysPulse.Agent.exe (self-contained, no .NET needed)
#         dist\SysPulse-Setup-<version>.exe, dist\SysPulse-Agent-Setup-<version>.exe
#         dist\SysPulse-Agent-linux-{x64,arm64}-<version>.tar.gz
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Fresh shells may not have the toolchain on PATH yet
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')

function Get-Version([string]$csproj) {
    [xml]$proj = Get-Content $csproj
    $v = $proj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
    if ($v) { $v } else { '1.0.0' }
}

$appVersion = Get-Version 'src\SysPulse\SysPulse.csproj'
$agentVersion = Get-Version 'src\SysPulse.Agent\SysPulse.Agent.csproj'
Write-Host "Building SysPulse $appVersion and SysPulse Agent $agentVersion" -ForegroundColor Cyan

if (Test-Path dist) { Remove-Item -Recurse -Force dist }

# Self-contained single files: run on any 64-bit Windows 10/11 without installing .NET.
# Deliberately NOT using EnableCompressionInSingleFile: it unpacks assemblies into RAM
# (~270 MB vs ~175 MB working set), and the installer's LZMA2 shrinks the raw exe better anyway.
$publish = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none', '-nologo')

dotnet publish src\SysPulse\SysPulse.csproj @publish -o dist\app
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (app) failed" }
dotnet publish src\SysPulse.Agent\SysPulse.Agent.csproj @publish -o dist\agent
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (agent) failed" }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

& $iscc /Qp "/DAppVersion=$appVersion" installer\SysPulse.iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed (app)" }
& $iscc /Qp "/DAppVersion=$agentVersion" installer\SysPulseAgent.iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed (agent)" }

# Linux agent: self-contained single file per CPU type, packed with install.sh as a .tar.gz
foreach ($arch in 'x64', 'arm64') {
    $stage = "dist\linux-$arch\syspulse-agent"
    dotnet publish src\SysPulse.Agent\SysPulse.Agent.csproj -c Release -r "linux-$arch" --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -nologo -o "dist\linux-$arch\bin"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (linux-$arch) failed" }
    New-Item -ItemType Directory -Force $stage | Out-Null
    Move-Item "dist\linux-$arch\bin\SysPulse.Agent" "$stage\syspulse-agent"
    Remove-Item -Recurse -Force "dist\linux-$arch\bin"
    Copy-Item installer\linux\install.sh, LICENSE $stage
    [IO.File]::WriteAllText("$PWD\$stage\ARCH", $arch)
    $tgz = "dist\SysPulse-Agent-linux-$arch-$agentVersion.tar.gz"
    tar -czf $tgz -C "dist\linux-$arch" syspulse-agent   # tar.exe ships with Windows 10+
    if ($LASTEXITCODE -ne 0) { throw "tar failed ($arch)" }
}

Get-ChildItem dist -File | ForEach-Object { "{0}  ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB) }
