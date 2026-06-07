# CLAUDE.md — Gptgraber

ТЗ и карта проекта для ассистента. Читать перед любой правкой. Язык общения и комментариев в коде — **русский**.

## 1. Что это и зачем

Голосовой мост: **диктовка в табе ChatGPT (Firefox) → текст автоматически вставляется в активное окно** (любое — VS Code, редактор, мессенджер). Идея — использовать качественную диктовку ChatGPT как системный голосовой ввод куда угодно.

Управление — одной клавишей в стиле WhisperFlow. Пользователь сидит в своём приложении, не переключаясь в браузер; расширение само кликает кнопки диктовки в ChatGPT, читает результат и отдаёт серверу, сервер вставляет текст в активное окно.

## 2. Модель управления (ГЛАВНОЕ)

Хоткеи ловит **низкоуровневый хук клавиатуры в C#-сервере** (не `RegisterHotKey` — он не умеет «только модификаторы»). Жест распознаётся на **отпускании** комбо: пока зажаты Ctrl+Win, хук следит, нажималась ли клавиша «+буфер» и не нажималась ли посторонняя клавиша (тогда жест отменяется).

- **Ctrl+Win** — тоггл диктовки:
  - из `Idle`: сервер запоминает активное окно, **на мгновение выносит окно Firefox на передний план** (иначе FF не стартует захват микрофона для фонового окна, см. §6.11), шлёт `mic` (клик «Start dictation»), состояние → `Recording`. Расширение, увидев, что запись пошла, шлёт `recording` → сервер **возвращает фокус** в рабочее окно.
  - из `Recording`: сервер запоминает **активное окно** (цель вставки), шлёт `stop` (клик «Submit dictation»), состояние → `Idle`. ChatGPT транскрибирует (1–3 с) → текст приходит → **авто-вставка в запомненное окно** (Ctrl+V) → `clear` (очистка композера).
- **Ctrl+Win+«Y»** — то же завершение, но текст **дополнительно остаётся в буфере обмена** (прежний буфер не восстанавливается). Клавиша «+буфер» ловится по **скан-коду `0x2C`** (физическая позиция, на немецкой раскладке «Y»), а НЕ по `vkCode` — vkCode зависит от раскладки (DE→0x59, RU→0x5A), скан-код одинаков. См. §6.12.
- Прочие `Ctrl+Win+<другое>` (D, стрелки) — игнорируются, не «глотаются» (системные шорткаты и Пуск работают).

**Вставка не крадёт фокус по сути:** пользователь и так в целевом окне, вставка = Ctrl+V в уже активное окно. `Injector.ForceForeground` (вынос окна на передний план через `AttachThreadInput`) используется и для подъёма Firefox на старте, и как гарантия, что цель на переднем плане перед Ctrl+V.

## 3. Структура репозитория

```
Gptgraber/
├─ CLAUDE.md                 этот файл
├─ README.md                 обзор для человека
├─ .gitignore                bin/ obj/ .vs/
├─ docs/PROTOCOL.md          контракт WS (источник правды по сообщениям)
├─ server-csharp/VoiceBridge/  C#-сервер (.NET 10, net10.0-windows, console)
│  ├─ Program.cs             entry point, цикл сообщений Win32, машина состояний Idle/Recording, возврат фокуса
│  ├─ KeyboardHook.cs        WH_KEYBOARD_LL: тоггл Ctrl+Win / Ctrl+Win+«Y» (по скан-коду)
│  ├─ WsServer.cs            HttpListener + WebSocket; приём/ack/broadcast; колбэки TextReceived / RecordingStarted
│  ├─ WindowFinder.cs        поиск окна Firefox (MozillaWindowClass + заголовок «ChatGPT») для подъёма на старте
│  ├─ Injector.cs            вставка: буфер + ForceForeground(AttachThreadInput) + SendInput(Ctrl+V скан-коды)
│  ├─ Clipboard.cs           Win32-буфер (CF_UNICODETEXT), save/restore, без WinForms
│  ├─ Native.cs              все P/Invoke, структуры, константы
│  ├─ SharedState.cs         TargetHwnd + LastText (LastText под локом)
│  ├─ WsMessage.cs           конверт {type, payload}
│  ├─ Config.cs              WsPrefix (порт 17890), тайминги
│  └─ Log.cs                 цветной консольный лог
└─ extension-firefox/        расширение (MV2, persistent background)
   ├─ manifest.json          permissions: ws://localhost:17890/*; matches: chatgpt.com / chat.openai.com
   ├─ content.js             поллинг композера + клики кнопок диктовки + сигнал recording + очистка
   ├─ background.js          WS-клиент с реконнектом, форвард команд в табы, индикатор на кнопке
   └─ README.md              как загрузить во временные дополнения
```

