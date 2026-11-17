import { Component, computed, inject, signal } from '@angular/core';
import { McpServer, McpTool, McpToolsService } from '../../core/mcp-tools/mcp-tools.service';
import { McpToolDetailComponent } from './mcp-tool-detail.component';

/** 遞迴收集 schema 所有屬性名稱（含巢狀 properties／items）。 */
const fieldNames = (schema: unknown): string[] => {
  if (!schema || typeof schema !== 'object') return [];
  const s = schema as { properties?: Record<string, unknown>; items?: unknown };
  return [
    ...Object.entries(s.properties ?? {}).flatMap(([k, v]) => [k, ...fieldNames(v)]),
    ...fieldNames(s.items),
  ];
};

const byName = (a: McpTool, b: McpTool) =>
  a.name.toLowerCase().localeCompare(b.name.toLowerCase());

@Component({
  selector: 'app-mcp-tools-page',
  standalone: true,
  imports: [McpToolDetailComponent],
  template: `
    <div class="p-4">
      <h1 class="text-2xl font-bold mb-4">MCP Tool Catalog</h1>
      @if (error(); as e) {
        <p class="error text-sm text-red-600">{{ e }}</p>
      } @else if (servers(); as list) {
        @if (list.length === 0) {
          <p class="empty text-sm text-slate-500">目前沒有任何 MCP server。</p>
        } @else {
          <div class="flex gap-4">
            <!-- 左側清單（#43 搜尋框加在這個區塊頂端） -->
            <nav class="list w-80 shrink-0 space-y-4">
              <input type="search" class="search w-full border rounded px-2 py-1" placeholder="搜尋工具、欄位…"
                [value]="query()" (input)="query.set($any($event.target).value)" />
              @if (query().trim() && visible().length === 0) {
                <p class="no-results text-sm text-slate-500">找不到符合「{{ query().trim() }}」的工具。</p>
              }
              @for (s of visible(); track s.id) {
                <section class="server">
                  <h2 class="server-title font-semibold">{{ s.id }}</h2>
                  @if (s.error) {
                    <p class="server-error text-sm text-red-600">
                      <span class="badge-error px-2 rounded bg-red-100">無法連線</span> {{ s.error }}
                    </p>
                  }
                  <ul>
                    @for (t of s.tools; track t.qualifiedName) {
                      <li>
                        <button type="button" class="tool w-full text-left px-2 py-1 rounded hover:bg-slate-100"
                          [class.bg-blue-100]="selected() === t" (click)="selected.set(t)">{{ t.name }}</button>
                      </li>
                    }
                  </ul>
                </section>
              }
            </nav>
            <!-- 右側詳情 -->
            <div class="detail flex-1"><app-mcp-tool-detail [tool]="selected()" /></div>
          </div>
        }
      } @else {
        <p class="loading text-sm text-slate-500">載入中…</p>
      }
    </div>
  `,
})
export class McpToolsPageComponent {
  private readonly svc = inject(McpToolsService);
  /** 已依工具名稱（不分大小寫）排序；server 維持後端給的設定檔順序。 */
  readonly servers = signal<McpServer[] | null>(null);
  readonly error = signal<string | null>(null);
  readonly query = signal('');
  /** 依搜尋字串過濾；無符合工具的 server 整組收起。 */
  readonly visible = computed(() => {
    const list = this.servers() ?? [];
    const q = this.query().trim().toLowerCase();
    if (!q) return list;
    const has = (x: string) => x.toLowerCase().includes(q);
    return list
      .map((s) => ({
        ...s,
        tools: has(s.id)
          ? s.tools
          : s.tools.filter((t) =>
              [t.name, t.description ?? '', ...fieldNames(t.inputSchema), ...fieldNames(t.outputSchema)].some(has)),
      }))
      .filter((s) => s.tools.length > 0);
  });
  readonly selected = signal<McpTool | null>(null);

  constructor() {
    this.svc.list().subscribe({
      next: (r) =>
        this.servers.set(r.servers.map((s) => ({ ...s, tools: [...s.tools].sort(byName) }))),
      error: () => this.error.set('無法載入 MCP 工具清單，請稍後再試。'),
    });
  }
}
