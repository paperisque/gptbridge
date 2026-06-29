# Changelog &nbsp;<sub>Список изменений на русском — в [docs/CHANGELOG.ru.md](docs/CHANGELOG.ru.md)</sub>

A short chronology of the work. WS protocol details — in [docs/PROTOCOL.md](docs/PROTOCOL.md);
architecture, decisions and pitfalls — in [CLAUDE.md](CLAUDE.md).

## 2026-06-29 — Standalone WebView2 version (GPT Grabber) + installer

A second, self-contained version lives in `poc-webview2/` — it embeds ChatGPT via
**WebView2** (Chromium), so it needs **no external Firefox, no extension and no network
hub**. The Firefox-based version above is unchanged. Details — [poc-webview2/README.md](poc-webview2/README.md).

- **App.** Hotkeys: Ctrl+Win (dictation), Ctrl+Win+Y (+clipboard), Ctrl+Win+Alt (re-paste
  the last text), Ctrl+Win during prepare/recognize = cancel. Lives in the tray (× hides,
  exit via the tray menu); the equalizer tray icon colour follows the phase. Status — the
  same on-top "pill" indicator. A **?** button shows hotkeys and flags.
- **Single instance.** A named mutex; launching it again just brings the running copy out
  of the tray (broadcast of a registered window message).
- **No console window.** `dotnet.exe` is a console host and SAC blocks an unsigned apphost,
  so the app relaunches itself windowless at startup (a brief flash is possible).
- **Paste sound.** A short descending two-tone "ding-dong" via `Beep` (not the system
  MessageBeep, which sounded like an error). `--no-beep` turns it off.
- **Hides ChatGPT clutter.** A CSS rule injected into `<head>` (survives React re-renders)
  hides the block after `#thread-bottom` and the header before `#thread-bottom-container`.
- **Flags:** `--lang en|de|ru` (UI language; default — Windows), `--tray` (start minimized
  to the tray), `--no-beep`. Compact startup window (740×550).
- **Data.** ChatGPT profile (WebView2) + log live next to the program in `data/`
  (`%LOCALAPPDATA%\GptGrabber\data` when installed; `bin\…\data` for a dev build).
- **Installer (Inno Setup).** One command — `tools\build-installer.ps1` (publish + a
  bundled portable .NET 10 runtime + WebView2 Runtime bootstrap + ISCC). Per-user, no
  admin, no wizard steps; clean uninstall (removes `data/`). Icon — `tools\make-icon.ps1`
  from `icons\ico2.png`.
- **Fix (extension build).** `.xpi` packaging now reads `manifest.json` as UTF-8 (PowerShell
  5.1 `Get-Content -Raw` read it as ANSI → Cyrillic in the add-on description came out garbled).
- Docs: English READMEs in their folders, Russian mirrors in `docs/`. The WS protocol
  applies to the Firefox version only (PROTOCOL.md); the WebView2 version drives the page
  via injected JS in-process.

## 2026-06-11 — Start/abort reliability (after field testing)

