import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { JsonPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { errorMessage } from '../../core/tickets/ticket.service';
import { ReviewDetail, ReviewReply, ReviewService } from '../../core/reviews/review.service';

/**
 * 評估畫面（spec 9、12）：Tool Spec 與 Workflow Definition 唯讀，沒有對話區。
 * 欄位顯示只是輔助；規則由 C# 驗證，錯誤訊息原樣顯示。
 */
@Component({
  selector: 'app-review-page',
  standalone: true,
  imports: [FormsModule, JsonPipe],
  template: `
    <div class="p-4 max-w-4xl">
      @if (error(); as e) {
        <p class="error text-sm text-red-600">{{ e }}</p>
      }
      @if (detail(); as d) {
        <h1 class="text-2xl font-bold mb-1">評估 {{ d.specId }}（revision {{ d.revision }}）</h1>
        <p class="text-sm text-slate-500 mb-4">
          需求單 {{ d.ticketId }}・CIM Engineer {{ d.cimEngineerName }}・Business User {{ d.businessUserName }}・已等待 {{ d.waitingDays }} 天
        </p>
        <h2 class="font-bold">Workflow Definition（唯讀）</h2>
        <pre class="workflow text-xs bg-slate-50 p-2 mb-4 overflow-auto">{{ d.workflow | json }}</pre>
        <h2 class="font-bold">Tool Spec（唯讀）</h2>
        <pre class="content text-xs bg-slate-50 p-2 mb-4 overflow-auto">{{ d.content | json }}</pre>

        @if (d.history.length > 0) {
          <h2 class="font-bold">回覆紀錄</h2>
          <ul class="history text-sm mb-4">
            @for (h of d.history; track h.at) {
              <li>r{{ h.revision }}・{{ h.result }}・{{ h.at }}{{ h.reason ? '・' + h.reason : '' }}</li>
            }
          </ul>
        }

        @if (d.canReply) {
          <form class="reply grid gap-2" (ngSubmit)="submit()">
            <label>結果
              <select name="result" [(ngModel)]="form.result" class="result border rounded p-1">
                <option value="">請選擇</option>
                <option value="Feasible">可行</option>
                <option value="Conditional">附條件</option>
                <option value="Infeasible">不可行</option>
              </select>
            </label>
            <label>原因{{ form.result === 'Feasible' ? '（選填）' : '' }}
              <textarea name="reason" [(ngModel)]="form.reason" class="reason border rounded p-1 w-full"></textarea>
            </label>
            @if (form.result === 'Conditional') {
              <label>條件內容
                <textarea name="condition" [(ngModel)]="form.condition" class="condition border rounded p-1 w-full"></textarea>
              </label>
            }
            @if (form.result === 'Feasible' || form.result === 'Conditional') {
              <label>預估工時（人日）
                <input name="days" type="number" min="0" step="0.5" [(ngModel)]="form.estimatedDays" class="days border rounded p-1" />
              </label>
            }
            <button type="submit" class="send px-3 py-1 rounded bg-blue-600 text-white w-fit">{{ d.history.length ? '修改回覆' : '送出回覆' }}</button>
          </form>
        } @else {
          <p class="locked text-sm text-slate-500">CIM Engineer 已處理，無法再修改回覆。</p>
        }
      }
    </div>
  `,
})
export class ReviewPageComponent {
  private readonly svc = inject(ReviewService);
  private readonly router = inject(Router);
  private readonly ticketId: string;
  private readonly specId: string;
  readonly detail = signal<ReviewDetail | null>(null);
  readonly error = signal<string | null>(null);
  form: ReviewReply = { result: '', reason: '', condition: '', estimatedDays: null };

  constructor() {
    const p = inject(ActivatedRoute).snapshot.paramMap;
    this.ticketId = p.get('ticketId')!;
    this.specId = p.get('specId')!;
    this.svc.get(this.ticketId, this.specId).then(
      (d) => {
        this.detail.set(d);
        const last = d.history.at(-1); // 修改時以上一筆回覆為預設值
        if (last) this.form = { result: last.result, reason: last.reason ?? '', condition: last.condition ?? '', estimatedDays: last.estimatedDays };
      },
      (e) => this.error.set(errorMessage(e, '無法載入評估資料。')),
    );
  }

  submit() {
    this.error.set(null);
    this.svc.reply(this.ticketId, this.specId, this.form).then(
      () => this.router.navigate(['/reviews']),
      (e) => this.error.set(this.replyError(e)),
    );
  }

  private replyError(e: unknown): string {
    const errs = (e as { error?: { errors?: string[] } })?.error?.errors;
    return errs?.length ? errs.join(' ') : errorMessage(e, '回覆失敗，請稍後再試。');
  }
}
