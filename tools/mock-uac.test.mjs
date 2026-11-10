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
