<p align="right"><a href="docs/README.ru.md">Описание на русском</a></p>

# GPT Grabber

**Voice input for the whole computer: you dictate, ChatGPT recognizes, the text lands in the window you work in. One hotkey.**

GPT Grabber embeds ChatGPT through **WebView2** (the Chromium engine built into Windows), presses its dictation buttons for you, reads back the recognized text and pastes it into the active window: VS Code, an editor, a messenger, anything. It is a single tray app. No external browser, no extension, no server to babysit.

Since version 0.1.8 it also has a **door for other programs**: an HTTP entry that takes an audio file and returns the text. Programs on this computer and on other computers in the network can knock. Your scripts, bots and helpers can use the same ChatGPT recognition you dictate with. Details in [Recognize a file from another program](#recognize-a-file-from-another-program).

## Why it's useful

- **ChatGPT quality, everywhere.** Punctuation, many languages, decent handling of mumbling. Not only inside the chat, but in any application.
- **No separate paid dictation tool.** You reuse the dictation already built into your ChatGPT account (similar in spirit to WhisperFlow).
- **You never leave your work.** One hotkey from any app. The window stays in the tray, the microphone works while it is hidden.
- **You see what's going on.** A small "pill" next to the caret or at the bottom of the screen: preparing, recording, recognizing, pasted. The tray icon changes color in sync.
- **Other programs can use it too.** An HTTP entry turns a WAV file into text for programs on this computer and in the network, with a polite queue: your own dictation always comes first.

## Hotkeys

| Hotkey | Action |
|---|---|
| **Ctrl+Win** | Dictation: start → speak → stop → paste into the active window |
| **Ctrl+Win+Y** | Same, but the text also stays in the clipboard |
| **Win+Alt** | Paste the last recognized text again |
| **Ctrl+Win** (while preparing / recognizing) | Cancel |

The same list is behind the **?** button in the app window. The "+clipboard" key is matched by its physical position, so it works on any keyboard layout (Y on German, Z on English, Я on Russian).

## Install

Build the installer with one command from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
```

You get `dist\GptGrabber-Setup-<version>.exe`. The installer:

- installs **per user, without administrator rights**, into `%LOCALAPPDATA%\GptGrabber`;
- bundles a **portable .NET 10 runtime**: the app runs through a Microsoft-signed `dotnet.exe`, so Smart App Control does not block it;
- installs the **Microsoft Edge WebView2 Runtime** if it is missing (Windows 11 usually has it);
- asks **no questions**: launch, installed, done. Start Menu and desktop shortcuts appear by themselves.

On first launch, sign in to ChatGPT once inside the app window. The session is kept.

## Run from source

Needs the **.NET 10 SDK** and the **WebView2 Runtime**.

```powershell
cd poc-webview2
dotnet run
```

No standalone `.exe` is built: Windows Smart App Control blocks an unsigned one. The app always runs through the trusted `dotnet`. For a permanent launcher, point a shortcut at `dotnet.exe` with the DLL as its argument.

### Command-line flags

Flags go **after** the DLL path: `dotnet "…\WebView2Poc.dll" <flags>`.

- `--lang en|de|ru` sets the UI language. By default it follows Windows.
- `--tray` starts hidden in the tray.
- `--no-beep` turns off the confirmation sound on paste.
- `--api-host ADDR` sets where the entry for other programs listens. The default is `0.0.0.0`, the whole network. `127.0.0.1` keeps the entry for this computer only.
- `--api-port N` sets the entry port. The default is `17891`.
- `--devtools-port N` opens the Chromium debugging port on `127.0.0.1:N`, to inspect the embedded page when ChatGPT changes its markup. **Keep it off unless you are debugging:** while it is open, any program on this computer can drive the page and reach your ChatGPT session.

## Recognize a file from another program

GPT Grabber listens on port **`17891`**. Any program on this computer or on another computer in the network can send it a WAV file and get the recognized text back. Inside, the grabber plays the file into ChatGPT dictation instead of the microphone: nothing comes out of the speakers, nothing is pasted anywhere, your clipboard and focus are untouched.

### Who can knock

- **This computer** uses `http://127.0.0.1:17891`.
- **Other computers in the network** use the address of the grabber's computer, for example `http://192.168.1.20:17891`. The grabber writes this computer's addresses to its log at startup.
- **No web pages.** A request with an `Origin` header (that is, from a browser page) gets `403`. The `Host` header must be an IP address, `localhost` or this computer's name with the entry port, otherwise `403` too. This blocks DNS rebinding tricks.
- **No key.** The entry is open to everyone who sees the computer in the network. If that is too much, `--api-host 127.0.0.1` keeps it for this computer only. `--api-port` changes the port.

On the first start Windows may ask whether to let the program into the network. Allowing it may need administrator rights. Without that permission the entry works for this computer only.

### The simple path in three steps

**1. Make sure the grabber is running.** It runs from the tray. If not, start it hidden:

```bat
"%LOCALAPPDATA%\GptGrabber\dotnet\dotnet.exe" "%LOCALAPPDATA%\GptGrabber\WebView2Poc.dll" --tray
```

A second launch is harmless: it just shows the already running copy. Startup takes a few seconds, so poll the status for up to 20 s.

**2. Check the status.**

```powershell
curl.exe -s http://127.0.0.1:17891/status
```

```json
{"ready":true,"state":"idle","reason":"","version":"0.1.8","maxAudioSec":360}
```

`ready:true` means: come in. `state` is `idle`, `live` (the user is dictating) or `file` (another file is in progress). `reason` says why it is not ready: `live`, `file`, `pause` or `not_ready`. When there is an estimate, `retryAfterSec` says when to come back.

**3. Send the file.** Either the WAV itself in the body, which works from any computer:

```powershell
curl.exe -s -H "Content-Type: audio/wav" --data-binary "@C:\path\to\voice.wav" http://127.0.0.1:17891/transcribe
```

A copy of the sent file lands in the inbox folder `data\inbox` next to the program. After a successful recognition the copy is deleted, failed ones stay for inspection.

Or the path to the file in JSON. **This computer only:** the path points to the grabber's disk, so the grabber refuses it from another machine. Cyrillic and spaces in the path are fine, the body is UTF-8:

```powershell
$json = @{ path = 'C:\Голосовые\запись 1.wav' } | ConvertTo-Json
Invoke-RestMethod http://127.0.0.1:17891/transcribe -Method Post `
  -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($json))
