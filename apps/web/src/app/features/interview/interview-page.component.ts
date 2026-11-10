import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Ticket, TicketService, errorMessage } from '../../core/tickets/ticket.service';
import { ChatMessage, InterviewService } from '../../core/interview/interview.service';
import { SseMessage } from '../../core/interview/sse';

// spec 8.2 的 grill 流程；#17 還沒有階段進度的資料來源，先一律停在第 1 階段（之後由後端提供）。
const STAGES = ['任務目標與觸發', '列出步驟', '對照現有工具', '補欄位與副作用', 'Naming Check／System Owner', '總結送出'];
const TABS = ['Workflow Definition', 'Tool Spec', '評估進度'];
const MAX_RECONNECT = 5;

@Component({
  selector: 'app-interview-page',
  standalone: true,
  imports: [RouterLink],
  styles: [`
    .grid3 { display: grid; grid-template-columns: 260px 1fr 460px; gap: 10px; padding: 10px; height: calc(100vh - 140px); }
    .grid3 > * { overflow: auto; min-height: 0; background: #fff; border: 1px solid #e5e7eb; border-radius: 6px; padding: 10px; }
    .col-mid { display: flex; flex-direction: column; }
    .msgs { flex: 1; overflow: auto; display: flex; flex-direction: column; gap: 8px; }
    .msg { max-width: 85%; padding: 6px 10px; border-radius: 8px; white-space: pre-wrap; }
    .msg.assistant { background: #f1f5f9; align-self: flex-start; }
    .msg.user { background: #dbeafe; align-self: flex-end; }
    .stage { font-size: 12px; padding: 2px 8px; border-radius: 4px; background: #e5e7eb; color: #6b7280; margin-bottom: 4px; }
    .stage.cur { background: #2563eb; color: #fff; }
    .tab { padding: 2px 10px; border: 1px solid #cbd5e1; border-radius: 4px; margin-right: 4px; }
  `],
  template: `
    <div class="grid3">
      <aside class="col-left">
        @if (ticket(); as t) {
          <a class="text-sm text-blue-700" [routerLink]="['/tickets', t.id]">← 需求單</a>
          <h3 class="font-semibold mt-1">{{ t.id }}</h3>
          <div><span class="status">{{ t.status }}</span></div>
          <div class="text-xs text-slate-500 my-2">CIM Engineer：{{ t.cimEngineerName }}<br />Business User：{{ t.businessUserName }}</div>
          <p class="text-sm whitespace-pre-wrap mb-3">{{ t.description }}</p>
        }
        <h4 class="text-xs text-slate-500">階段</h4>
        @for (s of stages; track s; let i = $index) {
          <div class="stage" [class.cur]="i === 0">{{ i + 1 }} {{ s }}</div>
        }
      </aside>

      <section class="col-mid">
        <div class="msgs">
          @for (m of messages(); track $index) {
            <div class="msg" [class.user]="m.role === 'user'" [class.assistant]="m.role === 'assistant'">{{ m.text }}</div>
          }
        </div>
        @if (error(); as e) {
          <p class="error text-sm text-red-600">{{ e }}
            @if (canChat() && !running()) {
              <button class="retry border rounded px-2 ml-2" (click)="retry()">重試</button>
            }
          </p>
        }
        @if (canChat()) {
          <form class="flex gap-2 pt-2 border-t" (submit)="submit($event)">
            <textarea name="message" rows="2" class="flex-1 border rounded p-1" [disabled]="running()"
              [value]="draft()" (input)="draft.set(val($event))"
              [placeholder]="running() ? '回覆中…' : '轉述 Business User 的說明…'"></textarea>
            @if (running()) {
              <button type="button" class="abort border rounded px-3" (click)="abort()">中止</button>
            } @else {
              <button type="submit" class="send px-3 rounded bg-blue-600 text-white" [disabled]="!draft().trim()">送出</button>
            }
          </form>
        } @else {
          <p class="readonly text-sm text-slate-500 pt-2 border-t">唯讀：只有負責的 CIM Engineer 能在此對話。</p>
        }
      </section>

      <aside class="col-right">
        @for (t of tabs; track t) {
          <span class="tab text-sm">{{ t }}</span>
        }
        <p class="text-sm text-slate-500 mt-4">草稿內容會在後續功能加入。</p>
      </aside>
    </div>
  `,
})
export class InterviewPageComponent {
  private readonly tickets = inject(TicketService);
  private readonly interview = inject(InterviewService);
  private readonly id = inject(ActivatedRoute).snapshot.paramMap.get('id')!;

