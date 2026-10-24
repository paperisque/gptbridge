// Выражение для tools/cdp-snap.mjs: покой → клик кнопки диктовки → срез записи (подписи, композер) → отмена.
// Микрофон при этом включается на ~2 с. Образцы подписей: EN/DE/RU на 10.10.2026.
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const attrs = el => Object.fromEntries([...el.attributes].map(a => [a.name, a.value.length > 70 ? a.value.slice(0, 70) + '…' : a.value]));
  const chain = el => { const c = []; for (let e = el, i = 0; e && i < 7; e = e.parentElement, i++) c.push(e.tagName + (e.id ? '#' + e.id : '') + [...e.attributes].filter(a => a.name.startsWith('data-')).map(a => '[' + a.name + (a.value ? '=' + a.value.slice(0, 30) : '') + ']').join('')); return c; };
  const desc = b => { const r = b.getBoundingClientRect(); return { aria: b.getAttribute('aria-label') || '', id: b.id || '', testid: b.getAttribute('data-testid') || '', text: (b.innerText || '').trim().slice(0, 30), svg: b.querySelectorAll('svg').length, x: Math.round(r.x), w: Math.round(r.width), dis: b.disabled, cls: (b.className || '').toString().slice(0, 40) }; };
  const form = () => document.querySelector('form[data-chatgpt-composer]') || document.querySelector('form');
  const snap = () => {
    const f = form();
    const edit = [...document.querySelectorAll('[contenteditable], textarea')].map(e => ({ tag: e.tagName, attrs: attrs(e), chain: chain(e), text: (e.innerText || e.value || '').slice(0, 60) }));
    return { formAttrs: f ? attrs(f) : null, buttons: f ? [...f.querySelectorAll('button')].map(desc) : [], editable: edit,
      idsInForm: f ? [...f.querySelectorAll('[id]')].map(e => e.tagName + '#' + e.id).slice(0, 30) : [],
      testidsInForm: f ? [...f.querySelectorAll('[data-testid]')].map(e => e.tagName + '[' + e.getAttribute('data-testid') + ']').slice(0, 30) : [] };
  };
  const out = { idle: snap() };
  const start = form() && [...form().querySelectorAll('button')].find(b => /diktieren|dictate|диктов/i.test(b.getAttribute('aria-label') || ''));
  if (!start) { out.note = 'кнопка Diktieren не найдена, запись не стартовала'; return JSON.stringify(out, null, 1); }
  start.click();
  await sleep(1800);
  out.recording = snap();
  const cancel = form() && [...form().querySelectorAll('button')].find(b => /abbrechen|cancel|отмен|verwerfen|discard/i.test(b.getAttribute('aria-label') || ''));
  if (cancel) { cancel.click(); out.cancelClicked = cancel.getAttribute('aria-label'); await sleep(900); out.afterCancel = snap(); }
  else out.cancelClicked = null;
  return JSON.stringify(out, null, 1);
})()
