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
}

export interface Ticket {
  id: string;
  description: string;
  cimEngineerEmpNo: string;
  cimEngineerName: string;
  businessUserEmpNo: string;
  businessUserName: string;
  status: string;
  cancelReason: string | null;
  createdAt: string;
  permissions: TicketPermissions;
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
  create(body: CreateTicket): Promise<Ticket> {
    return firstValueFrom(this.http.post<Ticket>(this.url(), body));
  }
  cancel(id: string, reason: string): Promise<Ticket> {
    return firstValueFrom(this.http.post<Ticket>(this.url(`${this.id(id)}/cancel`), { reason }));
  }
  reopen(id: string): Promise<Ticket> {
    return firstValueFrom(this.http.post<Ticket>(this.url(`${this.id(id)}/reopen`), {}));
  }
  reassign(id: string, newCimEngineerEmpNo: string, reason: string): Promise<Ticket> {
    return firstValueFrom(
      this.http.post<Ticket>(this.url(`${this.id(id)}/reassign`), { newCimEngineerEmpNo, reason }),
    );
  }
}
