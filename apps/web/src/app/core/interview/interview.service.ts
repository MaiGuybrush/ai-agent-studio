import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { ConfigService } from '../config/config.service';
import { SseMessage, parseSse } from './sse';

export interface ChatMessage {
  role: 'user' | 'assistant';
  text: string;
}
export interface Conversation {
  running: boolean;
  messages: ChatMessage[];
}

export interface WfStep {
  id: string;
  description?: string;
  tool_spec?: string | null; // null／未填 = 人工步驟
  inputs?: unknown[];
  outputs?: string[];
  needs_approval?: boolean;
  condition?: string;
}
export interface WorkflowDraft {
  version: number;
  steps: WfStep[];
  humanEdits: string[]; // 人工修改過的欄位路徑，例如 steps.s1.description
}

/** Tool Spec 草稿（#21）：欄位見 spec 6.2；inputs／outputs 為 JSON Schema 子集。 */
export interface ToolSpec {
  id: string;
  kind?: 'Reuse' | 'Extend' | 'New';
  purpose?: string;
  system?: string;
  system_owner?: string;
  side_effect?: 'read' | 'write';
  inputs?: unknown;
  outputs?: unknown;
  ref?: { server: string; tool: string; schema_snapshot?: unknown };
  expected_tool_name?: string;
  accepted_condition?: string;
}
export interface ToolSpecDraft {
  version: number;
  steps: ToolSpec[]; // 後端與 Workflow 草稿共用 DraftView，欄位名稱沿用 steps
  humanEdits: string[]; // 例如 tool_specs.ts1.purpose
  missing: Record<string, string[]>; // 每份 Tool Spec 還缺的必填欄位
}

@Injectable({ providedIn: 'root' })
export class InterviewService {
  private readonly http = inject(HttpClient);
  private readonly config = inject(ConfigService);
  private readonly auth = inject(AuthService);

  private url(ticketId: string, path = ''): string {
    const base = this.config.normalizeUrl(String(this.config.config()['apiBaseUrl'] ?? ''));
    return `${base}/api/tickets/${encodeURIComponent(ticketId)}/interview${path}`;
  }

  conversation(ticketId: string): Promise<Conversation> {
    return firstValueFrom(this.http.get<Conversation>(this.url(ticketId)));
  }

  workflowDraft(ticketId: string): Promise<WorkflowDraft> {
    return firstValueFrom(this.http.get<WorkflowDraft>(this.draftUrl(ticketId)));
  }

  /** 存檔帶版本號；版本過期時後端回 409。 */
  saveWorkflowDraft(ticketId: string, version: number, steps: WfStep[]): Promise<WorkflowDraft> {
    return firstValueFrom(this.http.put<WorkflowDraft>(this.draftUrl(ticketId), { version, steps }));
  }

  toolSpecDraft(ticketId: string): Promise<ToolSpecDraft> {
    return firstValueFrom(this.http.get<ToolSpecDraft>(this.draftUrl(ticketId, 'tool-spec-draft')));
  }

  saveToolSpecDraft(ticketId: string, version: number, steps: ToolSpec[]): Promise<ToolSpecDraft> {
    return firstValueFrom(this.http.put<ToolSpecDraft>(this.draftUrl(ticketId, 'tool-spec-draft'), { version, steps }));
  }

  private draftUrl(ticketId: string, doc = 'workflow-draft'): string {
    const base = this.config.normalizeUrl(String(this.config.config()['apiBaseUrl'] ?? ''));
    return `${base}/api/tickets/${encodeURIComponent(ticketId)}/${doc}`;
  }

  cancel(ticketId: string): Promise<unknown> {
    return firstValueFrom(this.http.post(this.url(ticketId, '/cancel'), {}));
  }

  /** 送訊息並串流本回合回覆；text 為 null = 重試上一則沒得到回覆的訊息。 */
  send(ticketId: string, text: string | null): AsyncIterable<SseMessage> {
    return this.stream(this.url(ticketId, '/messages'), { method: 'POST', body: JSON.stringify({ text }) });
  }

  /** 斷線後從 lastEventId 之後繼續接同一個 run。 */
  reconnect(ticketId: string, lastEventId: string | null): AsyncIterable<SseMessage> {
    return this.stream(this.url(ticketId, '/events'), {
      headers: lastEventId ? { 'Last-Event-ID': lastEventId } : {},
    });
  }

  private async *stream(url: string, init: RequestInit): AsyncGenerator<SseMessage> {
    const res = await fetch(url, {
      ...init,
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${this.auth.token}`,
        ...(init.headers as Record<string, string> | undefined),
      },
    });
    if (!res.ok) {
      let detail = '';
      try {
        const p = await res.json();
        detail = p.detail || p.title || '';
      } catch {
        /* 沒有 body */
      }
      throw Object.assign(new Error(detail || `HTTP ${res.status}`), { status: res.status, detail });
    }
    yield* parseSse(res.body!);
  }
}
