<p align="center"><img src="Assets/icon-256.png" width="96" alt="SysPulse icon"></p>

# SysPulse

A neofetch-style system info panel plus a live resource monitor for Windows 10/11.

**Left panel:** OS, hardware model, kernel build, uptime, CPU, GPU, memory, disks, display, battery, local IP, theme and BIOS, with storage bars. **Copy info** puts it on the clipboard as text.

**Right panel (updates every second):**
- CPU %, current clock speed, 60-second graph
- Memory used / total and commit charge
- GPU 3D-engine load
- Network download/upload speed
- Disk active time and read/write speed
- Battery level and charging state
- Per-thread CPU bars
- Top processes by CPU, with memory use

**Always on top** keeps it floating over other windows.

## Install

Download `SysPulse-Setup-<version>.exe` from [Releases](https://github.com/destruction4u/SysPulse/releases) and run it.

- Installs for the current user by default, without an admin prompt. You can choose to install for all users instead.
- Bundles .NET, so nothing else needs to be installed.
- Optional desktop shortcut and "start when I sign in".
- Remove it from **Settings → Apps → Installed apps**.

Windows SmartScreen may warn that the installer is from an unknown publisher, because it isn't code-signed. Click **More info → Run anyway**.

## Build from source

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download) and, for the installer, [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`).

Run the app:

```powershell
dotnet run -c Release
```

Build the standalone exe and the installer into `dist\`:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

To release a new version, bump `<Version>` in `SysPulse.csproj` and run `build.ps1`. The installer picks up the version automatically.

> **Keep the project in a short path** (e.g. `C:\dev\SysPulse`). When the build's intermediate paths
> go over Windows' 260-character limit, the WPF compiler silently leaves out `MainWindow.xaml`, and the
> exe crashes on launch with "Cannot locate resource 'mainwindow.xaml'".

## Project layout

| Path | What it does |
|---|---|
| `Services/StaticInfo.cs` | One-time hardware/OS facts (WMI + registry) |
| `Services/SystemMonitor.cs` | Live sampling: perf counters, network, processes, battery |
| `Services/Native.cs` | Win32 calls (memory, power, dark title bar) and formatting helpers |
| `ViewModels/MainViewModel.cs` | Turns samples into text and graph history for the UI |
| `Controls/Sparkline.cs` | The scrolling line graphs |
| `MainWindow.xaml` | Layout and styling |
| `Assets/` | App icon (`.ico` plus a 256 px PNG) |
| `tools/make-icon.ps1` | Regenerates the icon from vector drawing code |
| `installer/SysPulse.iss` | Inno Setup installer script |
| `build.ps1` | Publishes the app and builds the installer |
