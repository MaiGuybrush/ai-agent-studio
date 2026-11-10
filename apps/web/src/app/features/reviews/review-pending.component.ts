import { Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { errorMessage } from '../../core/tickets/ticket.service';
import { PendingReview, ReviewService } from '../../core/reviews/review.service';

/** 「待我評估」清單（spec 9）：不顯示對話紀錄；不做提醒與逾期狀態，只顯示已等待天數。 */
@Component({
  selector: 'app-review-pending',
  standalone: true,
  imports: [RouterLink],
  template: `
    <div class="p-4">
      <h1 class="text-2xl font-bold mb-4">待我評估</h1>
      @if (error(); as e) {
        <p class="error text-sm text-red-600">{{ e }}</p>
      }
      <table class="w-full text-sm bg-white border border-slate-200 rounded">
        <thead>
          <tr class="text-left text-slate-500">
            <th class="p-2">Tool Spec Revision</th><th class="p-2">Workflow Definition</th><th class="p-2">需求單號</th>
            <th class="p-2">CIM Engineer</th><th class="p-2">Business User</th><th class="p-2">已等待</th>
          </tr>
        </thead>
        <tbody>
          @for (r of items(); track r.ticketId + r.specId) {
            <tr class="border-t border-slate-100">
              <td class="p-2"><a class="text-blue-700" [routerLink]="['/reviews', r.ticketId, r.specId]">{{ r.specId }} r{{ r.revision }}</a></td>
              <td class="p-2">{{ stepCount(r) }} 個步驟</td>
              <td class="p-2">{{ r.ticketId }}</td>
              <td class="p-2">{{ r.cimEngineerName }}</td>
              <td class="p-2">{{ r.businessUserName }}</td>
              <td class="p-2 waiting">{{ r.waitingDays }} 天</td>
            </tr>
          } @empty {
            <tr><td class="p-2 text-slate-500" colspan="6">目前沒有待評估的項目。</td></tr>
          }
        </tbody>
      </table>
    </div>
  `,
})
export class ReviewPendingComponent {
  private readonly svc = inject(ReviewService);
  readonly items = signal<PendingReview[]>([]);
  readonly error = signal<string | null>(null);

  constructor() {
    this.svc.pending().then(
      (l) => this.items.set(l),
      (e) => this.error.set(errorMessage(e, '無法載入待評估清單。')),
    );
  }

  stepCount(r: PendingReview): number {
    return Array.isArray(r.workflow) ? r.workflow.length : 0;
  }
}
