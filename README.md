# Gptgraber &nbsp;<sub>Описание на русском языке — в [docs/README.ru.md](docs/README.ru.md)</sub>

**Voice bridge: ChatGPT dictation → text in any window, via a hotkey.**

You dictate by voice, ChatGPT transcribes your speech in a background Firefox tab — and on **Ctrl+Win** the text appears right in the window you are working in (VS Code, an editor, a messenger, anything). No need to switch to the browser. Over a local network the text can even be delivered to another computer.

> **Note — there is also a newer, self-contained version: _GPT Grabber_ ([`poc-webview2/`](poc-webview2/README.md)).** It embeds ChatGPT directly via WebView2, so it needs **no external Firefox and no browser extension** — a single app installed in a couple of clicks. The Firefox + extension + C# hub version described below still works and is documented here.

## Why it's useful

- **ChatGPT quality, everywhere.** Recognition with punctuation and support for many languages — not just inside the chat, but in any application.
- **No separate paid dictation tool needed** — you reuse the dictation already built into ChatGPT (similar in spirit to WhisperFlow).
- **You never leave your work.** The trigger is a single hotkey from any application; focus briefly visits the browser and comes right back to you.
- **You can see what's going on.** A small indicator (next to the text caret or at the bottom of the screen): a sparkle for "preparing", a green microphone for "speak now", a check mark for "pasted". It can be shrunk to an icon (`--overlay-compact`), recolored (`--overlay-colors`) or turned off (`--no-overlay`).
- **Works over the network.** One computer with Firefox serves dictation to several machines on the LAN — the text lands in the active window of the machine where the hotkey was pressed.

## How it works

**Ctrl+Win** is caught by a low-level keyboard hook in the C# server. The server tells the extension to click the dictation button in ChatGPT; the extension reads the transcription and sends it to the server over WebSocket; the server delivers the text to the active window. The paste target is the window that was active **at the moment dictation was stopped**.

```
 ┌────────────┐  Ctrl+V (synthetic) ┌──────────────┐   mic/stop/clear   ┌────────────┐
 │   active   │ ◄────────────────── │  VoiceBridge │ ─────────────────► │  Firefox   │
 │window (tgt)│                     │   (server)   │ ◄───────────────── │  (ChatGPT) │
 └────────────┘                     └──────────────┘   text/recording   └────────────┘
```

### Over the network: one Firefox — many machines

The server acts as a **hub**. The Firefox "dictation engine" is needed only on the server machine. There can be several "controllers" (those who catch the hotkey and receive text into their window): a local one is built into the server itself, and network ones connect from other machines with `VoiceBridge --connect <server-IP>`. A network client catches its own Ctrl+Win and sends a request to the server; the server drives the shared Firefox and returns the transcribed text **exactly to the one** who started the dictation — to be pasted into their active window.

```
   Laptop (network client)              Desktop (server + Firefox)
  ┌──────────────────────┐  start/stop ┌──────────────┐  mic/stop/clear  ┌────────────┐
  │ Ctrl+Win → request   │ ──────────► │  VoiceBridge │ ───────────────► │  Firefox   │
  │ paste into own window│             │    (hub)     │ ◄─────────────── │  (ChatGPT) │
  └──────────────────────┘ ◄────────── └──────────────┘  text/recording  └────────────┘
                            inject(text)
```

Dictation is a shared resource of a single Firefox: while one session is running, requests from other controllers are ignored ("busy"). There is no auth/token — it is designed for a trusted local network.

## How to use it

### Locally, on one machine

