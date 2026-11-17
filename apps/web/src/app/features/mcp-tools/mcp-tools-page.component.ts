import { Component, computed, inject, signal } from '@angular/core';
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
      <div class="flex items-center gap-2 mb-4">
        <h1 class="text-2xl font-bold">MCP Tool Catalog</h1>
        <button type="button" class="refresh px-2 py-1 text-sm border rounded" [disabled]="refreshing()" (click)="refresh()">重新整理</button>
        @if (refreshing()) { <span class="refreshing text-sm text-slate-500">更新中…</span> }
        @if (refreshError()) { <span class="refresh-error text-sm text-red-600">重新整理失敗，顯示的是舊資料。</span> }
      </div>
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
                  <h2 class="server-title font-semibold">{{ s.id }}
                    <span class="fetched-at text-xs font-normal text-slate-500">更新於 {{ time(s.fetchedAt) }}</span></h2>
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
  readonly selected = signal<McpTool | null>(null);

  readonly refreshing = signal(false);
  readonly refreshError = signal(false);

  constructor() {
    this.svc.list().subscribe({
      next: (r) => this.servers.set(this.sorted(r.servers)),
      error: () => this.error.set('無法載入 MCP 工具清單，請稍後再試。'),
    });
  }

  private sorted = (l: McpServer[]) => l.map((s) => ({ ...s, tools: [...s.tools].sort(byName) }));

  time(iso: string) {
    const d = new Date(iso);
    return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
  }

  refresh() {
    this.refreshing.set(true);
    this.refreshError.set(false);
    this.svc.list(true).subscribe({
      next: (r) => { this.servers.set(this.sorted(r.servers)); this.refreshing.set(false); },
      error: () => { this.refreshError.set(true); this.refreshing.set(false); },
    });
  }
}
