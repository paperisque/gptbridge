// Background: WS-клиент с авто-реконнектом. Живёт в персистентной фоновой
// странице (MV2) — поэтому WS-соединение держится постоянно. Принимает от
// content script сообщения {type:"text"} и форвардит их C#-серверу.
//
// WS из этого контекста не подчиняется CSP страницы ChatGPT — в этом весь смысл
// расширения вместо userscript'а.

"use strict";

const WS_URL = "ws://localhost:17890/";
const RECONNECT_MIN_MS = 1000;
const RECONNECT_MAX_MS = 15000;

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
        // Сначала вывести таб ChatGPT в активные (и сфокусировать его окно),
        // ТОЛЬКО ПОТОМ кликать Start — иначе захват микрофона в фоновом табе не идёт.
        activateChatGptTab().then(() => broadcastToTabs({ type: "mic" }));
        break;
      case "stop":
      case "clear":
        // Submit/clear работают и в фоновом табе — активация не нужна.
        console.log("[Gptgraber] команда от сервера:", msg.type);
        broadcastToTabs({ type: msg.type });
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
    console.log("[Gptgraber] таб ChatGPT закрыт — забыли его id.");
  }
  if (tabId === prevActiveTabId) prevActiveTabId = null; // возвращать уже некуда
});

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
    if (winId != null) {
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
