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

  private draftUrl(ticketId: string): string {
    const base = this.config.normalizeUrl(String(this.config.config()['apiBaseUrl'] ?? ''));
    return `${base}/api/tickets/${encodeURIComponent(ticketId)}/workflow-draft`;
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
