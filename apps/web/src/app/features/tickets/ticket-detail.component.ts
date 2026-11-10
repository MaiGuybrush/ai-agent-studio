import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ReadOnlyView, Ticket, TicketService, errorMessage } from '../../core/tickets/ticket.service';

type Mode = 'cancel' | 'reassign' | 'reopenInterview' | null;

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
          @if (t.permissions.canReopenInterview) {
            <button class="action border rounded px-3 py-1" data-action="reopen-interview" (click)="open('reopenInterview')">重開 Interview</button>
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
        @if (view(); as v) {
          <section class="mt-6 readonly-workflow">
            <h2 class="text-lg font-bold">Workflow Definition</h2>
            @if (v.workflow.length === 0) {
              <p class="text-sm text-slate-500">尚未送出。</p>
            }
            <ol class="list-decimal ml-5 text-sm">
              @for (s of v.workflow; track s.id) {
                <li class="step my-1">
                  {{ s.description }}
                  @if (s.tool_spec) { <span class="text-slate-600">（Tool Spec：{{ s.tool_spec }}）</span> }
                  @if (s.condition) { <span class="text-slate-600">條件：{{ s.condition }}</span> }
                  @if (s.needs_approval) { <span class="text-amber-700">需人工核准</span> }
                </li>
              }
            </ol>
          </section>
          <section class="mt-6 readonly-specs">
            <h2 class="text-lg font-bold">Tool Spec</h2>
            @if (v.toolSpecs.length === 0) {
              <p class="text-sm text-slate-500">尚未送出。</p>
            }
            @for (s of v.toolSpecs; track s.specId) {
              <div class="spec border rounded p-3 my-2 text-sm">
                <div class="font-bold">{{ s.specId }} <span class="font-normal">{{ s.kind }}・{{ stateLabel(s.state) }}・revision {{ s.revision }}</span></div>
                <p>用途：{{ s.content['purpose'] }}</p>
                <p>系統：{{ s.content['system'] }}　副作用：{{ s.content['side_effect'] }}</p>
                @if (s.content['system_owner_name']) { <p>System Owner：{{ s.content['system_owner_name'] }}（{{ s.content['system_owner'] }}）</p> }
                @if (s.implemented; as i) { <p>實作：MCP server {{ i.serverKey }}，工具 {{ i.toolName }}</p> }
                @if (s.acceptedCondition) { <p>已接受的條件：{{ s.acceptedCondition }}</p> }
                <details>
                  <summary>輸入／輸出欄位</summary>
                  <pre class="whitespace-pre-wrap">{{ pretty({ inputs: s.content['inputs'], outputs: s.content['outputs'] }) }}</pre>
                </details>
                @for (r of s.reviews; track $index) {
                  <p class="review text-slate-700">評估（revision {{ r.revision }}）：{{ r.result }}@if (r.estimatedDays) {・{{ r.estimatedDays }} 人日}@if (r.reason) {・{{ r.reason }}}@if (r.condition) {・條件：{{ r.condition }}}</p>
                }
              </div>
            }
          </section>
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
  readonly view = signal<ReadOnlyView | null>(null);
  readonly error = signal<string | null>(null);
  readonly copied = signal(false);
  readonly mode = signal<Mode>(null);
  readonly reason = signal('');
  readonly empNo = signal('');

  constructor() {
    this.run(() => this.svc.get(this.id));
    void this.loadReadOnly();
  }

  private static readonly stateLabels: Record<string, string> = {
    Pending: '待評估', Feasible: '可行', Infeasible: '不可行', Conditional: '附條件',
    ConditionAccepted: '已接受條件', Implementing: '實作中', Implemented: '已實作',
  };
  stateLabel(s: string): string {
    return TicketDetailComponent.stateLabels[s] ?? s;
  }
  pretty(o: unknown): string {
    return JSON.stringify(o, null, 2);
  }
  /** 唯讀區塊（所有登入者都看得到）；失敗不影響上面的操作區。 */
  private async loadReadOnly(): Promise<void> {
    try {
      this.view.set(await this.svc.readOnly(this.id));
    } catch {
      this.view.set(null);
    }
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
    else if (this.mode() === 'reopenInterview') this.run(() => this.svc.reopenInterview(this.id, reason));
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
      void this.loadReadOnly();
    } catch (e) {
      this.error.set(errorMessage(e));
    }
  }
}