- **"Recording" shown, but nothing records** (FF started manually, ChatGPT tab restored
  from the session, another tab active): the previous tab was restored immediately on the
  `recording` signal, which arrives BEFORE capture actually starts — switching away from
  the ChatGPT tab killed the capture. The previous tab is now restored with a 2.5 s delay
  (mirroring the server's window-focus hold) and the restore is cancelled when a new
  session starts.
- The previous tab is now restored ALWAYS — including when the ChatGPT tab was created by
  the extension itself (previously we stayed in the created tab). The previous tab is
  remembered before any switching, as early as the preparation stage.
- **Two Firefox windows on cold start** — fixed: launch-grace (a repeated firefox.exe
  launch within 30 s is suppressed; warm-up and recording start no longer race).
- **Slow ChatGPT tab opening** — sped up: for recording the tab is created ACTIVE
  (FF loads background tabs lazily); the wait for a "foreign" tab before creating our own
  is cut to 0.8 s (but 12 s right after FF was launched by us — to avoid duplicating its
  startup tab). `ensureTab` got payload hints `start`/`warmup` + `,cold` (PROTOCOL.md).
- **Hanging on "Transcribing…" when capture never started** — abort path: a STOP without
  confirmed recording sends `cancel` (clicks "Cancel dictation") and returns to Idle right
  away; plus a safety net — no text within 20 s after STOP → paste disarmed with an error
  on the indicator. New WS message `cancel` (server → extension).
- **Recording of silence** (microphone turned away): transcription comes back empty, no
  text will arrive — the extension notices this and sends `empty`; the server bails out
  immediately ("Empty — no text") without waiting for the 20-second timeout. New WS
  message `empty`. The outcome is determined by the composer BUTTONS, not by a timer:
  the send button (`#composer-submit-button`) = text recognized; the voice-mode button in
  its place = empty. The "empty for N seconds" timer produced false "Empty" with slow
  transcription (the text arrived later and leaked into the next dictation).
- Fixed the permanently-true `IsStartingUp` (overflow in the "never launched yet" case) —
  because of it Firefox would not auto-start and tab preparation always took the slow
  cold path.
- **Cold start with session restore**: Firefox is now launched WITHOUT a URL (the URL
  produced a duplicate of the session tab), and a "lazy" (discarded) restored ChatGPT tab
  is woken up by the extension (activate/reload) — previously it silently waited for an
  answer from an unloaded page and the start failed by timeout.
- **Ctrl+Win outside the start/stop pair = universal cancel**: during preparation —
  cancels the start; during transcription after a stop — cancels the paste (a late text
  is not pasted and is cleaned out of the composer); when recording failed to start —
  abort (as before). The indicator shows "Cancelled".

## 2026-06-11 — Multilingual UI (en/de/ru)

- All static strings (server/client console + indicator) go through `Lang.T` and the JSON
  modules `lang/{en,de,ru}.json`. Defaults to the Windows language
  (`GetUserDefaultUILanguage`, since `CultureInfo` is neutralized by
  `InvariantGlobalization`); the `--lang en|de|ru` flag overrides. Fallback: chosen
  language → English → the key itself.

## 2026-06-11 — On-screen status indicator

- **A "pill" indicator on top of all windows** (`StatusOverlay.cs`): you can see what is
  happening with dictation from any application. Phases: orange twinkling sparkle
  "Preparing ChatGPT…" → orange pulsing microphone "Turning the mic on…" → green pulsing
  microphone "Recording" → blue sparkle "Transcribing…" → check mark "Pasted" (fades by
  itself). Errors — a red cross.
- A layered window without WinForms/WPF: never steals focus, clicks pass through.
  Rendering — per-pixel alpha (`UpdateLayeredWindow`) + antialiased GDI+
  (System.Drawing.Common): a smooth capsule with a white border. Icons — Segoe MDL2
  Assets glyphs, the sparkle — dingbat frames; animation — WM_TIMER.
- Anchored 3 px to the right of the text caret of the active window; for
  Electron/Chromium (which do not expose the caret) — bottom-center of the monitor,
  Whisper-style.
- A white border along the pill outline (readability on any background).
- Flags: `--overlay-compact` (icon-only circle, no label), `--no-overlay` (off),
  `--overlay-colors key=RRGGBB,…` (recolor: bg, border, text, wait, rec, busy, err).
- v1 scope — the local user of the server; the indicator is not relayed to network clients.
- `Injector.Inject` now returns success/failure (for the "Pasted"/"Paste failed" phases).

## 2026-06-10 — Reliable recording start + dev loop

- **The main fix:** the server keeps focus on the Firefox window for ≥ 1.5 s after `mic`.
  Previously focus returned on the `recording` signal (~11 ms after the dictation UI
  opened), and Firefox did not manage to actually enable the microphone — dictation never
  started.
- **Auto-preparation of Firefox/tab** (Idle→Preparing→Recording): the server launches a
  closed Firefox itself; the extension prepares the tab based on honest readiness (page
  load + dictation button + settle), with no blind delays before the click. Fixed the
  duplicate ChatGPT tab on cold start.
- Stop in a tab we created — without back-and-forth switching.
- **Extension dev tools:** `tools/update-ext.ps1` (one-command plugin update +
  auto-reload of the tab), `tools/build-xpi.ps1`, `package.json` (web-ext
  lint/build/sign).
- Millisecond timestamps in plugin and server logs.

## 2026-06-09 — Active tab

- Before starting dictation the extension activates the ChatGPT tab (Firefox does not
  record the microphone in a background, unselected tab).

## 2026-06-08 — Network mode (hub)

- The server became a hub: one Firefox dictation engine + network clients
  (`--connect <IP>`). The text returns to whoever started the session. With no arguments
  the server works as before (local Ctrl+Win + paste on the same machine).

## 2026-06-07 — Cleanup

- Debug logs removed, README shortened.

## 2026-06-07 — First release

- Voice bridge: dictation in a ChatGPT tab (Firefox) → auto-paste of the text into the
  active window. Single-key control (Ctrl+Win) via a low-level hook; paste by synthesized
  Ctrl+V (SendInput).
