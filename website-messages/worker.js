// The website's message wall: a visitor leaves one line, you approve it, and it shows on the site.
// A Cloudflare Worker, because GitHub Pages cannot receive or store anything. See README.md.
//
// In KV, under a key that sorts newest first, with the message in the key's metadata so a listing
// costs one read rather than one per message:
//   pending:<id>   waiting for you
//   approved:<id>  on the website
//   rate:<hash>    a visitor who just wrote; expires by itself. Holds no address.

const MAX_LENGTH = 140;
const MAX_PENDING = 200;     // a flood stops here rather than filling the store
const WALL_SIZE = 50;
const RATE_SECONDS = 60;     // the shortest expiry KV allows
const SITEVERIFY = 'https://challenges.cloudflare.com/turnstile/v0/siteverify';

const json = (body, status = 200, headers = {}) =>
    new Response(JSON.stringify(body), {
        status,
        headers: { 'Content-Type': 'application/json', 'X-Content-Type-Options': 'nosniff', ...headers },
    });

/** One line of plain text, or null: no control characters, and none of the invisible ones that
 *  can reverse how a line reads. */
export function cleanText(value) {
    if (typeof value !== 'string')
        return null;
    const text = value
        .replace(/[\u0000-\u001F\u007F-\u009F\u200B-\u200F\u2028-\u202E\u2060-\u2069\uFEFF]/g, ' ')
        .replace(/\s+/g, ' ')
        .trim();
    const length = [...text].length;
    return length >= 1 && length <= MAX_LENGTH ? text : null;
}

async function sha256(text) {
    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
    return [...new Uint8Array(digest)].map(b => b.toString(16).padStart(2, '0')).join('');
}

/** Compared as digests, so the time it takes says nothing about the token. */
async function isAdmin(request, env) {
    const header = request.headers.get('Authorization') ?? '';
    if (!env.ADMIN_TOKEN || !header.startsWith('Bearer '))
        return false;
    const [given, wanted] = await Promise.all([sha256(header.slice(7)), sha256(env.ADMIN_TOKEN)]);
    let difference = 0;
    for (let i = 0; i < wanted.length; i++)
        difference |= given.charCodeAt(i) ^ wanted.charCodeAt(i);
    return difference === 0;
}

async function passedRobotCheck(token, env) {
    if (typeof token !== 'string' || token.length === 0 || token.length > 2048)
        return false;
    const response = await fetch(SITEVERIFY, {
        method: 'POST',
        body: new URLSearchParams({ secret: env.TURNSTILE_SECRET, response: token }),
    });
    if (!response.ok)
        return false;
    const result = await response.json();
    return result.success === true && result.hostname === new URL(env.ALLOWED_ORIGIN).hostname;
}

async function list(env, prefix, limit) {
    const { keys } = await env.MESSAGES.list({ prefix, limit });
    return keys.map(key => ({ id: key.name.slice(prefix.length), text: key.metadata?.text, at: key.metadata?.at }));
}

function newId(now) {
    const newestFirst = String(9_000_000_000_000_000 - now).padStart(16, '0');
    return `${newestFirst}-${crypto.randomUUID().replaceAll('-', '').slice(0, 12)}`;
}

const validId = id => typeof id === 'string' && /^\d{16}-[0-9a-f]{12}$/.test(id);

// ------------------------------------------------------------------------------- the website

async function leaveMessage(request, env, cors) {
    if (Number(request.headers.get('Content-Length') ?? 0) > 4096)
        return json({ error: 'Too long.' }, 413, cors);

    let body;
    try {
        body = await request.json();
    } catch {
        return json({ error: 'That did not arrive right.' }, 400, cors);
    }

    const text = cleanText(body?.text);
    if (text === null)
        return json({ error: `Write one line, up to ${MAX_LENGTH} characters.` }, 400, cors);
    if (!await passedRobotCheck(body?.token, env))
        return json({ error: 'The robot check did not pass. Please try again.' }, 403, cors);

    const visitor = `rate:${await sha256(`${env.TURNSTILE_SECRET}:${request.headers.get('CF-Connecting-IP') ?? ''}`)}`;
    if (await env.MESSAGES.get(visitor) !== null)
        return json({ error: 'One message a minute, please.' }, 429, cors);

    const { keys } = await env.MESSAGES.list({ prefix: 'pending:', limit: MAX_PENDING });
    if (keys.length >= MAX_PENDING)
        return json({ error: 'The inbox is full for now. Please try again later.' }, 503, cors);

    const now = Date.now();
    await env.MESSAGES.put(`pending:${newId(now)}`, '', {
        metadata: { text, at: new Date(now).toISOString().slice(0, 10) },
    });
    await env.MESSAGES.put(visitor, '1', { expirationTtl: RATE_SECONDS });
    return json({ ok: true }, 202, cors);
}

// ------------------------------------------------------------------------------- your approval page

