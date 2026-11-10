import { Component, inject, input, signal, OnInit } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ConfigService } from '../../core/config/config.service';
import { errorMessage } from '../../core/tickets/ticket.service';

export interface SpecComment {
  id: number;
  revision: number;
  authorEmpNo: string;
  authorName: string;
  body: string;
  at: string;
}

/**
 * Tool Spec 留言串與 System Owner 轉派／改派（spec 9、10.1），評估畫面與 Interview 的 Tool Spec 分頁共用。
 * 權限（誰能留言／轉派）由 C# 判斷，這裡只顯示後端回應的錯誤。
 */
@Component({
  selector: 'app-spec-comments',
  standalone: true,
  imports: [FormsModule],
  template: `
    <section class="spec-comments mt-2">
      <h3 class="font-bold text-sm">留言串</h3>
      <ul class="comments text-sm">
        @for (c of comments(); track c.id) {
          <li class="comment"><span class="text-xs text-slate-500">r{{ c.revision }}・{{ c.authorName }}・{{ c.at }}</span><br />{{ c.body }}</li>
        } @empty {
          <li class="text-xs text-slate-500">尚無留言。</li>
        }
      </ul>
      <form class="flex gap-1 mt-1" (ngSubmit)="send()">
        <input name="body" [(ngModel)]="body" class="comment-body border rounded p-1 flex-1" placeholder="提問或回覆（不影響狀態）" />
        <button type="submit" class="send-comment border rounded px-2" [disabled]="!body.trim()">留言</button>
      </form>
      <form class="flex gap-1 mt-1 text-xs" (ngSubmit)="reassign()">
        <input name="owner" [(ngModel)]="newOwner" class="new-owner border rounded p-1 w-28" placeholder="新 System Owner 工號" />
        <input name="reason" [(ngModel)]="reason" class="reassign-reason border rounded p-1 flex-1" placeholder="轉派／改派原因（必填）" />
        <button type="submit" class="reassign border rounded px-2" [disabled]="!newOwner.trim() || !reason.trim()">轉派／改派</button>
      </form>
      @if (error(); as e) { <p class="comment-error text-xs text-red-600">{{ e }}</p> }
      @if (notice(); as n) { <p class="comment-notice text-xs text-green-700">{{ n }}</p> }
    </section>
  `,
})
export class SpecCommentsComponent implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly config = inject(ConfigService);
  readonly ticketId = input.required<string>();
  readonly specId = input.required<string>();
  readonly comments = signal<SpecComment[]>([]);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);
  body = '';
  newOwner = '';
  reason = '';

  private url(path: string): string {
    const base = this.config.normalizeUrl(String(this.config.config()['apiBaseUrl'] ?? ''));
    return `${base}/api/tickets/${encodeURIComponent(this.ticketId())}/specs/${encodeURIComponent(this.specId())}/${path}`;
  }

  ngOnInit() {
    void this.load();
  }

  private async load() {
    try {
      this.comments.set(await firstValueFrom(this.http.get<SpecComment[]>(this.url('comments'))));
    } catch (e) {
      this.error.set(errorMessage(e, '無法載入留言。'));
    }
  }

  async send() {
    this.error.set(null);
    try {
      await firstValueFrom(this.http.post(this.url('comments'), { body: this.body }));
      this.body = '';
      await this.load();
    } catch (e) {
      this.error.set(errorMessage(e, '留言失敗。'));
    }
  }

  async reassign() {
    this.error.set(null);
    this.notice.set(null);
    try {
      await firstValueFrom(this.http.post(this.url('reassign-owner'), { newOwnerEmpNo: this.newOwner, reason: this.reason }));
      this.notice.set('已轉派，並通知新的 System Owner。');
      this.newOwner = this.reason = '';
    } catch (e) {
      this.error.set(errorMessage(e, '轉派失敗。'));
    }
  }
}
