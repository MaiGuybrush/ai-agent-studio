import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from '../config/config.service';

/** GET /api/mcp-tools/servers/{id} 的工具項目。 */
export interface McpTool {
  name: string;
  /** `server__tool` */
  qualifiedName: string;
  description: string | null;
  inputSchema: unknown;
  outputSchema: unknown | null;
  annotations: Record<string, unknown> | null;
}

export interface McpServer {
  id: string;
  type: string;
  /** 該筆列舉時間（ISO）；快取命中時是原本的時間；連不上時為 null */
  fetchedAt: string | null;
  /** 連不上時有值 */
  error: string | null;
  tools: McpTool[];
}

@Injectable({ providedIn: 'root' })
export class McpToolsService {
  private readonly http = inject(HttpClient);
  private readonly config = inject(ConfigService);

  private get base() {
    return this.config.normalizeUrl(String(this.config.config()['apiBaseUrl'] ?? ''));
  }

  /** GET /api/mcp-tools/servers：只有 id、type，依設定檔順序。 */
  listServers(): Observable<{ servers: { id: string; type: string }[] }> {
    return this.http.get<{ servers: { id: string; type: string }[] }>(`${this.base}/api/mcp-tools/servers`);
  }

  /** GET /api/mcp-tools/servers/{id}：單一 server 的工具。 */
  getServer(id: string, refresh = false): Observable<McpServer> {
    return this.http.get<McpServer>(`${this.base}/api/mcp-tools/servers/${encodeURIComponent(id)}`, refresh ? { params: { refresh: true } } : {});
  }
}