```

The answer comes when the text is ready. ChatGPT listens in real time, so a one-minute file takes about a minute plus a few seconds. Wait at least `audio length + 60 s` before giving up.

```json
{"id":"gg-20261109-141008-0003","requestId":"","text":"Imagine my feelings at that moment…","audioSec":20,"recognizeMs":1003}
```

> **PowerShell trap.** In Windows PowerShell the word `curl` is not curl at all, it is an alias for `Invoke-WebRequest`, which does not understand `-s` or `-H`. Type `curl.exe`.

### Optional headers

- `X-Client: MyBot` is the name of your program.
- `X-Request-Id: mybot-0001` is your own request number. It comes back as `requestId` in the answer.

Both go into the grabber log, so it is easy to find who asked what.

### Answers

| Code | `error` | What happened | What to do |
|---|---|---|---|
| 200 | | Text is in `text` | Use it |
| 409 | `busy` | The grabber is busy: the user is dictating, has just finished, or another file is running | Wait `retryAfterSec` (if present) and try again |
| 409 | `preempted` | The user pressed Ctrl+Win in the middle of your file. Your file was dropped | Try the whole file again later |
| 503 | `not_ready` | ChatGPT page is not ready (still loading, no dictation button) | Try again in a few seconds |
| 503 | `session` | ChatGPT session has expired. A human has to sign in again in the app window | Tell the human |
| 400 | `bad_file` | File not found, not a WAV, unreadable, or a path came from another computer | Fix the request, send the file itself over the network |
| 413 | `too_long` | Longer than 360 s | Cut the file into pieces |
| 422 | `empty` | No text came out, probably silence | Nothing to recognize |
| 504 | `timeout` | Recognition did not finish within 60 s | Try again |

Every error answer carries `id`, `requestId`, `error` and a human-readable `message`.

### Who goes first

The human at the keyboard always wins. That is the whole etiquette:

- **During the user's own dictation** files get `busy` without an estimate: nobody knows how long a person will talk.
- **For 10 s after each dictation** files still get `busy`, with `retryAfterSec` showing the time left. Each new dictation restarts the 10 s. People often dictate in bursts, and a file sneaking into the gap would get in the way.
- **If the user presses Ctrl+Win during a file**, the file is dropped on the spot, the caller gets `preempted`, and the user's dictation starts immediately.
- **Only one file at a time.** The next one gets `busy` with an estimate: the rest of the current audio plus about 3 s.
- **If your program hangs up** in the middle of a request, the grabber stops feeding the file and is free again right away. No ghost work blocks the next caller.

So a well-behaved caller is a simple loop: send, on `409` wait `retryAfterSec` (or a few seconds) and send again.

```powershell
do {
  try { $r = Invoke-RestMethod http://127.0.0.1:17891/transcribe -Method Post -ContentType 'audio/wav' -InFile 'C:\path\to\voice.wav'; break }
  catch {
    if ($_.Exception.Response.StatusCode.value__ -ne 409) { throw }
    $e = $_.ErrorDetails.Message | ConvertFrom-Json
    Start-Sleep -Seconds ([Math]::Max(3, [int]$e.retryAfterSec))
  }
} while ($true)
$r.text
```

### Known limitation

Do not press the dictation buttons on the ChatGPT page by hand while a file is running. The grabber does not notice that yet: it waits for the end of the audio and may return a fragment or a timeout. Ctrl+Win works correctly. A fix is on the way.

## Good to know

- **Tray app.** Closing the window (×) hides it to the tray. Exit via the tray menu.
- **Single instance.** Launching it again brings the running copy out of the tray instead of starting a second one.
- **Paste sound.** A short two-tone chime confirms a paste. Turn it off with `--no-beep`.
- **Tidier ChatGPT.** The embedded page hides some clutter around the composer so it does not jump around in a small window.
- **Data location.** The ChatGPT profile and the log `poc.log` live next to the program in a `data` folder: `%LOCALAPPDATA%\GptGrabber\data` for the installed app, `poc-webview2\bin\Debug\net10.0-windows\data` for a dev build. Uninstalling removes it.
- **No console window.** The app is launched through console-mode `dotnet.exe`, so it relaunches itself windowless at startup. A brief flash is possible.

Implementation notes and pitfalls are in [CLAUDE.md](CLAUDE.md), section 8.

---

<sub>The earlier version of the project, dictation in a Firefox tab driven by an extension and a C# hub with network clients, still lives in [server-csharp/](server-csharp/) and [extension-firefox/](extension-firefox/README.md). It is kept as is and not described here any more.</sub>
