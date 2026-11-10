import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Ticket, TicketService, errorMessage } from '../../core/tickets/ticket.service';

type Mode = 'cancel' | 'reassign' | null;

@Component({
  selector: 'app-ticket-detail',
  standalone: true,
  imports: [RouterLink],
  template: `
    <div class="p-4 max-w-3xl">
      <a class="text-sm text-blue-700" routerLink="/tickets">← 需求單清單</a>
      @if (ticket(); as t) {
        <h1 class="text-2xl font-bold mt-2">{{ t.id }} <span class="status text-base font-normal">{{ t.status }}</span></h1>
        <p class="whitespace-pre-wrap my-3">{{ t.description }}</p>
        <p class="text-sm text-slate-600">CIM Engineer：{{ t.cimEngineerName }}（{{ t.cimEngineerEmpNo }}）</p>
        <p class="text-sm text-slate-600">Business User：{{ t.businessUserName }}（{{ t.businessUserEmpNo }}）</p>
        @if (t.cancelReason) {
          <p class="text-sm text-red-700 mt-1">取消原因：{{ t.cancelReason }}</p>
        }
        <div class="flex gap-2 mt-4">
          @if (t.statusCode === 'Completed') {
            <button class="copy-md border rounded px-3 py-1" (click)="copyMarkdown()">{{ copied() ? '已複製' : '複製為 Markdown' }}</button>
          }
          @if (t.permissions.canViewConversation) {
            <a class="interview px-3 py-1 rounded bg-blue-600 text-white" [routerLink]="['/tickets', t.id, 'interview']">開啟 Interview</a>
          }
          @if (t.permissions.canCancel) {
            <button class="action border rounded px-3 py-1" data-action="cancel" (click)="open('cancel')">取消需求單</button>
          }
          @if (t.permissions.canReopen) {
            <button class="action border rounded px-3 py-1" data-action="reopen" (click)="reopen()">重開</button>
          }
          @if (t.permissions.canReassign) {
            <button class="action border rounded px-3 py-1" data-action="reassign" (click)="open('reassign')">改派</button>
          }
        </div>
        @if (mode(); as m) {
          <div class="mt-3 flex flex-col gap-2 max-w-md">
            @if (m === 'reassign') {
              <input name="newCimEngineerEmpNo" placeholder="新負責人工號" class="border rounded p-1" [value]="empNo()" (input)="empNo.set(val($event))" />
            }
            <textarea name="reason" rows="2" placeholder="原因（必填）" class="border rounded p-1" [value]="reason()" (input)="reason.set(val($event))"></textarea>
            <div>
              <button class="confirm px-3 py-1 rounded bg-blue-600 text-white" [disabled]="!canConfirm()" (click)="confirm()">確定</button>
              <button class="ml-2 border rounded px-3 py-1" (click)="mode.set(null)">放棄</button>
            </div>
          </div>
        }
      }
      @if (error(); as e) {
        <p class="error text-sm text-red-600 mt-2">{{ e }}</p>
      }
    </div>
  `,
})
export class TicketDetailComponent {
  private readonly svc = inject(TicketService);
  private readonly id = inject(ActivatedRoute).snapshot.paramMap.get('id')!;
  readonly ticket = signal<Ticket | null>(null);
  readonly error = signal<string | null>(null);
  readonly copied = signal(false);
  readonly mode = signal<Mode>(null);
  readonly reason = signal('');
  readonly empNo = signal('');

  constructor() {
    this.run(() => this.svc.get(this.id));
  }

  val(e: Event): string {
    return (e.target as HTMLInputElement).value;
  }
  open(m: Mode): void {
    this.mode.set(m);
    this.reason.set('');
    this.empNo.set('');
  }
  canConfirm(): boolean {
    return !!this.reason().trim() && (this.mode() !== 'reassign' || !!this.empNo().trim());
  }
  confirm(): void {
    const reason = this.reason().trim();
    if (this.mode() === 'cancel') this.run(() => this.svc.cancel(this.id, reason));
    else this.run(() => this.svc.reassign(this.id, this.empNo().trim(), reason));
  }
  /** Markdown 由後端產生，這裡只負責寫入剪貼簿（v1 不提供下載）。 */
  async copyMarkdown(): Promise<void> {
    this.error.set(null);
    try {
      await navigator.clipboard.writeText(await this.svc.markdown(this.id));
      this.copied.set(true);
    } catch (e) {
      this.error.set(errorMessage(e, '複製失敗，請稍後再試。'));
    }
  }
  reopen(): void {
    this.run(() => this.svc.reopen(this.id));
  }

  /** 後端回傳最新的需求單（含權限），畫面直接以它為準。 */
  private async run(call: () => Promise<Ticket>): Promise<void> {
    this.error.set(null);
    try {
      this.ticket.set(await call());
      this.mode.set(null);
    } catch (e) {
      this.error.set(errorMessage(e));
    }
  }
}