## 4. Сборка и запуск

**Сервер** (нужен .NET 10 SDK):
```powershell
cd server-csharp\VoiceBridge
dotnet build      # проверка
dotnet run        # запуск (или в Visual Studio: F5)
```
Окно консоли = сервер; держать открытым. Ctrl+C — выход. Порт `http://localhost:17890/` обычно открывается без админ-прав; если отказ — `netsh http add urlacl url=http://localhost:17890/ user=ДОМЕН\Пользователь`.

**Расширение** (Firefox Dev): `about:debugging#/runtime/this-firefox` → Load Temporary Add-on → `extension-firefox/manifest.json`. После правок — кнопка **Reload** там же; таб ChatGPT — **Ctrl+R**. Временное дополнение слетает при перезапуске браузера.

Проверка синтаксиса JS: `node --check content.js` / `background.js`.

## 5. WS-протокол (кратко; полностью — docs/PROTOCOL.md)

`ws://localhost:17890/`, сообщения `{ "type": "...", "payload": "..." }`, UTF-8.

- ext → сервер: `hello`, `text` (полный innerText композера), `recording` (запись реально началась).
- сервер → ext: `hello`, `ack`, `mic` (клик микрофона), `stop` (клик птички), `clear` (очистить композер).

## 6. Ключевые технические решения и ГРАБЛИ (не наступать повторно)

1. **ChatGPT пересоздаёт DOM-узлы.** `#prompt-textarea` (ProseMirror) и все кнопки композера пересоздаются после каждого манёвра; **во время записи `#prompt-textarea` вообще исчезает**. СТАБИЛЬНА только `<form>` (класс содержит `composer`).
   → Поэтому: НЕ кэшировать ссылки на узлы/кнопки. Композер читать поллингом (`setInterval`, каждые 250 мс), кнопки искать заново от формы при каждом клике. MutationObserver на конкретном узле — НЕ годится (узел умирает).

2. **Селекторы ChatGPT DOM** (aria-label на английском даже при русском UI):
   - форма: `form`, у которой `className` содержит `composer`;
   - микрофон: `button[aria-label="Start dictation"]`;
   - птичка: `button[aria-label="Submit dictation"]`;
   - отмена: `button[aria-label="Cancel dictation"]`;
   - (отправка промпта боту — отдельная `button[data-testid="send-button"]`, её НЕ трогаем).
   Есть фолбэк-поиск по подстроке `dictation|диктов`. Индикатор «запись идёт» = наличие кнопки `Submit`/`Cancel dictation`.

3. **Вставка в Electron/Chromium — только синтез ввода.** `WM_PASTE`/`WM_CHAR`/`WM_SETTEXT` не доходят до Chromium-виджета. Используем буфер обмена + `Ctrl+V` через `SendInput` **скан-кодами** (`KEYEVENTF_SCANCODE`). Посимвольный ввод не годится (кириллица/Unicode, скорость).

