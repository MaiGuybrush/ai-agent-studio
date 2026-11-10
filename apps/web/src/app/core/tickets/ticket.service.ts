import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { ConfigService } from '../config/config.service';

/** 後端算好的權限；前端只依此顯示按鈕，不自行判斷（業務規則只在 C#）。 */
export interface TicketPermissions {
  canCancel: boolean;
  canReopen: boolean;
  canReassign: boolean;
  canViewConversation: boolean;
  canChat: boolean;
  canSubmit: boolean;
  canReopenInterview: boolean; // 已送出（含已完成）且為負責的 CIM Engineer
}

export interface Ticket {
  id: string;
  description: string;
  cimEngineerEmpNo: string;
  cimEngineerName: string;
  businessUserEmpNo: string;
  businessUserName: string;
  status: string;
  statusCode: string;
  cancelReason: string | null;
  createdAt: string;
  permissions: TicketPermissions;
}

/** 唯讀檢視（spec 3.3、§12）：最新送出的 Workflow Definition、各 Tool Spec 凍結的 revision 與評估結果；沒有對話與草稿。 */
export interface ReadOnlyReview {
  revision: number;
  result: string;
  reason: string | null;
  condition: string | null;
  estimatedDays: number | null;
  reviewerEmpNo: string;
  at: string;
}
export interface ReadOnlyToolSpec {
  specId: string;
  kind: string;
  state: string;
  revision: number;
  acceptedCondition: string | null;
  content: Record<string, unknown>;
  reviews: ReadOnlyReview[];
  implemented: { serverKey: string; toolName: string } | null;
}
export interface ReadOnlyWorkflowStep {
  id: string;
  description?: string;
  tool_spec?: string;
  condition?: string;
  needs_approval?: boolean;
}
export interface ReadOnlyView {
  ticket: Ticket;
  workflow: ReadOnlyWorkflowStep[];
  toolSpecs: ReadOnlyToolSpec[];
}

export interface CreateTicket {
  id: string;
  description: string;
  businessUserEmpNo: string;
}

/** 後端 ProblemDetails 的 title／detail 轉成給使用者看的訊息。 */
export function errorMessage(e: unknown, fallback = '操作失敗，請稍後再試。'): string {
  const body = (e as { error?: { detail?: string; title?: string } })?.error;
  return body?.detail || body?.title || fallback;
}

@Injectable({ providedIn: 'root' })
export class TicketService {
  private readonly http = inject(HttpClient);
  private readonly config = inject(ConfigService);

  private url(path = ''): string {
    const base = this.config.normalizeUrl(String(this.config.config()['apiBaseUrl'] ?? ''));
    return `${base}/api/tickets${path}`;
  }
  private id(id: string): string {
    return `/${encodeURIComponent(id)}`;
  }

  list(): Promise<Ticket[]> {
    return firstValueFrom(this.http.get<Ticket[]>(this.url()));
  }
  get(id: string): Promise<Ticket> {
    return firstValueFrom(this.http.get<Ticket>(this.url(this.id(id))));
  }
  readOnly(id: string): Promise<ReadOnlyView> {
    return firstValueFrom(this.http.get<ReadOnlyView>(this.url(`${this.id(id)}/readonly`)));
  }
  markdown(id: string): Promise<string> {
    return firstValueFrom(this.http.get(this.url(`${this.id(id)}/markdown`), { responseType: 'text' }));
  }
  create(body: CreateTicket): Promise<Ticket> {
    return firstValueFrom(this.http.post<Ticket>(this.url(), body));
  }
  cancel(id: string, reason: string): Promise<Ticket> {
    return firstValueFrom(this.http.post<Ticket>(this.url(`${this.id(id)}/cancel`), { reason }));
  }
  reopen(id: string): Promise<Ticket> {
    return firstValueFrom(this.http.post<Ticket>(this.url(`${this.id(id)}/reopen`), {}));
  }
  /** 重開 Interview（須附原因），完成後回傳最新的需求單。 */
  async reopenInterview(id: string, reason: string): Promise<Ticket> {
    await firstValueFrom(this.http.post(this.url(`${this.id(id)}/interview/reopen`), { reason }));
    return this.get(id);
  }
  reassign(id: string, newCimEngineerEmpNo: string, reason: string): Promise<Ticket> {
    return firstValueFrom(
      this.http.post<Ticket>(this.url(`${this.id(id)}/reassign`), { newCimEngineerEmpNo, reason }),
    );
  }
}