const ADMIN_PAGE = nonce => `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex"><title>MagSID messages</title>
<style nonce="${nonce}">
body { margin:0 auto; max-width:760px; padding:20px 16px; background:#0e1018; color:#dfe4ee; font:16px/1.5 "Segoe UI", system-ui, sans-serif; }
[hidden] { display:none !important; }
h1 { font-size:22px; } h2 { font-size:16px; margin-top:28px; color:#8f99ad; }
form { display:flex; gap:8px; flex-wrap:wrap; }
input { flex:1; min-width:200px; padding:8px 10px; background:#141b29; color:#dfe4ee; border:1px solid #263048; border-radius:8px; font:inherit; }
button { padding:6px 14px; background:#1c2436; color:#dfe4ee; border:1px solid #263048; border-radius:8px; font:inherit; cursor:pointer; }
button:hover { border-color:#8f99ad; }
li { display:flex; gap:10px; align-items:center; flex-wrap:wrap; padding:10px 0; border-bottom:1px solid #263048; list-style:none; }
ul { padding:0; margin:0; } .text { flex:1; min-width:200px; overflow-wrap:anywhere; } time, #status { color:#8f99ad; font-size:14px; }
</style></head><body>
<h1>MagSID messages</h1>
<form id="login"><input id="token" type="password" autocomplete="current-password" placeholder="Admin token" required aria-label="Admin token"><button>Open</button></form>
<p id="status" role="status"></p>
<div id="lists" hidden><h2>Waiting for you</h2><ul id="pending"></ul><h2>On the website</h2><ul id="approved"></ul></div>
<script nonce="${nonce}">
let token = '';
const $ = id => document.getElementById(id);
const say = text => { $('status').textContent = text; };
async function call(path, body) {
  const response = await fetch(path, {
    method: body ? 'POST' : 'GET',
    headers: { Authorization: 'Bearer ' + token, 'Content-Type': 'application/json' },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (response.status === 401) { token = ''; $('lists').hidden = true; $('login').hidden = false; throw new Error('Wrong token.'); }
  if (!response.ok) throw new Error('That did not work (' + response.status + ').');
  return response.json();
}
function row(message, buttons) {
  const li = document.createElement('li');
  const text = document.createElement('span'); text.className = 'text'; text.textContent = message.text;
  const at = document.createElement('time'); at.textContent = message.at;
  li.append(text, at);
  for (const [label, path] of buttons) {
    const button = document.createElement('button'); button.textContent = label;
    button.addEventListener('click', () => act(path, message.id));
    li.append(button);
  }
  return li;
}
async function act(path, id) {
  try { await call(path, { id }); await load(); } catch (error) { say(error.message); }
}
async function load() {
  const { pending, approved } = await call('/admin/list');
  $('pending').replaceChildren(...pending.map(m => row(m, [['Approve', '/admin/approve'], ['Delete', '/admin/delete']])));
  $('approved').replaceChildren(...approved.map(m => row(m, [['Delete', '/admin/delete']])));
  say(pending.length === 0 ? 'Nothing is waiting.' : pending.length + ' waiting.');
  $('lists').hidden = false; $('login').hidden = true;
}
$('login').addEventListener('submit', async event => {
  event.preventDefault();
  token = $('token').value; $('token').value = '';
  try { await load(); } catch (error) { say(error.message); }
});
</script></body></html>`;

function adminPage() {
    const nonce = crypto.randomUUID();
    return new Response(ADMIN_PAGE(nonce), {
        headers: {
            'Content-Type': 'text/html; charset=utf-8',
            'Cache-Control': 'no-store',
            'Referrer-Policy': 'no-referrer',
            'Content-Security-Policy':
                `default-src 'none'; script-src 'nonce-${nonce}'; style-src 'nonce-${nonce}'; connect-src 'self'; ` +
                "base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
        },
    });
}

async function admin(request, env, path) {
    if (request.method === 'GET' && path === '/admin')
        return adminPage();

    if (!await isAdmin(request, env))
        return json({ error: 'Wrong token.' }, 401);

    if (request.method === 'GET' && path === '/admin/list') {
        const [pending, approved] = await Promise.all([
            list(env, 'pending:', MAX_PENDING),
            list(env, 'approved:', 1000),
        ]);
        return json({ pending, approved });
    }

    if (request.method === 'POST' && (path === '/admin/approve' || path === '/admin/delete')) {
        const body = await request.json().catch(() => null);
        if (!validId(body?.id))
            return json({ error: 'No such message.' }, 400);

        if (path === '/admin/delete') {
            await Promise.all([env.MESSAGES.delete(`pending:${body.id}`), env.MESSAGES.delete(`approved:${body.id}`)]);
            return json({ ok: true });
        }

        const { metadata } = await env.MESSAGES.getWithMetadata(`pending:${body.id}`);
        if (!metadata)
            return json({ error: 'No such message.' }, 404);
        await env.MESSAGES.put(`approved:${body.id}`, '', { metadata });
        await env.MESSAGES.delete(`pending:${body.id}`);
        return json({ ok: true });
    }

    return json({ error: 'Not found.' }, 404);
}

// ------------------------------------------------------------------------------- routing

export default {
    async fetch(request, env) {
        const path = new URL(request.url).pathname.replace(/\/+$/, '') || '/';

        if (path === '/admin' || path.startsWith('/admin/'))
            return admin(request, env, path);

        if (path !== '/messages')
            return json({ error: 'Not found.' }, 404);

        const cors = { 'Access-Control-Allow-Origin': env.ALLOWED_ORIGIN, Vary: 'Origin' };

        if (request.method === 'OPTIONS') {
            return new Response(null, {
                status: 204,
                headers: {
                    ...cors,
                    'Access-Control-Allow-Methods': 'GET, POST',
                    'Access-Control-Allow-Headers': 'Content-Type',
                    'Access-Control-Max-Age': '86400',
                },
            });
        }

        if (request.method === 'GET') {
            const messages = await list(env, 'approved:', WALL_SIZE);
            return json(messages.map(({ text, at }) => ({ text, at })), 200, { ...cors, 'Cache-Control': 'public, max-age=60' });
        }

        if (request.method === 'POST') {
            if (request.headers.get('Origin') !== env.ALLOWED_ORIGIN)
                return json({ error: 'Not from the website.' }, 403, cors);
            return leaveMessage(request, env, cors);
        }

        return json({ error: 'Not allowed.' }, 405, cors);
    },
};