  readonly stages = STAGES;
  readonly tabs = TABS;
  readonly ticket = signal<Ticket | null>(null);
  readonly messages = signal<ChatMessage[]>([]);
  readonly running = signal(false);
  readonly error = signal<string | null>(null);
  readonly draft = signal('');
  private lastEventId: string | null = null;

  constructor() {
    void this.init();
  }

  canChat(): boolean {
    return !!this.ticket()?.permissions.canChat;
  }
  val(e: Event): string {
    return (e.target as HTMLTextAreaElement).value;
  }

  private async init(): Promise<void> {
    try {
      const [t, c] = await Promise.all([this.tickets.get(this.id), this.interview.conversation(this.id)]);
      this.ticket.set(t);
      this.messages.set(c.messages);
      if (c.running) await this.consume(() => this.interview.reconnect(this.id, null));
    } catch (e) {
      this.error.set(errorMessage(e, '無法載入 Interview。'));
    }
  }

  async submit(e: Event): Promise<void> {
    e.preventDefault();
    const text = this.draft().trim();
    if (!text || this.running()) return;
    this.draft.set('');
    this.messages.update((m) => [...m, { role: 'user', text }]);
    await this.consume(() => this.interview.send(this.id, text));
  }

  async retry(): Promise<void> {
    await this.consume(() => this.interview.send(this.id, null));
  }

  async abort(): Promise<void> {
    try {
      await this.interview.cancel(this.id);
    } catch (e) {
      this.error.set(errorMessage(e));
    }
  }

  /** 讀一個 run 的串流；中途斷線就用 Last-Event-ID 重連。結束後以後端的對話為準（失敗回合的半截回覆不會留下）。 */
  private async consume(start: () => AsyncIterable<SseMessage>): Promise<void> {
    this.running.set(true);
    this.error.set(null);
    this.lastEventId = null;
    let reply = false;
    let ended = false;
    let source = start;
    for (let attempt = 0; !ended && attempt <= MAX_RECONNECT; attempt++) {
      try {
        for await (const m of source()) {
          if (m.id) this.lastEventId = m.id;
          if (m.type === 'text') {
            if (!reply) { this.messages.update((l) => [...l, { role: 'assistant', text: '' }]); reply = true; }
            this.messages.update((l) => [...l.slice(0, -1), { role: 'assistant', text: l[l.length - 1].text + m.data.text }]);
          } else if (m.type === 'error') {
            this.error.set(m.data.code === 'model_busy' ? '模型額度不足或忙碌，請稍後再試' : m.data.code === 'cancelled' ? '已中止' : m.data.message || '回覆失敗，請重試。');
          }
        }
        ended = true;
      } catch (e) {
        const status = (e as { status?: number }).status;
        if (status) { this.error.set(errorMessage({ error: { detail: (e as { detail?: string }).detail } }, `請求失敗（${status}）`)); ended = true; }
        else source = () => this.interview.reconnect(this.id, this.lastEventId); // 網路斷線
      }
    }
    if (!ended) this.error.set('與伺服器的連線中斷，請重新整理。');
    try {
      this.messages.set((await this.interview.conversation(this.id)).messages);
    } catch { /* 保留畫面現況 */ }
    this.running.set(false);
  }
}
