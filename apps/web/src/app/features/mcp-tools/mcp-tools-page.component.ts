import { Component, computed, effect, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { map } from 'rxjs';
import { McpServer, McpTool, McpToolsService } from '../../core/mcp-tools/mcp-tools.service';
import { McpToolDetailComponent } from './mcp-tool-detail.component';

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
              @for (s of list; track s.id) {
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
                          [class.bg-blue-100]="selected() === t" (click)="pick(s, t)">{{ t.name }}</button>
                      </li>
                    }
                  </ul>
                </section>
              }
            </nav>
            <!-- 右側詳情 -->
            <div class="detail flex-1">
              @if (notFound()) {
                <p class="not-found text-sm text-red-600">找不到指定的 MCP 工具。</p>
              } @else {
                <app-mcp-tool-detail [tool]="selected()" />
              }
            </div>
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
  readonly selected = signal<McpTool | null>(null);

  private readonly router = inject(Router);
  /** 深連結 `/mcp-tools/:server/:tool` 的參數；沒有則為 null。 */
  private readonly target = toSignal(
    inject(ActivatedRoute).paramMap.pipe(
      map((p) => (p.get('server') && p.get('tool') ? { server: p.get('server')!, tool: p.get('tool')! } : null)),
    ),
    { initialValue: null },
  );
  private readonly match = computed(() => {
    const t = this.target();
    const s = this.servers()?.find((x) => x.id === t?.server);
    return s?.tools.find((x) => x.name === t?.tool) ?? null;
  });
  readonly notFound = computed(() => this.target() !== null && this.servers() !== null && this.match() === null);

  pick(s: McpServer, t: McpTool) {
    this.selected.set(t);
    this.router.navigate(['/mcp-tools', s.id, t.name]).catch(() => {});
  }

  constructor() {
    effect(() => {
      const m = this.match();
      if (m) this.selected.set(m);
    });
    this.svc.list().subscribe({
      next: (r) =>
        this.servers.set(r.servers.map((s) => ({ ...s, tools: [...s.tools].sort(byName) }))),
      error: () => this.error.set('無法載入 MCP 工具清單，請稍後再試。'),
    });
  }
}
