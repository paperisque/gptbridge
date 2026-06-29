# GPT Grabber &nbsp;<sub>Описание на русском — в [docs/README.poc.ru.md](../docs/README.poc.ru.md)</sub>

**Standalone voice input: dictate into ChatGPT, get the text in any window — by a hotkey.**

GPT Grabber embeds ChatGPT directly via **WebView2** (Chromium), drives its dictation, reads back the recognized text and pastes it into whatever window you are working in. Unlike the [main Firefox-based version](../README.md), it needs **no external browser, no extension and no network hub** — it is one self-contained desktop app that lives in the tray.

## Hotkeys

| Hotkey | Action |
|---|---|
| **Ctrl+Win** | Dictation: start → speak → stop → paste into the active window |
| **Ctrl+Win+Y** | Same, but the text also stays in the clipboard |
| **Ctrl+Win+Alt** | Paste the last recognized text again |
| **Ctrl+Win** (during prepare / recognize) | Cancel |

The same list is available from the **?** button in the app window.

## Install (end users)

Build the installer with a single command (from the repository root):

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
```

This produces `dist\GptGrabber-Setup-<version>.exe`. The installer:

- installs **per-user, without administrator rights**, into `%LOCALAPPDATA%\GptGrabber`;
- bundles a **portable .NET 10 runtime** (the app runs through a Microsoft-signed `dotnet.exe`, so Smart App Control does not block it — see below);
- installs the **Microsoft Edge WebView2 Runtime** if it is missing (on Windows 11 it is usually already present);
- has **no setup steps** — launch → installed → Done; Start Menu and desktop shortcuts are created automatically.

On first launch, sign in to ChatGPT once inside the app window — the session is kept.

## Run from source (dev)

Needs the **.NET 10 SDK** and the **WebView2 Runtime**.

```powershell
cd poc-webview2
dotnet run
```

A standalone `.exe` is not built (Windows Smart App Control blocks an unsigned apphost), so the app always runs through the trusted `dotnet`. For a permanent desktop launcher, point a shortcut at `dotnet.exe` with the DLL as its argument.

### Command-line flags

Flags go **after** the DLL path — `dotnet "…\WebView2Poc.dll" <flags>`:

- `--lang en|de|ru` — UI language (overrides the system language; default is the Windows UI language);
- `--tray` — start minimized to the tray.

## Good to know

- **Tray app.** Closing the window (×) hides it to the tray, it does not quit; exit via the tray menu. Microphone capture keeps working while the window is hidden or minimized.
- **Single instance.** Launching it again does not start a second copy — it brings the already-running one out of the tray.
- **Data location.** The ChatGPT profile (WebView2) and the log live next to the program, in a `data` folder: `%LOCALAPPDATA%\GptGrabber\data` for the installed app, `bin\Debug\net10.0-windows\data` for a dev build. Uninstalling removes it.
- **No console window.** As the app is launched through the console-mode `dotnet.exe`, it relaunches itself windowless at startup (a brief flash is possible); all status goes to the tray icon, the always-on-top "pill" indicator and a log file.
- **Icon.** Regenerate `installer\app.ico` from `icons\ico2.png` with `tools\make-icon.ps1`.

For the Firefox-based version see the main [README.md](../README.md); implementation notes and pitfalls of the original server are in [CLAUDE.md](../CLAUDE.md).
