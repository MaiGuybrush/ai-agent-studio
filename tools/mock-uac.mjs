// 本機 mock UAC：免內網即可開發。用法：node tools/mock-uac.mjs [port]
// 提供 /auth/SsoUrl、/auth/VerifyCert、/auth/RefreshToken、/auth/login（假登入頁）與 /.well-known/jwks.json。
// 另提供 HR（欄位名稱為推測，spec 第 14 節待確認，見 issue #15 留言）：
//   GET /HrEmployee/{empNo}?includeDept=true  需使用者 JWT，只能查自己 → { empNo, empName, adId, deptCode, deptName }
//   GET /HrEmployee/{empNo}/basic-info        免認證                   → { empNo, empName, adId }
// 使用者清單見 tools/mock-users.json。
import { createServer } from 'node:http';
import { generateKeyPairSync, createSign, createVerify } from 'node:crypto';
import { readFileSync } from 'node:fs';

const port = Number(process.argv[2] || 5090);
const origin = `http://localhost:${port}`;
const users = JSON.parse(readFileSync(new URL('./mock-users.json', import.meta.url), 'utf8'));
const { publicKey, privateKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
const b64 = o => Buffer.from(JSON.stringify(o)).toString('base64url');

// JWT 無 kid，與正式 UAC 一致。
function jwt(empNo) {
  const u = users.find(x => x.EmpNo === empNo);
  const p = `${b64({ alg: 'RS256' })}.${b64({ empNo, empID: u?.AdId, empName: u?.Name, exp: Math.floor(Date.now() / 1000) + 8 * 3600 })}`;
  return `${p}.${createSign('RSA-SHA256').update(p).sign(privateKey).toString('base64url')}`;
}
// 驗證 Authorization: Bearer <JWT>（RS256、未過期），回傳 empNo 或 null。
function verify(header) {
  const [h, p, sig] = String(header ?? '').replace(/^Bearer /i, '').split('.');
  if (!sig) return null;
  try {
    if (!createVerify('RSA-SHA256').update(`${h}.${p}`).verify(publicKey, sig, 'base64url')) return null;
    const c = JSON.parse(Buffer.from(p, 'base64url'));
    return c.exp > Date.now() / 1000 ? c.empNo : null;
  } catch { return null; }
}
// CertificateKey / refreshToken 直接編成 mock-<empNo>，免存狀態；重新整理頁面也能續登。
const login = key => {
  const empNo = String(key ?? '').replace(/^mock-/, '');
  return users.some(u => u.EmpNo === empNo) ? { token: jwt(empNo), refreshToken: `mock-${empNo}` } : null;
};

const json = (res, status, body) => res.writeHead(status, { 'content-type': 'application/json' }).end(JSON.stringify(body));
const body = req => new Promise(r => { let s = ''; req.on('data', c => s += c).on('end', () => r(s ? JSON.parse(s) : {})); });

createServer(async (req, res) => {
  res.setHeader('access-control-allow-origin', '*');
  res.setHeader('access-control-allow-headers', '*');
  const url = new URL(req.url, origin);
  if (req.method === 'OPTIONS') return res.writeHead(204).end();
  if (url.pathname === '/.well-known/jwks.json') return json(res, 200, { keys: [{ ...publicKey.export({ format: 'jwk' }), alg: 'RS256' }] });
  if (url.pathname === '/auth/SsoUrl') return json(res, 200, { loginPage: `${origin}/auth/login` });
  if (url.pathname === '/auth/VerifyCert' || url.pathname === '/auth/RefreshToken') {
    const b = await body(req);
    const result = login(b.CertificateKey ?? b.refreshToken);
    return result ? json(res, 200, result) : json(res, 401, { error: 'unknown mock user' });
  }
  if (url.pathname === '/auth/login') {
    const back = url.searchParams.get('url') ?? '';
    const rows = users.map(u => {
      const to = new URL(back); to.searchParams.set('CertificateKey', `mock-${u.EmpNo}`);
      return `<li><a href="${to.href.replace(/&/g, '&amp;')}">${u.Name}（${u.DeptCode ?? '無部門'}，${u.EmpNo}）</a></li>`;
    });
    return res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' }).end(`<meta charset="utf-8"><h3>Mock UAC：選擇登入身分</h3><ul>${rows.join('')}</ul>`);
  }
  const hr = req.method === 'GET' && url.pathname.match(/^\/HrEmployee\/([^/]+?)(\/basic-info)?\/?$/);
  if (hr) {
    const empNo = decodeURIComponent(hr[1]);
    const u = users.find(x => x.EmpNo === empNo);
    if (hr[2]) return u ? json(res, 200, { empNo, empName: u.Name, adId: u.AdId }) : json(res, 404, { error: 'not found' });
    const caller = verify(req.headers.authorization);
    if (!caller) return json(res, 401, { error: 'invalid token' });
    if (caller !== empNo) return json(res, 403, { error: 'own empNo only' });
    if (!u) return json(res, 404, { error: 'not found' });
    const out = { empNo, empName: u.Name, adId: u.AdId };
    if (url.searchParams.get('includeDept') === 'true') Object.assign(out, { deptCode: u.DeptCode ?? null, deptName: u.DeptName ?? null });
    return json(res, 200, out);
  }
  res.writeHead(404).end();
}).listen(port, () => console.log(`Mock UAC ${origin}
  API : apps/api 的 Development 設定已指向 ${origin}（Authentication:Uac:BaseUrls）；mock 重啟後金鑰會變，需重啟 API
  Web : apps/web/src/assets/config.json 的 authApiUrl 指向 ${origin}/auth`));