1. Start the server and load the extension (see "Install and run"). A green dot ● on the extension button means the bridge is connected.
2. Work in your application. When you want to dictate:
   - **Ctrl+Win** — start. The Firefox window flashes for a moment (that's how the browser allows microphone capture), focus returns to you immediately. An indicator appears near the caret; when the microphone turns green — speak.
   - **Ctrl+Win** again — stop. In 1–3 s the transcribed text is pasted into your window.
   - **Ctrl+Win+Y** — same as stop, but the text additionally stays in the clipboard.
   - **Ctrl+Win in any intermediate phase** (preparation in progress, recording failed to start, transcription in progress) — cancels everything, the indicator shows "Cancelled".

### From another computer on the LAN

1. On the machine with Firefox start the server in network mode, on the client machine start the network client (see "Network access").
2. On the client — the same hotkeys: **Ctrl+Win** start/stop, **Ctrl+Win+Y** to keep the text in the clipboard. On start, Firefox flashes **on the server** (you don't see it), and the text lands in the active window of **your** machine.

## Install and run

You need the **.NET 10 SDK**. The project does not build a standalone `.exe`: Windows Smart App Control (SAC) blocks it, so it always runs through `dotnet` (which is signed and trusted). Argument help — `dotnet run -- --help`. UI language follows Windows (English/German/Russian); override with `--lang en|de|ru`.

### Server

```powershell
cd server-csharp\VoiceBridge
dotnet run
```

The console window is the server (logs live there too), `Ctrl+C` — quit. By default it listens on `localhost:17890` — this works without administrator rights. If the port fails to open with an access error, run once in an admin console:

```powershell
netsh http add urlacl url=http://localhost:17890/ user=DOMAIN\User
```

### Firefox extension

`about:debugging#/runtime/this-firefox` → **Load Temporary Add-on…** → pick `extension-firefox/manifest.json`. A green dot ● on the extension button = WebSocket connected to the server. More details — in [extension-firefox/README.md](extension-firefox/README.md).

### Network access (only if you really need it)

**On the server machine** (the one with Firefox) start the server on all interfaces:

```powershell
dotnet run -- --host +
```

and open access once (admin console): URL reservation + a firewall rule **for the local subnet only** (the port is not exposed to the internet — the machine sits behind NAT with a private IP):

```powershell
netsh http add urlacl url=http://+:17890/ user=DOMAIN\User
netsh advfirewall firewall add rule name="VoiceBridge 17890" dir=in action=allow protocol=TCP localport=17890 remoteip=localsubnet
```

**On the client machine** (no Firefox or extension needed there — only .NET):

```powershell
dotnet run -- --connect 192.168.1.50    # IP of the server machine
```

The default port is `17890`; change it with `--port` (then also adjust it in the extension: `extension-firefox/background.js` and `manifest.json`).

## Under the hood

- **Control.** A low-level keyboard hook (`WH_KEYBOARD_LL`) catches Ctrl+Win (a "modifier-only toggle", which plain `RegisterHotKey` cannot do). The "+clipboard" key is recognized by its **scan code**, so it does not depend on the keyboard layout.
- **Recording start.** Firefox does not capture the microphone while its window is in the background. So the server briefly brings the browser window to the front, recording starts, and focus immediately returns to your working window.
- **Pasting.** The program puts the text into the clipboard and synthesizes a Ctrl+V keystroke **itself** via `SendInput` (scan codes) — you press nothing. This is the only way to deliver text into Electron/Chromium fields (e.g. VS Code), and it pastes any Unicode correctly.
- **Indicator.** A "pill" window on top of everything, rendered with per-pixel alpha (a layered `WS_EX_NOACTIVATE` window + antialiased GDI+ — it never steals focus, clicks pass through) and anchored to the text caret of the active window; where the system does not expose the caret (Electron/Chromium) — to the bottom of the screen, Whisper-style.
- **Transport.** WebSocket on port `17890`. The server is a hub: it distinguishes the Firefox extension from network clients, remembers the owner of the current session and returns the transcribed text exactly to whoever started it.
- **Launch.** One project — two modes: with no arguments it is the server, with `--connect <IP>` it is a network client. Both reuse the same pasting and hook code.

Implementation details and pitfalls — in [CLAUDE.md](CLAUDE.md), the full WebSocket message contract — in [docs/PROTOCOL.md](docs/PROTOCOL.md).
