import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from '../config/config.service';

/** GET /api/mcp-tools 的工具項目。 */
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

export interface McpToolsResponse {
  servers: McpServer[];
}

@Injectable({ providedIn: 'root' })
export class McpToolsService {
  private readonly http = inject(HttpClient);
  private readonly config = inject(ConfigService);

  list(refresh = false): Observable<McpToolsResponse> {
    const base = this.config.normalizeUrl(String(this.config.config()['apiBaseUrl'] ?? ''));
    return this.http.get<McpToolsResponse>(`${base}/api/mcp-tools`, refresh ? { params: { refresh: true } } : {});
  }
}
