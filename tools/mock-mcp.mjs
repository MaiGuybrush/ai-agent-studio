// 本機 mock MCP server（stdio）：免真實 MCP server 即可開發 MCP Tool Catalog 頁。用法：node tools/mock-mcp.mjs
// 只實作 initialize、notifications/initialized、tools/list（以換行分隔的 JSON-RPC）。純 Node、無套件。
// 設定範例見 tools/studio.config.mock-mcp.json（含一個刻意連不上的 server）。
//
// 手動驗證：
//   1. printf '%s\n' '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}' \
//        '{"jsonrpc":"2.0","method":"notifications/initialized"}' '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' | node tools/mock-mcp.mjs
//      預期：2 行回應；id 1 含 serverInfo、capabilities.tools；id 2 列出 5 個工具。
//   2. 把 tools/studio.config.mock-mcp.json 的內容貼進 apps/api/studio.config.json 的 mcpServers，從 repo 根目錄啟動 Studio，
//      開 MCP Tool Catalog 頁：看到 mock 工具；mock-unreachable 顯示「無法連線」，不影響其他 server。
import { createInterface } from 'node:readline';

const str = (description, extra = {}) => ({ type: 'string', description, ...extra });
const tools = [
  {
    name: 'get_lot_status', title: '查詢 Lot 狀態',
    description: '依 Lot ID 查詢目前站點與狀態。\n第二行：唯讀、不會改動資料。\n第三行：Lot ID 格式為 L 開頭加 8 碼數字。',
    inputSchema: { type: 'object', properties: { lotId: str('Lot ID', { pattern: '^L\\d{8}$' }), includeHistory: { type: 'boolean' } }, required: ['lotId'] },
    outputSchema: { type: 'object', properties: { lotId: { type: 'string' }, status: { type: 'string', enum: ['Run', 'Hold', 'Idle'] }, stage: { type: 'string' } }, required: ['lotId', 'status'] },
    annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true },
  },
  {
    name: 'hold_lot', title: 'Hold Lot',
    description: '將 Lot 設為 Hold。\n會改動 MES 資料，需謹慎使用。',
    inputSchema: { type: 'object', properties: { lotId: str('Lot ID'), reason: { type: 'string', enum: ['Quality', 'Equipment', 'Engineering', 'Other'] }, note: str('備註') }, required: ['lotId', 'reason'] },
    annotations: { readOnlyHint: false, destructiveHint: true },
  },
  {
    name: 'search_equipment', description: '依條件搜尋機台（巢狀 object 與 array 參數）。',
    inputSchema: {
      type: 'object',
      properties: {
        filter: { type: 'object', properties: { area: str('區域'), status: { type: 'array', items: { type: 'string', enum: ['Up', 'Down', 'PM'] } }, range: { type: 'object', properties: { from: str('起', { format: 'date' }), to: str('迄', { format: 'date' }) }, required: ['from'] } } },
        tags: { type: 'array', items: { type: 'object', properties: { key: { type: 'string' }, value: { type: 'string' } }, required: ['key', 'value'] } },
        limit: { type: 'integer', minimum: 1, maximum: 100, default: 20 },
      },
    },
    outputSchema: { type: 'object', properties: { items: { type: 'array', items: { type: 'object', properties: { id: { type: 'string' }, name: { type: 'string' } } } } } },
    annotations: { readOnlyHint: true },
  },
  { name: 'ping', description: '無參數、無 outputSchema、無 annotations 的最小工具。', inputSchema: { type: 'object', properties: {} } },
  {
    name: 'delete_recipe', description: '刪除 Recipe（不可復原）。',
    inputSchema: { type: 'object', properties: { recipeId: str('Recipe ID'), force: { type: 'boolean', default: false } }, required: ['recipeId'] },
    annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: true },
  },
];

const send = o => process.stdout.write(JSON.stringify({ jsonrpc: '2.0', ...o }) + '\n');

createInterface({ input: process.stdin }).on('line', line => {
  let m;
  try { m = JSON.parse(line); } catch { return send({ id: null, error: { code: -32700, message: 'Parse error' } }); }
  if (m.id === undefined) return; // notification（如 notifications/initialized）不回應
  if (m.method === 'initialize')
    return send({ id: m.id, result: { protocolVersion: m.params?.protocolVersion ?? '2025-06-18', capabilities: { tools: {} }, serverInfo: { name: 'mock-mcp', version: '0.0.1' } } });
  if (m.method === 'tools/list') return send({ id: m.id, result: { tools } });
  send({ id: m.id, error: { code: -32601, message: `Method not found: ${m.method}` } });
});
