// cd website-messages && node --test   needs Node 20 or later and nothing else: no Cloudflare, no network.
import assert from 'node:assert/strict';
import { beforeEach, test } from 'node:test';
import worker, { cleanText } from './worker.js';

const ORIGIN = 'https://magnarharestad.github.io';
const TOKEN = 'the-admin-token';

/** KV's behaviour that the Worker relies on: keys listed in order, metadata alongside. */
class FakeKv {
    entries = new Map();
    async get(key) { return this.entries.get(key)?.value ?? null; }
    async getWithMetadata(key) {
        const entry = this.entries.get(key);
        return { value: entry?.value ?? null, metadata: entry?.metadata ?? null };
    }
    async put(key, value, options = {}) { this.entries.set(key, { value, metadata: options.metadata }); }
    async delete(key) { this.entries.delete(key); }
    async list({ prefix, limit }) {
        const names = [...this.entries.keys()].filter(k => k.startsWith(prefix)).sort().slice(0, limit);
        return { keys: names.map(name => ({ name, metadata: this.entries.get(name).metadata })) };
    }
}

let env;
let robotCheck;

beforeEach(() => {
    env = { MESSAGES: new FakeKv(), TURNSTILE_SECRET: 'secret', ADMIN_TOKEN: TOKEN, ALLOWED_ORIGIN: ORIGIN };
    robotCheck = { success: true, hostname: 'magnarharestad.github.io' };
    globalThis.fetch = async url => {
        assert.match(String(url), /challenges\.cloudflare\.com/);
        return new Response(JSON.stringify(robotCheck));
    };
});

const call = (path, init = {}) => worker.fetch(new Request(`https://w.example${path}`, init), env);

const post = (body, headers = {}) => call('/messages', {
    method: 'POST',
    headers: { Origin: ORIGIN, 'Content-Type': 'application/json', 'CF-Connecting-IP': '192.0.2.1', ...headers },
    body: JSON.stringify(body),
});

const admin = (path, body, token = TOKEN) => call(path, {
    method: body ? 'POST' : 'GET',
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
    body: body ? JSON.stringify(body) : undefined,
});

const pendingIds = async () => (await (await admin('/admin/list')).json()).pending.map(m => m.id);
const wall = async () => (await call('/messages')).json();

test('a message waits for approval, and only then shows on the wall', async () => {
    assert.equal((await post({ text: 'Great player!', token: 't' })).status, 202);
    assert.deepEqual(await wall(), []);

    const [id] = await pendingIds();
    assert.equal((await admin('/admin/approve', { id })).status, 200);

    const messages = await wall();
    assert.equal(messages.length, 1);
    assert.equal(messages[0].text, 'Great player!');
    assert.deepEqual(await pendingIds(), []);
});

test('the wall gives the text and date, and nothing that identifies anyone', async () => {
    await post({ text: 'Hello', token: 't' });
    await admin('/admin/approve', { id: (await pendingIds())[0] });
    assert.deepEqual(Object.keys((await wall())[0]).sort(), ['at', 'text']);
    assert.ok(![...env.MESSAGES.entries.keys()].some(key => key.includes('192.0.2.1')));
});

test('a failed robot check stores nothing', async () => {
    robotCheck = { success: false };
    assert.equal((await post({ text: 'Spam', token: 't' })).status, 403);
    assert.equal(env.MESSAGES.entries.size, 0);
});

test('a robot check solved for another site is refused', async () => {
    robotCheck = { success: true, hostname: 'evil.example' };
    assert.equal((await post({ text: 'Spam', token: 't' })).status, 403);
});

test('a missing robot check token is refused', async () => {
    assert.equal((await post({ text: 'Spam' })).status, 403);
});

test('a post from another website is refused', async () => {
    assert.equal((await post({ text: 'Spam', token: 't' }, { Origin: 'https://evil.example' })).status, 403);
    assert.equal(env.MESSAGES.entries.size, 0);
});

test('one message a minute from the same visitor, another visitor is unaffected', async () => {
    assert.equal((await post({ text: 'One', token: 't' })).status, 202);
    assert.equal((await post({ text: 'Two', token: 't' })).status, 429);
    assert.equal((await post({ text: 'Three', token: 't' }, { 'CF-Connecting-IP': '192.0.2.2' })).status, 202);
});

