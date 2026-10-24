// Срез композера ChatGPT из живой страницы GPT Grabber по CDP (CLAUDE.md §8.11).
// Программу запустить с --devtools-port 9333. Node 22+ (встроенный WebSocket).
//   node tools/cdp-snap.mjs                         — кнопки формы и композер в покое
//   node tools/cdp-snap.mjs tools/cdp-expr-rec.js   — покой → клик «Диктовать» → срез записи → отмена
// Пока порт открыт, любой локальный процесс имеет доступ к сессии ChatGPT — после разбора перезапустить без порта.
const PORT = 9333;
const list = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
const page = list.find(p => p.type === 'page' && /chatgpt\.com|chat\.openai\.com/.test(p.url));
if (!page) { console.error('нет страницы ChatGPT:', list.map(p => p.url)); process.exit(1); }

const expr = process.argv[2]
  ? await (await import('node:fs/promises')).readFile(process.argv[2], 'utf8')
  : `(() => {
  const attrs = el => Object.fromEntries([...el.attributes].map(a => [a.name, a.value.length > 80 ? a.value.slice(0, 80) + '…' : a.value]));
  const vis = el => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el); return { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height), disp: s.display, visib: s.visibility }; };
  const desc = b => ({ tag: b.tagName, id: b.id || '', testid: b.getAttribute('data-testid') || '', aria: b.getAttribute('aria-label') || '', title: b.title || '', text: (b.innerText || '').trim().slice(0, 40), cls: (b.className || '').toString().slice(0, 60), svg: b.querySelectorAll('svg').length, role: b.getAttribute('role') || '', ...vis(b) });
  const forms = [...document.querySelectorAll('form')].map(f => ({ attrs: attrs(f), buttons: [...f.querySelectorAll('button')].map(desc) }));
  const dict = [...document.querySelectorAll('button,[role=button]')].filter(b => /dictat|diktat|диктов|voice|sprach|speech|голос/i.test((b.getAttribute('aria-label') || '') + ' ' + (b.getAttribute('data-testid') || '') + ' ' + (b.title || ''))).map(desc);
  const tb = document.querySelector('#thread-bottom');
  const tbc = document.querySelector('#thread-bottom-container');
  const composer = document.querySelector('#prompt-textarea');
  return JSON.stringify({ url: location.href, lang: document.documentElement.lang, forms, dictLike: dict, threadBottom: !!tb, threadBottomContainer: !!tbc, composer: composer ? { tag: composer.tagName, attrs: attrs(composer) } : null,
    composerParentChain: composer ? (() => { const c = []; let e = composer; for (let i = 0; i < 8 && e; i++, e = e.parentElement) c.push(e.tagName + (e.id ? '#' + e.id : '') + (e.getAttribute('data-type') ? '[data-type=' + e.getAttribute('data-type') + ']' : '')); return c; })() : null }, null, 1);
})()`;

const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
ws.send(JSON.stringify({ id: 1, method: 'Runtime.evaluate', params: { expression: expr, returnByValue: true, awaitPromise: true } }));
const msg = await new Promise(res => { ws.onmessage = e => res(JSON.parse(e.data)); });
ws.close();
if (msg.result?.exceptionDetails) { console.error('JS error:', JSON.stringify(msg.result.exceptionDetails, null, 1)); process.exit(2); }
const v = msg.result?.result?.value;
console.log(typeof v === 'string' ? v : JSON.stringify(v, null, 1));
