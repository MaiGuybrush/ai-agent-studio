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
  /** 重開 Interview 產生的對話才有（spec 8.6）；previous 是舊對話，可唯讀查閱。 */
  reopenReason?: string | null;
  previous?: { id: number; reopenReason: string | null; createdAt: string }[];
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
export interface NamingResult {
  status: 'pass' | 'pending' | 'rename' | 'new';
  suggestion?: string;
}

export interface ToolSpec {
  id: string;
  kind?: 'Reuse' | 'Extend' | 'New';
  purpose?: string;
  system?: string;
  system_owner?: string;
  /** 建議改名但不改時的理由，記在欄位上：欄位路徑 → 理由（送出條件，spec 8.5）。 */
  naming_reasons?: Record<string, string>;
  side_effect?: 'read' | 'write';
  inputs?: unknown;
  /** 後端算出：每個欄位（含巢狀）的 Naming Check 結果與詞彙 sha，欄位路徑如 inputs.lots[].state。 */
  naming_check?: { sha: string; fields: Record<string, NamingResult> };
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
  vocabulary?: { sha: string; fetchedAt: string; warning?: string | null } | null; // 目前使用的詞彙版本；warning 表示沿用舊版
  vocabularyError?: string | null; // 完全沒有詞彙時，Naming Check 暫停的原因
}

/** 阻擋送出的一項待處理事項（後端算好；前端只顯示）。 */
export interface Blocker {
  code: 'no_tool_specs' | 'missing_field' | 'owner' | 'new_term' | 'rename_reason' | 'naming_unavailable';
  message: string;
  specId?: string | null;
  path?: string | null;
}
export interface SubmissionState {
  blockers: Blocker[];
  pendingCount: number;
  stage: number; // 1..6，第 5、6 階段才展開完整阻擋清單
  canSubmit: boolean;
  suggestedOwners: { system: string; empNo: string; name: string | null }[]; // 系統對照表的建議
  toolSpecs: { specId: string; kind: string; state: string; revision: number; handled?: boolean; acceptedCondition?: string | null }[]; // 已送出的 Tool Spec 目前狀態
  submissions: { number: number }[];
  canHandle?: boolean; // 負責的 CIM Engineer 可處理評估結果（後端算好）
  allInfeasible?: boolean; // 全部 Tool Spec 不可行 → 導向 Cancellation
}

export interface ComplianceReport { passed: boolean; failures: string[]; warnings: string[] }

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

  oldConversation(ticketId: string, interviewId: number): Promise<{ messages: ChatMessage[] }> {
    return firstValueFrom(this.http.get<{ messages: ChatMessage[] }>(this.url(ticketId, `/${interviewId}`)));
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

  submissionState(ticketId: string): Promise<SubmissionState> {
    return firstValueFrom(this.http.get<SubmissionState>(this.draftUrl(ticketId, 'submission')));
  }

  /** 送出 Interview；有阻擋事項時後端回 409。 */
  submit(ticketId: string): Promise<{ number: number; status: string }> {
    return firstValueFrom(this.http.post<{ number: number; status: string }>(this.draftUrl(ticketId, 'submit'), {}));
  }

  /** 處理評估結果（#28）：accept-condition／implementing；修改後重評帶 fields；刪除用 DELETE。業務規則在後端。 */
  handleSpec(ticketId: string, specId: string, action: 'accept-condition' | 'implementing' | 'revise', fields?: Record<string, unknown>): Promise<unknown> {
    return firstValueFrom(this.http.post(this.draftUrl(ticketId, `specs/${encodeURIComponent(specId)}/${action}`), fields ? { fields } : {}));
  }

  /** 標為已實作（#29）：後端做 Interface Compliance Check；沒通過時 passed=false 並附 failures。 */
  markImplemented(ticketId: string, specId: string, serverKey: string, toolName: string): Promise<ComplianceReport> {
    return firstValueFrom(this.http.post<ComplianceReport>(this.draftUrl(ticketId, `specs/${encodeURIComponent(specId)}/implemented`), { serverKey, toolName }));
  }

  deleteSpec(ticketId: string, specId: string): Promise<unknown> {
    return firstValueFrom(this.http.delete(this.draftUrl(ticketId, `specs/${encodeURIComponent(specId)}`)));
  }

  /** 確認新名詞（只有負責的 CIM Engineer；說明必填）→ 成為 Pending Term。 */
  confirmTerm(ticketId: string, term: string, explanation: string): Promise<unknown> {
    return firstValueFrom(this.http.post(this.draftUrl(ticketId, 'pending-terms'), { term, explanation }));
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
