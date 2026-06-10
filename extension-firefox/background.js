// Background: WS-клиент с авто-реконнектом. Живёт в персистентной фоновой
// странице (MV2) — поэтому WS-соединение держится постоянно. Принимает от
// content script сообщения {type:"text"} и форвардит их C#-серверу.
//
// WS из этого контекста не подчиняется CSP страницы ChatGPT — в этом весь смысл
// расширения вместо userscript'а.

"use strict";

// Метки времени (ЧЧ:ММ:СС.мс) на ВСЕ наши логи — чтобы ловить тайминги (фокус и пр.).
// Оборачиваем console один раз: дальше любой console.log/warn/debug печатает время первым.
try {
  const _ol = console.log.bind(console);
  const _ow = console.warn.bind(console);
  const _od = console.debug.bind(console);
  const _ts = () => {
    const d = new Date();
    const p = (n, w = 2) => String(n).padStart(w, "0");
    return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}.${p(d.getMilliseconds(), 3)}`;
  };
  console.log = (...a) => _ol(_ts(), ...a);
  console.warn = (...a) => _ow(_ts(), ...a);
  console.debug = (...a) => _od(_ts(), ...a);
} catch (e) { /* консоль не патчится — не критично */ }

const WS_URL = "ws://localhost:17890/";
const RECONNECT_MIN_MS = 1000;
const RECONNECT_MAX_MS = 15000;

// Подготовка таба ChatGPT по команде ensureTab от сервера (см. §6.16).
const CHATGPT_MATCH = ["*://chatgpt.com/*", "*://chat.openai.com/*"]; // для tabs.query (нужны host-permissions)
const CHATGPT_OPEN_URL = "https://chatgpt.com/"; // что открываем, если таба нет
const READY_POLL_MS = 300;       // как часто опрашивать content.js на готовность кнопки Start
const READY_TIMEOUT_MS = 20000;  // сколько ждать готовности таба, прежде чем сдаться
const LOAD_POLL_MS = 150;        // как часто опрашивать status таба при ожидании загрузки
const CREATE_GRACE_MS = 3000;    // ждём появления уже открывающегося таба ChatGPT, прежде чем создавать свой
// Пауза ПОСЛЕ полной загрузки СВЕЖЕСОЗДАННОГО таба, прежде чем рапортовать готовность:
// страница загрузилась (status==="complete"), но захвату диктовки нужно ещё немного
// «осесть». Только для созданного нами таба; существующий уже прогрет (см. §6.16).
const CREATED_TAB_SETTLE_MS = 1000;

let ws = null;
let reconnectDelay = RECONNECT_MIN_MS;
let reconnectTimer = null;

// Id таба с ChatGPT. Узнаём из sender.tab.id любого сообщения content script'а
// (он шлёт "register" при загрузке, а также "text"/"recording" в работе) — так не
// нужен url-фильтрованный tabs.query и доп. разрешения (см. §6.10). Нужен, чтобы
// перед стартом диктовки активировать ИМЕННО этот таб: Firefox не начинает захват
// микрофона в фоновом, невыбранном табе (см. §6.15).
let chatGptTabId = null;

// Таб, который был выбран в окне Firefox ДО того, как мы переключились на ChatGPT.
// Запоминаем на старте, возвращаем по сигналу "recording" (когда захват уже пошёл —
// переключаться обратно безопасно). null — возвращать нечего (см. §6.15).
let prevActiveTabId = null;

// Признак: таб ChatGPT открыли МЫ сами (его не было). Тогда после старта записи
// НЕ возвращаемся на прежний таб — остаёмся в созданном, не дёргаем «туда-сюда» (см. §6.15).
let createdChatGptTab = false;

// ensureTab уже выполняется — защита от создания двух табов, если ensureTab прилетит
// несколько раз подряд (старт сервера + подключение + реконнект).
let ensureInFlight = false;

function connect() {
  clearTimeout(reconnectTimer);
  try {
    ws = new WebSocket(WS_URL);
  } catch (err) {
    console.warn("[Gptgraber] WebSocket() бросил:", err);
    scheduleReconnect();
    return;
  }

  ws.onopen = () => {
    console.log("[Gptgraber] WS подключён к", WS_URL);
    reconnectDelay = RECONNECT_MIN_MS;
    send({ type: "hello", payload: "firefox extension" });
    setBadge(true);
  };

  ws.onmessage = (ev) => {
    let msg;
    try { msg = JSON.parse(ev.data); } catch { return; }
    switch (msg.type) {
      case "ack":
        console.debug("[Gptgraber] ack:", msg.payload, "симв. принято сервером");
        break;
      case "hello":
        console.log("[Gptgraber] сервер:", msg.payload);
        break;
      case "mic":
        console.log("[Gptgraber] команда от сервера: mic");
        // Сначала вывести таб ChatGPT в активные (и сфокусировать его окно), ТОЛЬКО
        // ПОТОМ кликать Start — иначе захват микрофона в фоновом табе не идёт (§6.15).
        // Никаких слепых задержек тут нет: готовность свежесозданного таба обеспечена
        // заранее, на этапе ensureTab (загрузка + осадка), см. ensureChatGptTabReady.
        activateChatGptTab().then(() => broadcastToTabs({ type: "mic" }));
        break;
      case "stop":
      case "clear":
        // Submit/clear работают и в фоновом табе — активация не нужна.
        console.log("[Gptgraber] команда от сервера:", msg.type);
        broadcastToTabs({ type: msg.type });
        break;
      case "ensureTab":
        // Сервер просит подготовить таб ChatGPT (открыть, если нет) и подтвердить
        // готовность — он по этому "ready" начнёт запись (или просто прогревается).
        console.log("[Gptgraber] команда от сервера: ensureTab");
        ensureChatGptTabReady();
        break;
      default:
        console.debug("[Gptgraber] входящее:", msg);
    }
  };

  ws.onclose = () => {
    setBadge(false);
    scheduleReconnect();
  };

  ws.onerror = () => {
    // onclose всё равно последует — там и переподключимся.
    try { ws.close(); } catch { /* ignore */ }
  };
}

function scheduleReconnect() {
  clearTimeout(reconnectTimer);
  reconnectTimer = setTimeout(connect, reconnectDelay);
  reconnectDelay = Math.min(reconnectDelay * 2, RECONNECT_MAX_MS);
}

function send(obj) {
  if (ws && ws.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify(obj));
    return true;
  }
  return false;
}

// Сообщения от content script -> на сервер: текст композера и сигнал «запись пошла».
// Заодно из sender запоминаем таб ChatGPT (для активации перед стартом диктовки).
browser.runtime.onMessage.addListener((msg, sender) => {
  if (!msg) return;
  if (sender && sender.tab && typeof sender.tab.id === "number") {
    chatGptTabId = sender.tab.id;
  }
  if (msg.type === "register") {
    console.log("[Gptgraber] зарегистрирован таб ChatGPT, id=", chatGptTabId);
  } else if (msg.type === "text") {
    if (!send({ type: "text", payload: msg.payload })) {
      console.warn("[Gptgraber] WS не подключён — текст не отправлен.");
    }
  } else if (msg.type === "recording") {
    if (!send({ type: "recording", payload: "" })) {
      console.warn("[Gptgraber] WS не подключён — сигнал recording не отправлен.");
    }
    // Захват реально пошёл — теперь можно вернуть прежний таб Firefox, не сорвав запись.
    restorePrevActiveTab();
  }
});

// Забыть таб, если он закрылся (tabs.onRemoved разрешений не требует) — иначе
// активация будет бить в мёртвый id; новый таб зарегистрируется при загрузке.
browser.tabs.onRemoved.addListener((tabId) => {
  if (tabId === chatGptTabId) {
    chatGptTabId = null;
    createdChatGptTab = false;
    console.log("[Gptgraber] таб ChatGPT закрыт — забыли его id.");
  }
  if (tabId === prevActiveTabId) prevActiveTabId = null; // возвращать уже некуда
});

// При установке/обновлении расширения content.js в уже открытых табах остаётся
// СТАРЫМ — Firefox не переинжектит content script в существующие табы. Перезагружаем
// табы ChatGPT, чтобы их content.js совпал с обновлённым background. Это и удобный
// dev-цикл (не нужно вручную жать Ctrl+R после обновления, см. tools\update-ext.ps1),
// и корректное поведение вообще. reason browser_update/прочее не трогаем.
browser.runtime.onInstalled.addListener((details) => {
  if (details.reason !== "install" && details.reason !== "update") return;
  browser.tabs.query({ url: CHATGPT_MATCH }).then((tabs) => {
    for (const t of tabs) {
      browser.tabs.reload(t.id).catch(() => { /* таб мог закрыться — ок */ });
    }
  }).catch((err) => console.warn("[Gptgraber] не смог перезагрузить табы ChatGPT после обновления:", err));
});

// Подготовить таб ChatGPT по команде ensureTab (см. §6.16): найти существующий таб
// (tabs.query по url — нужны host-permissions), при отсутствии открыть новый ПАССИВНО
// (active:false, чтобы не дёргать пользователя на прогреве). Затем дождаться готовности
// и сообщить серверу "ready". Готовность СВЕЖЕСОЗДАННОГО таба = полная загрузка страницы
// (status==="complete") + кнопка «Start dictation» + осадка CREATED_TAB_SETTLE_MS;
// у существующего таба страница уже прогрета, ждём только кнопку. Активацию таба и клик
// делает уже обработчик mic (после ready) — здесь только готовим.
async function ensureChatGptTabReady() {
  // Защита от параллельных ensureTab (на старте/реконнекте их может прилететь несколько):
  // иначе два прохода одновременно не найдут таб и создадут ДВА таба ChatGPT.
  if (ensureInFlight) {
    console.log("[Gptgraber] ensureTab уже выполняется — пропускаю повторный.");
    return;
  }
  ensureInFlight = true;
  try {
    let tabId = await findChatGptTabId();

    // Таба нет? Возможно, Firefox только что запущен сервером с ChatGPT, и стартовый таб
    // ещё не «закоммитил» свой URL — tabs.query его пока не видит. Дадим ему шанс появиться,
    // прежде чем создавать свой, иначе на старте FF получаются ДВА таба ChatGPT.
    if (tabId == null) {
      tabId = await waitForChatGptTabId(CREATE_GRACE_MS);
    }

    if (tabId == null) {
      try {
        const created = await browser.tabs.create({ url: CHATGPT_OPEN_URL, active: false });
        tabId = created.id;
        createdChatGptTab = true;   // таб наш — после старта останемся в нём
        console.log("[Gptgraber] открыл таб ChatGPT (id=" + tabId + ").");
      } catch (err) {
        console.warn("[Gptgraber] не удалось открыть таб ChatGPT:", err);
        return;
      }
    } else {
      createdChatGptTab = false;    // таб уже был / открыт сервером — вежливо вернём прежний
    }

    chatGptTabId = tabId;

    // Свежесозданный таб: сначала дождаться, что страница реально догрузилась
    // (status==="complete") — иначе кнопка диктовки может уже появиться, а захват ещё
    // не готов. Существующий таб уже загружен — этот шаг пропускаем.
    if (createdChatGptTab) {
      await waitTabLoaded(tabId, READY_TIMEOUT_MS);
    }

    const ready = await waitTabReady(tabId, READY_TIMEOUT_MS);
    if (!ready) {
      console.warn("[Gptgraber] таб ChatGPT не стал готов за " + READY_TIMEOUT_MS + " мс (нет кнопки диктовки?).");
      return;
    }

    // Свежесозданному табу даём ~1 c «осесть» после загрузки, прежде чем рапортовать
    // готовность: дальше сервер сразу пойдёт в старт записи (поднимет FF, mic), и к тому
    // моменту захват должен быть способен стартовать. Существующему табу это не нужно.
    if (createdChatGptTab) {
      await delay(CREATED_TAB_SETTLE_MS);
    }

    send({ type: "ready", payload: "" });
    console.log("[Gptgraber] таб ChatGPT готов — сообщил серверу (ready).");
  } finally {
    ensureInFlight = false;
  }
}

// Найти id таба ChatGPT: предпочесть уже известный (из register/работы), иначе первый по url.
// null — таба нет (или его url ещё не виден в tabs.query — тогда поможет waitForChatGptTabId).
async function findChatGptTabId() {
  try {
    const tabs = await browser.tabs.query({ url: CHATGPT_MATCH });
    const tab = tabs.find((t) => t.id === chatGptTabId) || tabs[0];
    if (tab) return tab.id;
  } catch (err) {
    console.warn("[Gptgraber] tabs.query(chatgpt) не удался:", err);
  }
  // chatGptTabId мог прийти из register (content.js) даже раньше, чем url стал виден в query.
  return chatGptTabId;
}

// Подождать появления таба ChatGPT (на случай только что запущенного сервером Firefox:
// его стартовый таб ещё грузится и не виден в tabs.query). null — так и не появился.
async function waitForChatGptTabId(timeoutMs) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    await delay(LOAD_POLL_MS);
    const id = await findChatGptTabId();
    if (id != null) {
      console.log("[Gptgraber] таб ChatGPT появился сам (id=" + id + ") — второй не создаю.");
      return id;
    }
  }
  return null;
}

// Дождаться полной загрузки страницы таба (status==="complete"). Для свежесозданного
// таба: пока грузится — status "loading"; "complete" = ресурсы загружены. Опрос tabs.get
// прав не требует. true — дождались, false — таймаут/таб исчез.
async function waitTabLoaded(tabId, timeoutMs) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    try {
      const t = await browser.tabs.get(tabId);
      if (t.status === "complete") return true;
    } catch {
      return false; // таб закрылся/недоступен
    }
    await delay(LOAD_POLL_MS);
  }
  return false;
}

// Опрашивать content.js, пока он не подтвердит наличие кнопки «Start dictation»
// (страница загрузилась и диктовка возможна) или не выйдет таймаут.
async function waitTabReady(tabId, timeoutMs) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    let ready = false;
    try {
      const r = await browser.tabs.sendMessage(tabId, { type: "checkReady" });
      ready = !!(r && r.ready);
    } catch { /* content.js ещё не загрузился в табе — считаем «не готов» */ }
    if (ready) return true;
    await delay(READY_POLL_MS);
  }
  return false;
}

const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

// Активировать таб ChatGPT и сфокусировать его окно перед стартом диктовки.
// Без активного таба Firefox не запускает захват микрофона (защита от скрытой
// записи), даже если окно уже на переднем плане (его поднимает сервер, см. §6.11).
// Никаких прав не требует, пока не трогаем url таба. Если таб ещё не известен —
// просто шлём команду как раньше (фолбэк на старое поведение).
async function activateChatGptTab() {
  prevActiveTabId = null;
  if (chatGptTabId == null) {
    console.debug("[Gptgraber] таб ChatGPT ещё не зарегистрирован — активация пропущена.");
    return;
  }
  try {
    // Узнать окно таба ChatGPT и какой таб в нём выбран сейчас — чтобы вернуть его
    // после старта записи. tabs.get/query без url-фильтра прав не требуют (см. §6.15).
    const chatTab = await browser.tabs.get(chatGptTabId);
    const winId = chatTab.windowId;
    // Прежний таб запоминаем ТОЛЬКО если таб ChatGPT был не наш. Если мы его создали
    // сами (createdChatGptTab) — остаёмся в нём, прежний не возвращаем (см. §6.15).
    if (!createdChatGptTab && winId != null) {
      const [active] = await browser.tabs.query({ active: true, windowId: winId });
      if (active && active.id !== chatGptTabId) prevActiveTabId = active.id;
    }
    await browser.tabs.update(chatGptTabId, { active: true });
    if (winId != null) await browser.windows.update(winId, { focused: true });
    console.log("[Gptgraber] таб ChatGPT активирован перед стартом диктовки.");
  } catch (err) {
    console.warn("[Gptgraber] не удалось активировать таб ChatGPT:", err);
    chatGptTabId = null;   // вероятно, таб закрыт/перезагружен — забудем, найдём заново
    prevActiveTabId = null;
  }
}

// Вернуть таб Firefox, который был выбран до старта диктовки. Зовётся по "recording",
// т.е. когда захват уже идёт и переключение таба его не сорвёт. Окно при этом не
// трогаем — его фокусом на уровне ОС рулит сервер (§6.11), это две независимые вещи.
async function restorePrevActiveTab() {
  if (prevActiveTabId == null) return;
  const id = prevActiveTabId;
  prevActiveTabId = null;
  try {
    await browser.tabs.update(id, { active: true });
    console.log("[Gptgraber] вернул ранее активный таб Firefox (id=" + id + ").");
  } catch (err) {
    console.warn("[Gptgraber] не удалось вернуть прежний таб:", err);
  }
}

// Разослать сообщение во все табы (где есть наш content script — обработает,
// остальные молча отвалятся). Без url-фильтра, поэтому доп. прав не нужно.
function broadcastToTabs(msg) {
  browser.tabs.query({}).then((tabs) => {
    for (const t of tabs) {
      browser.tabs.sendMessage(t.id, msg).catch(() => { /* нет приёмника — ок */ });
    }
  });
}

// Индикатор на кнопке расширения: зелёная точка = подключено.
function setBadge(connected) {
  try {
    browser.browserAction.setBadgeText({ text: connected ? "●" : "" });
    browser.browserAction.setBadgeBackgroundColor({
      color: connected ? "#2da44e" : "#cf222e",
    });
    browser.browserAction.setTitle({
      title: connected ? "Gptgraber: мост подключён" : "Gptgraber: нет связи с сервером",
    });
  } catch { /* browser_action может быть недоступен — не критично */ }
}

connect();
