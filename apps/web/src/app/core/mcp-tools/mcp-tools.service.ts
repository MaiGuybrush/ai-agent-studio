import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from '../config/config.service';

/** GET /api/mcp-tools 的工具項目。後續欄位（例如 #44 的 fetchedAt 在 server 層）加在這裡即可。 */
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

  list(): Observable<McpToolsResponse> {
    const base = this.config.normalizeUrl(String(this.config.config()['apiBaseUrl'] ?? ''));
    return this.http.get<McpToolsResponse>(`${base}/api/mcp-tools`);
  }
}