test('the inbox stops at its limit', async () => {
    for (let i = 0; i < 200; i++)
        await env.MESSAGES.put(`pending:${String(i).padStart(16, '0')}-000000000000`, '', { metadata: { text: 'x', at: '' } });
    assert.equal((await post({ text: 'One more', token: 't' })).status, 503);
});

test('text is one line of at most 140 characters', () => {
    assert.equal(cleanText('  two\nlines\t '), 'two lines');
    assert.equal(cleanText('x'.repeat(140)).length, 140);
    assert.equal(cleanText('x'.repeat(141)), null);
    assert.equal(cleanText('   '), null);
    assert.equal(cleanText(''), null);
    assert.equal(cleanText(42), null);
    assert.equal(cleanText('😀'.repeat(140)).length, 280);      // characters, not UTF-16 units
});

test('invisible characters that could reverse a line are removed', () => {
    assert.equal(cleanText('abc\u202Edef'), 'abc def');
    assert.equal(cleanText('\u200Bhi\uFEFF'), 'hi');
});

test('markup is stored as the text it is', async () => {
    await post({ text: '<script>alert(1)</script>', token: 't' });
    await admin('/admin/approve', { id: (await pendingIds())[0] });
    assert.equal((await wall())[0].text, '<script>alert(1)</script>');
    assert.match((await call('/messages')).headers.get('Content-Type'), /application\/json/);
});

test('every admin call needs the token', async () => {
    assert.equal((await admin('/admin/list', undefined, 'wrong')).status, 401);
    assert.equal((await call('/admin/list')).status, 401);
    assert.equal((await admin('/admin/approve', { id: '0000000000000001-000000000000' }, 'wrong')).status, 401);
    assert.equal((await admin('/admin/delete', { id: '0000000000000001-000000000000' }, 'wrong')).status, 401);
});

test('without a token configured, nothing opens', async () => {
    delete env.ADMIN_TOKEN;
    assert.equal((await admin('/admin/list', undefined, 'anything')).status, 401);
});

test('a bad id is refused rather than reaching into other keys', async () => {
    assert.equal((await admin('/admin/delete', { id: '../rate:x' })).status, 400);
    assert.equal((await admin('/admin/approve', { id: 'x' })).status, 400);
    assert.equal((await admin('/admin/approve', { id: '0000000000000001-000000000000' })).status, 404);
});

test('delete takes a message off the wall', async () => {
    await post({ text: 'Oops', token: 't' });
    const [id] = await pendingIds();
    await admin('/admin/approve', { id });
    await admin('/admin/delete', { id });
    assert.deepEqual(await wall(), []);
});

test('the newest message comes first', async () => {
    const realNow = Date.now;
    try {
        Date.now = () => 1_000;
        await post({ text: 'Older', token: 't' }, { 'CF-Connecting-IP': '192.0.2.3' });
        Date.now = () => 2_000;
        await post({ text: 'Newer', token: 't' }, { 'CF-Connecting-IP': '192.0.2.4' });
    } finally {
        Date.now = realNow;
    }
    for (const id of await pendingIds())
        await admin('/admin/approve', { id });
    assert.deepEqual((await wall()).map(m => m.text), ['Newer', 'Older']);
});

test('the website may read the wall, and other sites are not told they may', async () => {
    const response = await call('/messages');
    assert.equal(response.headers.get('Access-Control-Allow-Origin'), ORIGIN);
});

test('the approval page opens without the token but shows no messages, and locks scripts down', async () => {
    await post({ text: 'Private until approved', token: 't' });
    const response = await call('/admin');
    const html = await response.text();
    assert.equal(response.status, 200);
    assert.ok(!html.includes('Private until approved'));
    assert.match(response.headers.get('Content-Security-Policy'), /script-src 'nonce-/);
    assert.ok(!/innerHTML/.test(html));
    assert.match(html, /\[hidden\] \{ display:none !important; \}/);   // the login form sets display:flex
});
