// Content script: читает композер ChatGPT (ProseMirror, #prompt-textarea) и
// шлёт текст в background, который форвардит его C#-серверу по WS.
//
// Почему расширение, а не Tampermonkey: WS до localhost открывается из
// background-контекста расширения, который НЕ подчиняется CSP страницы ChatGPT.
//
// Почему опрос по таймеру, а не MutationObserver на узле поля: ChatGPT при
// рендере ПЕРЕСОЗДАЁТ узел #prompt-textarea, и наблюдатель, привязанный к
// конкретному узлу, после замены становится «мёртвым». Опрос каждый раз заново
// находит элемент в DOM, поэтому пересоздание узла не мешает. Заодно нет нагрузки
// от стриминга ответа ассистента (наблюдатель за документом дёргался бы на каждый токен).

(() => {
  "use strict";

  console.log("[Gptgraber] content script загружен:", location.href);

  const COMPOSER_SELECTOR = "#prompt-textarea";
  const POLL_MS = 250;   // как часто опрашивать поле
  const SETTLE_MS = 800; // текст не менялся столько -> «ввод завершён», шлём

  let prevText = "";
  let stableSince = 0;
  let lastSent = null;

  function readComposerText() {
    const el = document.querySelector(COMPOSER_SELECTOR);
    if (!el) return null; // поля сейчас нет в DOM
    //   (неразрывный пробел) -> обычный; срезаем хвостовые пробелы/переносы.
    return (el.innerText || "").replace(/ /g, " ").replace(/\s+$/g, "");
  }

  function tick() {
    const text = readComposerText();
    if (text === null) return;

    if (text === "") {
      // поле очистилось (например, отправили сообщение) — сбрасываем состояние,
      // чтобы повторная диктовка той же фразы снова отправилась.
      prevText = "";
      lastSent = null;
      return;
    }

    if (text !== prevText) {
      // поле ещё меняется — ждём, пока успокоится
      prevText = text;
      stableSince = performance.now();
      return;
    }

    // текст стабилен с прошлого опроса
    if (text !== lastSent && performance.now() - stableSince >= SETTLE_MS) {
      lastSent = text;
      browser.runtime
        .sendMessage({ type: "text", payload: text })
        .catch((err) => console.warn("[Gptgraber] sendMessage:", err));
      console.log(`[Gptgraber] текст композера отправлен (${text.length} симв.)`);
    }
  }

  // Очистка композера по команде сервера (после вставки). ProseMirror нельзя просто
  // обнулить innerHTML — он откатит правку. Надёжно: сфокусировать, выделить всё
  // и выполнить execCommand('delete') — это идёт через beforeinput, который PM
  // обрабатывает штатно. Работает и для фонового таба (delete не требует фокуса окна,
  // в отличие от clipboard-команд).
  function clearComposer() {
    const el = document.querySelector(COMPOSER_SELECTOR);
    if (!el) {
      console.warn("[Gptgraber] очистка: композер не найден");
      return;
    }
    el.focus();
    const sel = window.getSelection();
    if (sel) {
      const range = document.createRange();
      range.selectNodeContents(el);
      sel.removeAllRanges();
      sel.addRange(range);
    }
    let ok = document.execCommand("delete", false, null);
    if (!ok) ok = document.execCommand("insertText", false, "");

    // Сбрасываем состояние поллера: пустое поле — не «новый текст», а будущая
    // повторная диктовка той же фразы снова отправится.
    prevText = "";
    lastSent = null;
    console.log("[Gptgraber] композер очищен по команде сервера (ok=" + ok + ").");
  }

  // --- Кнопки диктовки ---
  // Контролы пересоздаются после каждого манёвра, СТАБИЛЬНА только <form>
  // (класс содержит "composer"). Поэтому ищем форму и кнопки ЗАНОВО при каждом
  // действии и никогда не кэшируем ссылки. Во время записи #prompt-textarea
  // исчезает, но форма остаётся — якоримся на неё, с фолбэком на документ.
  function findComposerForm() {
    return [...document.querySelectorAll("form")].find(
      (f) => (f.className || "").includes("composer")
    ) || null;
  }

  // kind: "Start" | "Submit" | "Cancel" (как в aria-label "... dictation")
  function findDictationButton(kind) {
    const root = findComposerForm() || document;
    const exact = `${kind} dictation`;
    const btn =
      root.querySelector(`button[aria-label="${exact}"]`) ||
      document.querySelector(`button[aria-label="${exact}"]`);
    if (btn) return btn;
    // Фолбэк на случай иной формулировки/локали: кнопка про диктовку с нужным словом.
    return (
      [...document.querySelectorAll("button[aria-label]")].find((b) => {
        const a = b.getAttribute("aria-label") || "";
        return /dictation|диктов/i.test(a) && new RegExp(kind, "i").test(a);
      }) || null
    );
  }

  function clickDictation(kind, human) {
    const btn = findDictationButton(kind);
    if (btn) {
      btn.click();
      console.log(`[Gptgraber] ${human}: клик по "${kind} dictation".`);
    } else {
      console.warn(`[Gptgraber] ${human}: кнопка "${kind} dictation" не найдена.`);
    }
  }

  // Запись реально идёт, если в DOM появилась кнопка завершения/отмены диктовки
  // (они существуют ТОЛЬКО во время записи). Лёгкая проверка без шумного лога —
  // её дёргаем в цикле ожидания старта.
  function isRecordingLive() {
    return !!document.querySelector(
      'button[aria-label="Submit dictation"], button[aria-label="Cancel dictation"]'
    );
  }

  // После клика Start ждём, пока запись реально начнётся, и сообщаем серверу
  // ({type:"recording"}). Сервер по этому сигналу возвращает фокус в рабочее окно
  // пользователя (на старте он на мгновение поднимает окно FF — иначе Firefox не
  // стартует захват микрофона для фонового окна).
  let recWaitTimer = null;
  function waitRecordingThenNotify() {
    clearInterval(recWaitTimer);
    const startedAt = performance.now();
    recWaitTimer = setInterval(() => {
      if (isRecordingLive()) {
        clearInterval(recWaitTimer);
        recWaitTimer = null;
        browser.runtime
          .sendMessage({ type: "recording" })
          .catch((err) => console.warn("[Gptgraber] sendMessage(recording):", err));
        console.log("[Gptgraber] запись пошла — уведомил сервер (вернуть фокус в рабочее окно).");
      } else if (performance.now() - startedAt > 3000) {
        clearInterval(recWaitTimer);
        recWaitTimer = null;
        console.warn("[Gptgraber] индикатор записи (Submit/Cancel dictation) не появился за 3 c.");
      }
    }, 100);
  }

  // Команды от background: mic (старт), stop (птичка), clear (очистить поле).
  browser.runtime.onMessage.addListener((msg) => {
    if (!msg) return;
    if (msg.type === "mic") {
      clickDictation("Start", "старт диктовки");
      waitRecordingThenNotify();
    } else if (msg.type === "stop") clickDictation("Submit", "финал диктовки");
    else if (msg.type === "clear") clearComposer();
  });

  setInterval(tick, POLL_MS);
  console.log(`[Gptgraber] опрашиваю композер раз в ${POLL_MS} мс.`);
})();