4. **Защита от кражи фокуса в Windows.** `SetForegroundWindow` работает, только если процесс уже на переднем плане. Обход в `Injector.ForceForeground`: `SPI_SETFOREGROUNDLOCKTIMEOUT=0` → `AttachThreadInput` (склейка очередей ввода с foreground- и target-потоком) → `SetForegroundWindow`/`SetFocus` → обязательная отвязка и возврат таймаута. Используется и для подъёма Firefox на старте, и перед Ctrl+V (гарантия, что цель на переднем плане).

5. **Низкоуровневый хук должен быть быстрым.** В колбэке `KeyboardHook.HookProc` НЕЛЬЗЯ залипать (таймаут LL-хука) — только `PostThreadMessage` себе в очередь; тяжёлую работу делает цикл сообщений. Делегат хука хранится в поле (иначе GC съест). Хук ставится на главном потоке (у него цикл сообщений).

6. **Тяжёлая работа — на потоке цикла сообщений.** И хоткей-хук, и приходящие по WS события (`text`, `recording`) маршалятся в главный поток (`WM_APP_TOGGLE`, `WM_APP_INJECT`, `WM_APP_FOCUS_BACK` через `PostThreadMessage`). У этого потока есть очередь ввода — важно для `AttachThreadInput`. Сам WS-сервер живёт в фоне.

7. **Гейт авто-вставки.** Сервер вставляет текст только если `_pendingInject` (выставляется на СТОПе). Чужие/случайные изменения композера не вставляются.

8. **Кодировка консоли.** В `Program.Main` стоит `Console.OutputEncoding = UTF8`, иначе кириллица в консоли Windows = `?`.

9. **WS из расширения, не Tampermonkey.** WS до localhost открывается из background-контекста расширения, который НЕ подчиняется CSP страницы ChatGPT. Background — persistent MV2 (MV3 event-page усыпил бы WS).

10. **Команды серверу в табы** идут через `browser.tabs.query({}) + tabs.sendMessage` (без url-фильтра → доп. прав не нужно; не-наши табы молча отваливаются).

11. **Firefox не стартует захват микрофона для окна БЕЗ фокуса.** Программный `.click()` по «Start dictation» проходит, UI открывается, но реальная запись висит, пока окно FF в фоне (защита от скрытой записи). Раз начавшись при фокусе, запись продолжается и в фоне. → Обход на старте: сервер находит окно FF (`WindowFinder`: `MozillaWindowClass` + заголовок содержит «ChatGPT»), на мгновение выносит его вперёд (`Injector.ForceForeground`), шлёт `mic`. Расширение, увидев индикатор записи, шлёт `recording` → сервер возвращает фокус в окно, активное до старта (`_returnWindow`). Страховка: `Timer` на `Config.RecordingFocusTimeoutMs` (если `recording` не пришёл — вернуть фокус всё равно). Возврат фокуса маршалится через `WM_APP_FOCUS_BACK`.

12. **Хоткей-клавиша — по скан-коду, не по vkCode.** `vkCode` зависит от раскладки: одна и та же физическая клавиша на немецкой раскладке = `VK_Y` (0x59), на русской = `VK_Z` (0x5A). Скан-код аппаратной позиции одинаков всегда. Поэтому клавиша «+буфер» ловится по `scanCode == 0x2C` (`Native.SCAN_KEEPBUFFER`), а модификаторы Ctrl/Win — по vk (они от раскладки не зависят).

## 7. Конвенции

- .NET 10, `net10.0-windows`, console, Nullable enable, ImplicitUsings.
- Имя exe/сборки — `VoiceBridge`. Имя проекта — `Gptgraber`.
- Комментарии и логи — на русском.
- Контракт WS меняем ТОЛЬКО синхронно: `docs/PROTOCOL.md` + `WsServer.OnMessageAsync` + обработчики в `content.js`/`background.js`.
- Проверять после изменений: `dotnet build` для C#, `node --check` для JS.
- В гит не коммитим `bin/`, `obj/`, `.vs/` (см. `.gitignore`).
