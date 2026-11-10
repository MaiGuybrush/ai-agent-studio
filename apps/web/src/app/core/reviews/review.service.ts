import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { ConfigService } from '../config/config.service';

export type ReviewResult = 'Feasible' | 'Conditional' | 'Infeasible';

export interface PendingReview {
  ticketId: string;
  specId: string;
  revision: number;
  kind: string;
  purpose: string | null;
  workflow: unknown;
  cimEngineerName: string;
  businessUserName: string;
  waitingDays: number;
}

export interface ReviewHistory {
  revision: number;
  result: ReviewResult;
  reason: string | null;
  condition: string | null;
  estimatedDays: number | null;
  reviewer: string;
  at: string;
}

export interface ReviewDetail {
  ticketId: string;
  specId: string;
  revision: number;
  kind: string;
  state: string;
  /** 後端算好：是否還能回覆（CIM Engineer 處理前）。 */
  canReply: boolean;
  content: Record<string, unknown>;
  workflow: unknown;
  cimEngineerName: string;
  businessUserName: string;
  waitingDays: number;
  history: ReviewHistory[];
  /** 與同一份 Tool Spec 上一個 revision 的差異（後端算好）；第一版為 null。 */
  diff?: { fromRevision: number; changes: { path: string; before: unknown; after: unknown }[] } | null;
}

export interface ReviewReply {
  result: ReviewResult | '';
  reason: string;
  condition: string;
  estimatedDays: number | null;
}

@Injectable({ providedIn: 'root' })
export class ReviewService {
  private readonly http = inject(HttpClient);
  private readonly config = inject(ConfigService);

  private url(path = ''): string {
    const base = this.config.normalizeUrl(String(this.config.config()['apiBaseUrl'] ?? ''));
    return `${base}/api/reviews${path}`;
  }
  private spec(t: string, s: string): string {
    return `/${encodeURIComponent(t)}/${encodeURIComponent(s)}`;
  }

  pending(): Promise<PendingReview[]> {
    return firstValueFrom(this.http.get<PendingReview[]>(this.url('/pending')));
  }
  get(ticketId: string, specId: string): Promise<ReviewDetail> {
    return firstValueFrom(this.http.get<ReviewDetail>(this.url(this.spec(ticketId, specId))));
  }
  reply(ticketId: string, specId: string, body: ReviewReply): Promise<unknown> {
    return firstValueFrom(this.http.put(this.url(this.spec(ticketId, specId)), body));
  }
}
