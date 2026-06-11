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

  // Метки времени (ЧЧ:ММ:СС.мс) на наши логи — для стыковки таймингов с background/сервером.
  // Патчим console в изолированном мире content script (страницы ChatGPT не касается).
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

  // Кнопка отправки промпта — НАДЁЖНЫЙ индикатор исхода распознавания (наблюдение
  // пользователя): она появляется, ТОЛЬКО если в композере есть текст. Если запись
  // была тишиной — её нет, вместо неё возвращается «стартовая» кнопка голосового
  // режима (справа от диктовки; мы её не используем).
  // Ищем цепочкой: id (первично) → data-testid → aria-label "Send prompt". id/testid
  // могут со временем сменить, aria-label может быть локализован — потому все три.
  function hasComposerSubmit() {
    return !!(
      document.querySelector("#composer-submit-button") ||
      document.querySelector('button[data-testid="send-button"]') ||
      document.querySelector('button[aria-label="Send prompt"]')
    );
  }

  // Кнопка ГОЛОСОВОГО РЕЖИМА (справа от диктовки; мы её не используем) — позитивный
  // индикатор исхода «пусто» (наблюдение пользователя): при тишине она возвращается
  // на место кнопки отправки. Пока идёт распознавание, нет НИ её, НИ кнопки отправки.
  function hasVoiceModeButton() {
    const root = findComposerForm() || document;
    if (root.querySelector('button[data-testid="composer-speech-button"]')) return true;
    return [...root.querySelectorAll("button[aria-label]")].some((b) =>
      /voice mode|голосов/i.test(b.getAttribute("aria-label") || "")
    );
  }

  // После Submit следим за ИСХОДОМ распознавания и ждём ОДНОЗНАЧНОГО сигнала:
  //   кнопка отправки (или текст) -> распознался текст, выходим молча (текст отправит
  //   обычный поллер tick); кнопка голосового режима -> «пусто», шлём {type:"empty"} —
  //   иначе сервер висел бы в «Распознаю…» до страховочного таймаута.
  // ВАЖНО: «пусто» НЕ определяется тупым таймером — медленное распознавание давало
  // ложное «пусто», и текст прошлой сессии примешивался к следующей. Таймер остаётся
  // лишь страховкой на случай, если селекторы кнопок устареют.
  const EMPTY_CONFIRM_MS = 700;          // осадка после появления голосовой кнопки
  const EMPTY_FALLBACK_MS = 8000;        // нет НИКАКИХ сигналов столько -> считаем «пусто»
  const EMPTY_WATCH_TIMEOUT_MS = 30000;  // дольше не следим (страховка от вечного интервала)
  let emptyWatchTimer = null;
  function watchTranscriptionOutcome() {
    clearInterval(emptyWatchTimer);
    const startedAt = performance.now();
    let uiGoneAt = 0;
    let voiceSeenAt = 0;
    emptyWatchTimer = setInterval(() => {
      if (hasComposerSubmit() || readComposerText()) { // распознался текст — обычный поток
        clearInterval(emptyWatchTimer);
        emptyWatchTimer = null;
        return;
      }
      if (isRecordingLive()) {
        uiGoneAt = 0; // UI ещё открыт — ждём
        voiceSeenAt = 0;
      } else {
        const now = performance.now();
        if (!uiGoneAt) uiGoneAt = now;

        if (!hasVoiceModeButton()) {
          voiceSeenAt = 0; // исход ещё не определён (распознавание может идти)
        } else if (!voiceSeenAt) {
          voiceSeenAt = now;
        }

        // Голосовая кнопка вернулась (+осадка против гонки с поздней вставкой текста) —
        // или вообще никаких сигналов слишком долго (фолбэк на случай смены селекторов).
        if ((voiceSeenAt && now - voiceSeenAt >= EMPTY_CONFIRM_MS)
            || now - uiGoneAt >= EMPTY_FALLBACK_MS) {
          clearInterval(emptyWatchTimer);
          emptyWatchTimer = null;
          browser.runtime
            .sendMessage({ type: "empty" })
            .catch((err) => console.warn("[Gptgraber] sendMessage(empty):", err));
          console.log("[Gptgraber] распознано пусто (" + (voiceSeenAt ? "вернулась кнопка голосового режима" : "фолбэк по таймеру") + ") — уведомил сервер (empty).");
          return;
        }
      }
      if (performance.now() - startedAt > EMPTY_WATCH_TIMEOUT_MS) {
        clearInterval(emptyWatchTimer);
        emptyWatchTimer = null;
      }
    }, 250);
  }

  // Команды от background: mic (старт), stop (птичка), cancel (отмена без вставки),
  // clear (очистить поле), checkReady (готова ли страница — есть ли кнопка «Start dictation»).
  browser.runtime.onMessage.addListener((msg) => {
    if (!msg) return;
    if (msg.type === "checkReady") {
      // Возвращаем Promise — это и есть ответ для background (sendMessage его получит).
      return Promise.resolve({ ready: !!findDictationButton("Start") });
    }
    if (msg.type === "mic") {
      clearInterval(emptyWatchTimer);
      emptyWatchTimer = null;
      clickDictation("Start", "старт диктовки");
      waitRecordingThenNotify();
    } else if (msg.type === "stop") {
      clickDictation("Submit", "финал диктовки");
      watchTranscriptionOutcome();
    }
    else if (msg.type === "cancel") {
      // Отбой зависшей сессии (захват не стартовал): закрыть UI диктовки без распознавания.
      clearInterval(recWaitTimer);
      recWaitTimer = null;
      clearInterval(emptyWatchTimer);
      emptyWatchTimer = null;
      clickDictation("Cancel", "отмена диктовки");
    }
    else if (msg.type === "clear") clearComposer();
  });

  // Регистрируемся в background: сообщаем, что в этом табе живёт ChatGPT, чтобы
  // перед стартом диктовки background мог активировать ИМЕННО этот таб. Firefox не
  // начинает захват микрофона в фоновом (невыбранном) табе — активный таб обязателен
  // (см. §6.15; §6.11 — про окно, это про таб внутри окна). Сам id таба background
  // берёт из sender этого сообщения, поэтому payload не нужен.
  browser.runtime
    .sendMessage({ type: "register" })
    .catch((err) => console.warn("[Gptgraber] sendMessage(register):", err));

  setInterval(tick, POLL_MS);
  console.log(`[Gptgraber] опрашиваю композер раз в ${POLL_MS} мс.`);
})();
