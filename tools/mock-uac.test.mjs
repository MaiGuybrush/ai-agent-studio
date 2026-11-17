// 執行：node --test tools/
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const port = 5191;
const base = `http://localhost:${port}`;
let proc;

before(async () => {
  proc = spawn(process.execPath, [fileURLToPath(new URL('./mock-uac.mjs', import.meta.url)), String(port)], { stdio: 'ignore' });
  for (let i = 0; i < 50; i++) {
    try { await fetch(`${base}/auth/SsoUrl`); return; } catch { await new Promise(r => setTimeout(r, 100)); }
  }
  throw new Error('mock-uac did not start');
});
after(() => proc?.kill());

const tokenOf = async empNo => (await (await fetch(`${base}/auth/VerifyCert`, { method: 'POST', body: JSON.stringify({ CertificateKey: `mock-${empNo}` }) })).json()).token;
const hr = (empNo, token, q = '?includeDept=true') => fetch(`${base}/HrEmployee/${empNo}${q}`, { headers: token ? { authorization: `Bearer ${token}` } : {} });

test('includeDept returns own deptCode', async () => {
  const r = await hr('10000001', await tokenOf('10000001'));
  assert.equal(r.status, 200);
  assert.equal((await r.json()).deptCode, 'CIM100');
});
test('no token -> 401', async () => assert.equal((await hr('10000001', null)).status, 401));
test('garbage token -> 401', async () => assert.equal((await hr('10000001', 'a.b.c')).status, 401));
test("someone else's empNo -> 403", async () => assert.equal((await hr('10000001', await tokenOf('10000002'))).status, 403));
test('without includeDept there is no deptCode', async () => {
  const j = await (await hr('10000001', await tokenOf('10000001'), '')).json();
  assert.equal('deptCode' in j, false);
});
test('user without dept -> deptCode null', async () => {
  const j = await (await hr('10000003', await tokenOf('10000003'))).json();
  assert.equal(j.deptCode, null);
});
test('basic-info needs no auth and returns adId', async () => {
  const r = await fetch(`${base}/HrEmployee/10000002/basic-info`);
  assert.equal(r.status, 200);
  assert.equal((await r.json()).adId, 'biz.lin');
});
test('basic-info unknown empNo -> 404', async () => assert.equal((await fetch(`${base}/HrEmployee/99999999/basic-info`)).status, 404));

// --- Personal API Key（spec §12.1）---
const keys = (method, token, path = '', payload) => fetch(`${base}/api-keys${path}`, {
  method, headers: token ? { authorization: `Bearer ${token}` } : {}, body: payload ? JSON.stringify(payload) : undefined,
});
const create = (token, payload = { label: 'k', scopes: ['*'], expiresAt: null }) => keys('POST', token, '', payload);

test('api-keys: no token -> 401 on all endpoints', async () => {
  for (const [m, p] of [['GET', ''], ['POST', ''], ['DELETE', '/x'], ['GET', '/systems']]) assert.equal((await keys(m, null, p)).status, 401);
});
test('api-keys/systems returns fixed list including *', async () => {
  const r = await keys('GET', await tokenOf('10000001'), '/systems');
  assert.equal(r.status, 200);
  const j = await r.json();
  assert.ok(Array.isArray(j) && j.includes('*') && j.length > 1);
});
test('POST returns fullKey once; GET lists without it', async () => {
  const t = await tokenOf('10000001');
  const r = await create(t, { label: 'mine', scopes: ['*'], expiresAt: '2030-01-01T00:00:00Z' });
  assert.equal(r.status, 201);
  const k = await r.json();
  assert.ok(k.fullKey.startsWith(k.keyPrefix));
  assert.deepEqual([k.empId, k.label, k.scopes, k.expiresAt, k.lastUsedAt], ['10000001', 'mine', ['*'], '2030-01-01T00:00:00Z', null]);
  assert.ok(k.id && k.createdAt);
  const got = (await (await keys('GET', t)).json()).find(x => x.id === k.id);
  assert.ok(got && !('fullKey' in got));
});
test('POST rejects * mixed with other scopes, unknown scope, empty scopes', async () => {
  const t = await tokenOf('10000001');
  const [sys] = (await (await keys('GET', t, '/systems')).json()).filter(s => s !== '*');
  for (const scopes of [['*', sys], ['no-such-system'], []]) assert.equal((await create(t, { label: 'x', scopes, expiresAt: null })).status, 400);
  assert.equal((await create(t, { label: 'x', scopes: [sys], expiresAt: null })).status, 201);
});
test('POST rejects label longer than 50 chars', async () => {
  const t = await tokenOf('10000001');
  assert.equal((await create(t, { label: 'x'.repeat(51), scopes: ['*'], expiresAt: null })).status, 400);
  assert.equal((await create(t, { label: 'x'.repeat(50), scopes: ['*'], expiresAt: null })).status, 201);
});
test("GET and DELETE only touch the caller's own keys", async () => {
  const a = await tokenOf('10000001'), b = await tokenOf('10000002');
  const k = await (await create(a)).json();
  assert.equal((await (await keys('GET', b)).json()).some(x => x.id === k.id), false);
  assert.equal((await keys('DELETE', b, `/${k.id}`)).status, 404);
  assert.equal((await keys('DELETE', a, `/${k.id}`)).status, 204);
  assert.equal((await (await keys('GET', a)).json()).some(x => x.id === k.id), false);
});
