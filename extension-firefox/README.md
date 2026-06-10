# Расширение Firefox — Gptgraber Voice Bridge

Читает композер ChatGPT, кликает кнопки диктовки по командам сервера и шлёт распознанный текст C#-серверу `VoiceBridge` по WebSocket.

## Файлы

- `manifest.json` — MV2, персистентный background (нужен для постоянного WS), host-доступ к `ws://localhost:17890/*`, content script на `chatgpt.com` / `chat.openai.com`.
- `content.js` — поллит `#prompt-textarea` (ProseMirror) каждые 250 мс; когда текст стабилизировался, шлёт `innerText` в background. По командам сервера кликает кнопки диктовки (`Start`/`Submit dictation`), сигналит `recording`, когда запись пошла, и чистит композер.
- `background.js` — WS-клиент с авто-реконнектом (экспоненциальная задержка до 15 с); форвардит текст и сигнал `recording` серверу, команды сервера — в табы; индикатор на кнопке.

## Почему MV2, а не MV3

В Firefox MV3 фоновый скрипт — событийная страница, которая засыпает и выгружается. Для постоянно открытого WebSocket это плохо: соединение будет рваться. MV2 с `"persistent": true` держит фоновую страницу (и WS) живой.

## Почему расширение, а не Tampermonkey

WS до `localhost` открывается из background-контекста расширения, который **не подчиняется CSP страницы ChatGPT**. Userscript исполняется в контексте страницы — его WS-подключение зарезал бы `connect-src` из CSP ChatGPT.

## Почему поллинг, а не MutationObserver

ChatGPT пересоздаёт узел `#prompt-textarea` (и кнопки) после каждого манёвра, а во время записи поле вообще исчезает. Наблюдатель, привязанный к узлу, после замены становится «мёртвым». Поллинг каждый раз заново находит элемент в DOM. По той же причине кнопки ищутся заново при каждом клике, от стабильной `<form>` (класс содержит `composer`).

## Установка (временно, для разработки)

1. Запусти C#-сервер (`server-csharp`), убедись, что он слушает `ws://localhost:17890/`.
2. В Firefox открой `about:debugging#/runtime/this-firefox`.
3. **Load Temporary Add-on…** → выбери `extension-firefox/manifest.json`.
4. На кнопке расширения появится зелёная точка ● — WS подключён. Красная/пусто — сервер не запущен (background сам переподключится, когда сервер поднимется).

После правок в коде расширения — кнопка **Reload** там же; таб ChatGPT обнови **Ctrl+R**.

> Временное дополнение выгружается при перезапуске браузера. Для постоянной работы (и чтобы заработала авто-подготовка «запустить закрытый Firefox», см. CLAUDE.md §6.16) установи расширение постоянно — одним из способов ниже.

## Установка постоянно — A. Firefox Developer Edition / ESR / Nightly (локально, без аккаунта)

Эти сборки позволяют отключить проверку подписи и поставить свой `.xpi` навсегда.

1. Собери `.xpi`:
   ```powershell
   powershell -ExecutionPolicy Bypass -File tools\build-xpi.ps1
   ```
   Появится `dist\gptgraber-<version>.xpi` (manifest.json в корне архива).
2. В Firefox: `about:config` → принять риск → найти `xpinstall.signatures.required` → **false**.
3. `about:addons` → шестерёнка ⚙ → **Install Add-on From File…** → выбери собранный `.xpi` → подтверди установку.
4. Расширение останется после перезапуска браузера. Обновление — см. раздел «Быстрое обновление» ниже (одна команда).

> На обычном **Firefox Release/Beta** `xpinstall.signatures.required` игнорируется — там только способ B.

## Быстрое обновление постоянного расширения (dev-цикл)

Когда расширение уже стоит постоянно (способ A), обновлять его на каждую правку — **одна команда**:

```powershell
powershell -ExecutionPolicy Bypass -File tools\update-ext.ps1
```

Скрипт сам: (1) сгенерирует растущую версию (база из `manifest.json` + секунды от 2024-01-01 — исходный `manifest.json` **не меняется**, версия живёт только в собранном `.xpi`); (2) упакует `dist\gptgraber-dev.xpi`; (3) откроет его в Firefox (предпочтёт уже запущенный экземпляр — тот же профиль, где расширение и стоит). Остаётся **один клик «Add»/«Добавить»** в окне Firefox.

- **Версию руками бампить не нужно** — растёт сама, поэтому Firefox всегда видит «обновление» и ставит поверх (ID `gptgraber@local` стабилен → не плодит копии).
- **Таб ChatGPT обновлять руками тоже не нужно** — `background.js` ловит `runtime.onInstalled` и перезагружает табы ChatGPT сам (новый `content.js` подхватится).
- Опции: `-NoLaunch` — только собрать, не открывать Firefox; `-FirefoxPath "C:\...\firefox.exe"` — явный путь, если автопоиск не нашёл.

Итог цикла: правишь код → `update-ext.ps1` → клик «Add» → готово. `dist\` в гит не коммитим (`.gitignore`).

## Установка постоянно — B. Подпись через AMO (работает в любом Firefox)

«Unlisted»-подпись: AMO подписывает `.xpi` автоматически (без публикации в каталоге), и его можно поставить в любой Firefox.

Один раз: заведи аккаунт на addons.mozilla.org → Developer Hub → **Manage API Keys** → сгенерируй JWT **issuer** и **secret**.

```powershell
npm install                              # поставит web-ext (devDependency)
$env:WEB_EXT_API_KEY    = "<issuer>"     # НЕ коммить ключи
$env:WEB_EXT_API_SECRET = "<secret>"
npm run lint:ext                         # проверить расширение линтером AMO
npm run sign:ext                         # подписать -> dist\gptgraber-<version>.xpi (подписанный)
```
Поставь подписанный `.xpi` через `about:addons → Install Add-on From File`.

> Нюансы: расширение на **MV2** — AMO пока подписывает, но Mozilla сворачивает MV2 (долгосрочно может потребоваться миграция на MV3). Permission `ws://localhost:17890/*` даст **warning** линтера — для unlisted это не блокирует подпись. Каждой новой подписи нужна уникальная `version` в `manifest.json`.

## Проверка

1. Сервер запущен, точка зелёная.
2. Встань в любое окно (например, VS Code). Нажми **Ctrl+Win** — окно Firefox мелькнёт, запись стартует, фокус вернётся.
3. Продиктуй фразу, нажми **Ctrl+Win** ещё раз. Через 1–3 с текст вставится в твоё окно.

Логи расширения: `about:debugging` → у дополнения **Inspect** → вкладка Console (отдельно для content и для background). Логи сервера — в его консоли.

Проверка синтаксиса: `node --check content.js` / `node --check background.js`.
