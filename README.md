<p align="center"><img src="src/SysPulse/Assets/icon-256.png" width="96" alt="SysPulse icon"></p>

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

**Tray mode:** closing the window keeps SysPulse running in the notification area. The tray icon is a live CPU mini-graph (cyan when calm, amber when busy, red when maxed out). Hover over it for CPU, RAM and network numbers. Left-click to reopen the window; right-click for settings or **Exit**. You can turn this off with the **Close to tray** button. Windows 11 puts new tray icons in the hidden-icons overflow (**^**); drag it onto the taskbar to keep it visible.

**Settings:** update speed (0.5 s / 1 s / 2 s), always on top, close to tray and the window position are remembered between runs. They're stored in `%APPDATA%\SysPulse\settings.json`.

**Command line:** `SysPulse.exe --tray` starts hidden in the tray (the installer's "start when I sign in" option uses this). Launching SysPulse while it's already running just brings the existing window to the front.

## Monitor other PCs on your network

Install **SysPulse Agent** on each PC you want to watch. It runs as a background Windows service, starts with Windows, and works even when nobody is signed in. Then add those PCs in SysPulse; each appears in the machine bar at the top with a live status dot and CPU/RAM. Click one to see its full dashboard.

1. On the other PC, run `SysPulse-Agent-Setup-<version>.exe` (needs administrator rights). The last page shows its **address** and a **pairing code**. You can show them again later from Start ▸ *SysPulse Agent - Show pairing code*.
2. In SysPulse, click **+ Add PC** and enter the address and code.
3. To stop monitoring a PC, right-click its chip ▸ **Remove this PC**.

**Requirements:** both PCs on the same local network, and the agent PC's network set to **Private** in Windows (Settings ▸ Network & internet ▸ your connection ▸ *Private network*). The agent doesn't accept connections on Public networks.

**Security**
- **Read-only.** The agent only reports readings; nothing can be changed remotely.
- **Encrypted.** All traffic is HTTPS. The agent makes its own certificate, and SysPulse pins its fingerprint when you pair, so another device on the network can't impersonate it.
- **Pairing code required** on every request. It's 120 random bits, and wrong codes are slowed down. To issue a new code (which disconnects existing viewers), run `"C:\Program Files\SysPulse Agent\SysPulse.Agent.exe" --reset-code` as administrator.
- **Local network only.** The firewall rule allows only TCP port 47613, only for the agent, only from your local subnet, and only on Private/Domain networks. The agent also rejects non-private addresses itself.
- **Stored safely.** The agent's code and certificate key are in `%ProgramData%\SysPulse Agent`, readable only by Administrators and SYSTEM. On the viewer PC, saved codes are encrypted with Windows DPAPI for your user account.
- The agent runs as LocalSystem so it can see every process (as Task Manager does for an administrator).

**Footprint:** with nobody watching, the agent pauses sampling (about 0.2% of one CPU core, ~95 MB RAM). While watched it samples once a second and serves all viewers from the same reading. SysPulse also stops polling remote PCs while its window is hidden.

## Install

Download `SysPulse-Setup-<version>.exe` (and `SysPulse-Agent-Setup-<version>.exe` for PCs you want to monitor remotely) from [Releases](https://github.com/destruction4u/SysPulse/releases) and run it.

- Installs for the current user by default, without an admin prompt. You can choose to install for all users instead.
- Bundles .NET, so nothing else needs to be installed.
- Optional desktop shortcut and "start when I sign in".
- Remove it from **Settings → Apps → Installed apps**.

Windows SmartScreen may warn that the installer is from an unknown publisher, because it isn't code-signed. Click **More info → Run anyway**.

## Build from source

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download) and, for the installer, [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`).

Run the app:

```powershell
dotnet run --project src\SysPulse -c Release
```

Run the agent in a console for testing (no admin needed; uses a throwaway data folder and prints nothing until a viewer connects):

```powershell
dotnet run --project src\SysPulse.Agent -c Release -- --data-dir $env:TEMP\syspulse-agent-test
```

Build both standalone exes and both installers into `dist\`:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

To release a new version, bump `<Version>` in `src\SysPulse\SysPulse.csproj` (and/or `src\SysPulse.Agent\SysPulse.Agent.csproj`) and run `build.ps1`. The installers pick up the versions automatically.

> **Keep the project in a short path** (e.g. `C:\dev\SysPulse`). When the build's intermediate paths
> go over Windows' 260-character limit, the WPF compiler silently leaves out `MainWindow.xaml`, and the
> exe crashes on launch with "Cannot locate resource 'mainwindow.xaml'".

## Project layout

| Path | What it does |
|---|---|
| **`src/SysPulse.Core/`** | Shared by the app and the agent |
| `Monitoring/StaticInfo.cs` | One-time hardware/OS facts (WMI + registry) |
| `Monitoring/SystemMonitor.cs` | Live sampling: perf counters, network, processes, battery |
| `Monitoring/Native.cs` | Win32 calls and formatting helpers |
| `Remote/AgentClient.cs` | HTTPS client with certificate pinning |
| `Remote/PairingCode.cs`, `AgentProtocol.cs` | Pairing codes and the wire format |
| **`src/SysPulse.Agent/`** | The Windows service |
| `Program.cs` | HTTPS endpoints, local-network + pairing-code checks |
| `SampleCache.cs` | Samples once a second while watched, idles otherwise |
| `Cli.cs` | `--install`, `--uninstall`, `--pairing`, `--reset-code` (service, firewall, setup) |
| `AgentConfig.cs` | Pairing code + self-signed certificate in a locked-down ProgramData folder |
| **`src/SysPulse/`** | The desktop app |
| `MainWindow.xaml` | Machine bar, dashboard layout and styling |
| `AddMachineWindow.xaml` | Pairing dialog |
| `ViewModels/` | Dashboard (per machine), machine bar and window state |
| `Services/` | Settings, saved machines (DPAPI), tray icon |
| `Controls/Sparkline.cs` | The scrolling line graphs |
| `Assets/` | Icon (`.ico` plus a 256 px PNG), shared by everything |
| `tools/make-icon.ps1` | Regenerates the icon from vector drawing code |
| `installer/*.iss` | Inno Setup scripts for the app and the agent |
| `build.ps1` | Publishes both and builds both installers |

## License

[MIT](LICENSE)
