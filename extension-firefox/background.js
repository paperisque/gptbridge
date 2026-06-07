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
      case "stop":
      case "clear":
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
browser.runtime.onMessage.addListener((msg) => {
  if (!msg) return;
  if (msg.type === "text") {
    if (!send({ type: "text", payload: msg.payload })) {
      console.warn("[Gptgraber] WS не подключён — текст не отправлен.");
    }
  } else if (msg.type === "recording") {
    if (!send({ type: "recording", payload: "" })) {
      console.warn("[Gptgraber] WS не подключён — сигнал recording не отправлен.");
    }
  }
});

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
