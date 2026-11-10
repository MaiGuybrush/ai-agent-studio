import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Ticket, TicketService, errorMessage } from '../../core/tickets/ticket.service';
import { ChatMessage, ComplianceReport, InterviewService, SubmissionState, ToolSpec, ToolSpecDraft, WfStep, WorkflowDraft } from '../../core/interview/interview.service';
import { SpecCommentsComponent } from '../reviews/spec-comments.component';
import { SseMessage } from '../../core/interview/sse';

// spec 8.2 的 grill 流程；#17 還沒有階段進度的資料來源，先一律停在第 1 階段（之後由後端提供）。
const STAGES = ['任務目標與觸發', '列出步驟', '對照現有工具', '補欄位與副作用', 'Naming Check／System Owner', '總結送出'];
const TABS = ['Workflow Definition', 'Tool Spec', '評估進度'];
// Tool Spec 狀態的顯示名稱（spec 5.1）；狀態本身由後端決定。
const STATE_LABELS: Record<string, string> = {
  Pending: '待評估', Feasible: '可行', Infeasible: '不可行', Conditional: '附條件', ConditionAccepted: '已接受條件', Implementing: '實作中', Implemented: '已實作',
};
const MAX_RECONNECT = 5;

@Component({
  selector: 'app-interview-page',
  standalone: true,
  imports: [RouterLink, SpecCommentsComponent],
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
    .human { background: #fef3c7; }
    .badge { font-size: 11px; color: #b45309; margin-left: 4px; }
    .step { border: 1px solid #e5e7eb; border-radius: 6px; padding: 6px; margin-top: 6px; }
    .stage.done { background: #d1fae5; color: #065f46; }
    .pending-count { font-size: 13px; font-weight: 600; color: #b45309; }
    .blocker-list { font-size: 12px; color: #b91c1c; margin: 4px 0 4px 16px; list-style: disc; }
    .tab { cursor: pointer; padding: 2px 10px; border: 1px solid #cbd5e1; border-radius: 4px; margin-right: 4px; }
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
          <div class="stage" [class.cur]="i + 1 === stage()" [class.done]="i + 1 < stage()">{{ i + 1 }} {{ s }}</div>
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
        @if (canChat() && sub(); as sb) {
          <div class="pt-2 border-t">
            <span class="pending-count">{{ sb.pendingCount ? '送出前待處理 ' + sb.pendingCount + ' 項' : '可以送出' }}</span>
            @if (stage() >= 5 && sb.blockers.length) {
              <ul class="blocker-list">@for (b of sb.blockers; track $index) { <li>{{ b.message }}</li> }</ul>
            }
            @if (stage() === 6 && canSubmitTicket()) {
              <button class="submit-interview border rounded px-3 ml-2 bg-green-600 text-white" [disabled]="!sb.canSubmit || submitting()" (click)="submitInterview()">送出 Interview</button>
            }
            @if (submitError(); as se2) { <p class="submit-error text-sm text-red-600">{{ se2 }}</p> }
          </div>
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
          <p class="readonly text-sm text-slate-500 pt-2 border-t">唯讀：只有負責的 CIM Engineer 能在訪談中對話；Interview 送出後輸入框停用。</p>
        }
      </section>

      <aside class="col-right">
        @for (t of tabs; track $index) {
          <span class="tab text-sm" [class.font-semibold]="$index === tab()" (click)="tab.set($index)">{{ t }}</span>
        }
        @if (tab() === 0) {
          <div class="wf mt-2">
            @for (st of steps(); track st.id; let i = $index) {
              <div class="step">
                <div class="text-xs text-slate-500">{{ i + 1 }}. {{ st.id }}
                  @if (canChat()) { <button class="del text-red-600 ml-2" (click)="removeStep(st.id)">刪除</button> }
                </div>
                <label class="block text-xs">說明@if (human(st.id, 'description')) { <span class="badge">人工修改</span> }
                  <textarea class="f-description w-full border rounded p-1" [class.human]="human(st.id, 'description')" [disabled]="!canChat()"
                    [value]="st.description ?? ''" (input)="edit(st.id, 'description', val($event))"></textarea></label>
                <label class="block text-xs">Tool Spec（空白 = 人工步驟）@if (human(st.id, 'tool_spec')) { <span class="badge">人工修改</span> }
                  <input class="f-tool_spec w-full border rounded p-1" [class.human]="human(st.id, 'tool_spec')" [disabled]="!canChat()"
                    [value]="st.tool_spec ?? ''" (input)="edit(st.id, 'tool_spec', val($event) || null)" /></label>
                <label class="block text-xs">條件@if (human(st.id, 'condition')) { <span class="badge">人工修改</span> }
                  <input class="f-condition w-full border rounded p-1" [class.human]="human(st.id, 'condition')" [disabled]="!canChat()"
                    [value]="st.condition ?? ''" (input)="edit(st.id, 'condition', val($event))" /></label>
                <label class="text-xs"><input type="checkbox" class="f-needs_approval" [checked]="!!st.needs_approval" [disabled]="!canChat()"
                  (change)="edit(st.id, 'needs_approval', checked($event))" /> 需要人工核准@if (human(st.id, 'needs_approval')) { <span class="badge">人工修改</span> }</label>
                <div class="text-xs text-slate-500">輸入：{{ json(st.inputs) }}　輸出：{{ (st.outputs ?? []).join(', ') }}</div>
              </div>
            } @empty {
              <p class="text-sm text-slate-500">尚無步驟；agent 會在訪談中逐步建立。</p>
            }
            @if (draftError(); as de) { <p class="draft-error text-sm text-red-600">{{ de }}</p> }
            @if (canChat()) {
              <div class="mt-2">
                <button class="add border rounded px-2 mr-2" (click)="addStep()">新增步驟</button>
                <button class="save border rounded px-2 bg-blue-600 text-white" [disabled]="!dirty()" (click)="saveDraft()">儲存草稿</button>
              </div>
            }
          </div>
        } @else if (tab() === 1) {
          <div class="specs mt-2">
            @for (sp of specs(); track sp.id) {
              <div class="step spec">
                <div class="text-xs text-slate-500">{{ sp.id }}　<b>{{ sp.kind ?? '（未定）' }}</b>　{{ sp.system ?? '' }}　{{ sp.side_effect ?? '' }}
                  @if (sp.ref) { 　參照 {{ sp.ref.server }}__{{ sp.ref.tool }} }
                  @if (canChat()) { <button class="del-spec text-red-600 ml-2" (click)="removeSpec(sp.id)">刪除</button> }
                </div>
                @if (missingOf(sp.id).length) { <div class="missing text-xs text-red-600">缺：{{ missingOf(sp.id).join('、') }}</div> }
                @if (sp.kind !== 'Reuse') {
                  <label class="block text-xs">System Owner@if (humanSpec(sp.id, 'system_owner')) { <span class="badge">人工修改</span> }
                    <select class="owner-select w-full border rounded p-1" [disabled]="!canChat()" (change)="editSpec(sp.id, 'system_owner', val($event))">
                      <option value="" [selected]="!sp.system_owner">（未指定）</option>
                      @for (o of ownerOptions(sp); track o.empNo) {
                        <option [value]="o.empNo" [selected]="o.empNo === sp.system_owner">{{ o.label }}</option>
                      }
                    </select></label>
                }
                @for (f of specFields; track f.key) {
                  <label class="block text-xs">{{ f.label }}@if (humanSpec(sp.id, f.key)) { <span class="badge">人工修改</span> }
                    <textarea class="w-full border rounded p-1" [class]="'f-' + f.key" [class.human]="humanSpec(sp.id, f.key)" [disabled]="!canChat()"
                      [value]="specText(sp, f.key)" (input)="editSpec(sp.id, f.key, val($event))"></textarea></label>
                }
                @for (n of namingRows(sp); track n.path) {
                  <div class="naming-row text-xs" [class]="'naming-' + n.status">
                    <code>{{ n.path }}</code>
                    @switch (n.status) {
                      @case ('pass') { <span class="text-green-700">通過</span> }
                      @case ('rename') {
                        <span class="text-amber-700">建議改名 → <b>{{ n.suggestion }}</b></span>
                        @if (canChat()) { <button class="apply-rename border rounded px-1 ml-1" (click)="applyRename(sp.id, n.path, n.suggestion!)">改成標準名</button> }
                      }
                      @case ('pending') { <span class="text-slate-600">Pending Term（已提案，視為已知）</span> }
                      @default {
                        <span class="text-blue-700">新名詞（待 CIM Engineer 確認）</span>
                        @if (canChat()) {
                          <input class="term-explanation border rounded px-1 ml-1" placeholder="說明（必填）" [value]="explained(n.path)" (input)="explain(n.path, val($event))" />
                          <button class="confirm-term border rounded px-1 ml-1" [disabled]="!explained(n.path).trim()" (click)="confirmTerm(n.path)">確認新名詞</button>
                        }
                        @if (confirmError(); as ce) { <span class="text-red-600">{{ ce }}</span> }
                      }
                    }
                    @if (n.status === 'rename' && canChat()) {
                      <input class="rename-reason border rounded px-1 ml-1" placeholder="不改名的理由" [value]="sp.naming_reasons?.[n.path] ?? ''" (input)="setReason(sp.id, n.path, val($event))" />
                    }
                  </div>
                }
                <div class="text-xs text-slate-500">輸入：{{ json(sp.inputs) }}　輸出：{{ json(sp.outputs) }}</div>
                @if (isSubmitted(sp.id)) { <app-spec-comments [ticketId]="id" [specId]="sp.id" /> }
              </div>
            } @empty {
              <p class="text-sm text-slate-500">尚無 Tool Spec；agent 會在訪談中逐份建立。</p>
            }
            @if (vocab(); as v) {
              <p class="vocab-info text-xs text-slate-500">詞彙版本 {{ v.sha.slice(0, 7) }}（{{ v.fetchedAt }}）</p>
              @if (v.warning) { <p class="vocab-warning text-xs text-amber-700">{{ v.warning }}</p> }
            }
            @if (vocabError(); as ve) { <p class="vocab-error text-sm text-red-600">{{ ve }}</p> }
            @if (specError(); as se) { <p class="spec-error text-sm text-red-600">{{ se }}</p> }
            @if (canChat()) {
              <button class="save-specs border rounded px-2 mt-2 bg-blue-600 text-white" [disabled]="!specsDirty()" (click)="saveSpecs()">儲存 Tool Spec</button>
            }
          </div>
        } @else {
          <div class="progress mt-2">
            @for (r of sub()?.toolSpecs ?? []; track r.specId) {
              <div class="progress-row step text-sm">{{ r.specId }}　<b>{{ r.kind }}</b>　rev {{ r.revision }}　<span>{{ stateLabel(r.state) }}</span>
                @if (r.acceptedCondition) { <div class="text-xs text-slate-600">已接受的條件：{{ r.acceptedCondition }}</div> }
                @if (sub()?.canHandle) {
                  <div class="actions flex gap-1 mt-1">
                    @if (r.state === 'Conditional') { <button class="accept-condition border rounded px-1" (click)="act(r.specId, 'accept-condition')">接受條件</button> }
                    @if (r.state === 'Conditional' || r.state === 'Infeasible') { <button class="revise-open border rounded px-1" (click)="openRevise(r.specId)">修改後重評</button> }
                    @if (r.state === 'Infeasible') { <button class="delete-spec border rounded px-1 text-red-600" (click)="removeSpecResult(r.specId)">刪除</button> }
                    @if (r.state === 'Feasible' || r.state === 'ConditionAccepted') { <button class="mark-implementing border rounded px-1" (click)="act(r.specId, 'implementing')">標為實作中</button> }
                  </div>
                  @if (r.state === 'Implementing') { <button class="implemented-open border rounded px-1" (click)="openImpl(r.specId)">標為已實作</button> }
                  @if (implId() === r.specId) {
                    <div class="flex gap-1 mt-1">
                      <input class="impl-server border rounded p-1" placeholder="MCP server key" [value]="implServer()" (input)="implServer.set(val($event))" />
                      <input class="impl-tool border rounded p-1" placeholder="工具名稱" [value]="implTool()" (input)="implTool.set(val($event))" />
                      <button class="implemented-send border rounded px-2 bg-blue-600 text-white" [disabled]="!implServer().trim() || !implTool().trim()" (click)="sendImpl(r.specId)">檢查並標為已實作</button>
                    </div>
                  }
                  @if (compliance(); as c) {
                    @if (c.specId === r.specId) {
                      @if (c.report.failures.length) { <ul class="compliance-failures text-xs text-red-600 list-disc ml-4">@for (m of c.report.failures; track m) { <li>{{ m }}</li> }</ul> }
                      @if (c.report.warnings.length) { <ul class="compliance-warnings text-xs text-amber-700 list-disc ml-4">@for (m of c.report.warnings; track m) { <li>{{ m }}</li> }</ul> }
                    }
                  }
                  @if (reviseId() === r.specId) {
                    <textarea class="revise-purpose w-full border rounded p-1 mt-1" placeholder="修改後的用途說明" [value]="revisePurpose()" (input)="revisePurpose.set(val($event))"></textarea>
                    <button class="revise-send border rounded px-2 bg-blue-600 text-white" [disabled]="!revisePurpose().trim()" (click)="sendRevise(r.specId)">送去重評</button>
                  }
                }
              </div>
            } @empty {
              <p class="text-sm text-slate-500">尚未送出；送出後在此追蹤各 Tool Spec 的評估與實作進度。</p>
            }
            @if (sub()?.canHandle && sub()?.allInfeasible) {
              <p class="cancel-hint text-sm text-amber-700 mt-2">所有 Tool Spec 都不可行，請至<a class="underline" [routerLink]="['/tickets', ticket()?.id]">需求單頁面</a>執行 Cancellation（取消需求單）。</p>
            }
            @if (actionError(); as ae) { <p class="action-error text-sm text-red-600">{{ ae }}</p> }
          </div>
        }
      </aside>
    </div>
  `,
})
export class InterviewPageComponent {
  private readonly tickets = inject(TicketService);
  private readonly interview = inject(InterviewService);
  readonly id = inject(ActivatedRoute).snapshot.paramMap.get('id')!;

  /** 已送出（有 tool_spec 紀錄）且非 Reuse 的 Tool Spec 才有留言串。 */
  isSubmitted(specId: string): boolean {
    return !!this.sub()?.toolSpecs.some((r) => r.specId === specId && r.kind !== 'Reuse');
  }

  readonly stages = STAGES;
  readonly tabs = TABS;
  readonly ticket = signal<Ticket | null>(null);
  readonly messages = signal<ChatMessage[]>([]);
  readonly running = signal(false);
  readonly error = signal<string | null>(null);
  readonly draft = signal('');
  readonly tab = signal(0);
  readonly wf = signal<WorkflowDraft | null>(null);
  readonly steps = signal<WfStep[]>([]);
  readonly dirty = signal(false);
  readonly draftError = signal<string | null>(null);
  readonly specs = signal<ToolSpec[]>([]);
  readonly specsDirty = signal(false);
  readonly specError = signal<string | null>(null);
  readonly vocab = signal<ToolSpecDraft['vocabulary']>(null);
  readonly vocabError = signal<string | null>(null);
  readonly sub = signal<SubmissionState | null>(null);
  readonly submitting = signal(false);
  readonly submitError = signal<string | null>(null);
  readonly confirmError = signal<string | null>(null);
  readonly explanations = signal<Record<string, string>>({});
  private specDraft: ToolSpecDraft | null = null;
  /** 畫面上可直接編輯的文字欄位；inputs／outputs 等結構化欄位目前只顯示，修改請對 agent 說。 */
  readonly specFields = [
    { key: 'purpose', label: '用途說明' },
    { key: 'expected_tool_name', label: '預期工具名稱' }, { key: 'accepted_condition', label: '已接受的條件' },
  ] as const;
  private lastEventId: string | null = null;

  constructor() {
    void this.init();
  }

  /** 目前階段（後端算出）；送出後固定在最後一階段。 */
  stage(): number {
    return this.sub()?.stage ?? 1;
  }
  stateLabel(state: string): string {
    return STATE_LABELS[state] ?? state;
  }
  readonly reviseId = signal<string | null>(null);
  readonly revisePurpose = signal('');
  readonly actionError = signal<string | null>(null);
  readonly implId = signal<string | null>(null);
  readonly implServer = signal('');
  readonly implTool = signal('');
  readonly compliance = signal<{ specId: string; report: ComplianceReport } | null>(null);
  openImpl(specId: string): void {
    this.implId.set(this.implId() === specId ? null : specId);
    this.compliance.set(null);
  }
  /** 後端比對實際 MCP 工具介面；沒通過時顯示差異報告，狀態不變。 */
  sendImpl(specId: string): Promise<void> {
    return this.handle(async () => {
      const report = await this.interview.markImplemented(this.id, specId, this.implServer().trim(), this.implTool().trim());
      this.compliance.set({ specId, report });
      if (report.passed) this.implId.set(null);
    });
  }
  openRevise(specId: string): void {
    this.reviseId.set(this.reviseId() === specId ? null : specId);
    this.revisePurpose.set('');
  }
  /** 處理評估結果（業務規則在後端；失敗訊息直接顯示）。 */
  private async handle(run: () => Promise<unknown>): Promise<void> {
    this.actionError.set(null);
    try {
      await run();
      this.reviseId.set(null);
      this.ticket.set(await this.tickets.get(this.id));
    } catch (e) {
      this.actionError.set(errorMessage(e, '處理失敗。'));
    }
    await this.refreshSubmission();
  }
  act(specId: string, action: 'accept-condition' | 'implementing'): Promise<void> {
    return this.handle(() => this.interview.handleSpec(this.id, specId, action));
  }
  sendRevise(specId: string): Promise<void> {
    return this.handle(async () => { await this.interview.handleSpec(this.id, specId, 'revise', { purpose: this.revisePurpose().trim() }); await this.loadSpecs(); });
  }
  removeSpecResult(specId: string): Promise<void> {
    return this.handle(async () => { await this.interview.deleteSpec(this.id, specId); await Promise.all([this.loadDraft(), this.loadSpecs()]); });
  }
  async refreshSubmission(): Promise<void> {
    try { this.sub.set(await this.interview.submissionState(this.id)); } catch { /* 載入失敗不影響對話 */ }
  }
  /** 選項為系統對照表的所有 System Owner；與此 spec 的系統對應者標示為 agent 的建議，目前值若不在表內仍保留。 */
  ownerOptions(sp: ToolSpec): { empNo: string; label: string }[] {
    const list = this.sub()?.suggestedOwners ?? [];
    const opts = new Map<string, string>();
    for (const o of list) {
      const base = `${o.name ?? o.empNo}（${o.empNo}）`;
      const suggested = o.system === sp.system;
      if (suggested || !opts.has(o.empNo)) opts.set(o.empNo, suggested ? `${base} ★ agent 建議` : base);
    }
    if (sp.system_owner && !opts.has(sp.system_owner)) opts.set(sp.system_owner, sp.system_owner);
    return [...opts].map(([empNo, label]) => ({ empNo, label }));
  }
  explained(path: string): string {
    return this.explanations()[path] ?? '';
  }
  explain(path: string, v: string): void {
    this.explanations.update((m) => ({ ...m, [path]: v }));
  }
  /** 確認新名詞（說明必填，後端會再驗一次）；成功後重新載入，欄位狀態會變成 pending。 */
  async confirmTerm(path: string): Promise<void> {
    const term = path.split('.').pop()!.replace(/\[\]$/, '');
    this.confirmError.set(null);
    try {
      await this.interview.confirmTerm(this.id, term, (this.explanations()[path] ?? '').trim());
      await this.loadSpecs();
    } catch (e) {
      this.confirmError.set(errorMessage(e, '確認新名詞失敗。'));
    }
  }
  setReason(id: string, path: string, v: string): void {
    this.specs.update((l) => l.map((s) => (s.id === id ? { ...s, naming_reasons: { ...s.naming_reasons, [path]: v } } : s)));
    this.specsDirty.set(true);
  }
  async submitInterview(): Promise<void> {
    this.submitting.set(true);
    this.submitError.set(null);
    try {
      await this.interview.submit(this.id);
      this.ticket.set(await this.tickets.get(this.id));
      await this.refreshSubmission();
      this.tab.set(2);
    } catch (e) {
      this.submitError.set(errorMessage(e, '送出失敗。'));
      await this.refreshSubmission();
    } finally {
      this.submitting.set(false);
    }
  }

  canSubmitTicket(): boolean {
    return !!this.ticket()?.permissions.canSubmit;
  }
  canChat(): boolean {
    return !!this.ticket()?.permissions.canChat;
  }
  checked(e: Event): boolean {
    return (e.target as HTMLInputElement).checked;
  }
  json(v: unknown): string {
    return v ? JSON.stringify(v) : '';
  }
  /** 這個欄位（或整步）是否被人工修改過（標記由後端保留到 Interview 送出）。 */
  human(id: string, field: string): boolean {
    const m = this.wf()?.humanEdits ?? [];
    return m.includes(`steps.${id}.${field}`) || m.includes(`steps.${id}`);
  }
  missingOf(id: string): string[] {
    return this.specDraft?.missing[id] ?? [];
  }
  humanSpec(id: string, field: string): boolean {
    const m = this.specDraft?.humanEdits ?? [];
    return m.includes(`tool_specs.${id}.${field}`) || m.includes(`tool_specs.${id}`);
  }
  specText(sp: ToolSpec, key: string): string {
    return String((sp as unknown as Record<string, unknown>)[key] ?? '');
  }
  editSpec(id: string, field: string, value: string): void {
    this.specs.update((l) => l.map((s) => (s.id === id ? { ...s, [field]: value } : s)));
    this.specsDirty.set(true);
  }
  namingRows(sp: ToolSpec): { path: string; status: string; suggestion?: string }[] {
    return Object.entries(sp.naming_check?.fields ?? {}).map(([path, r]) => ({ path, ...r }));
  }
  /** 一鍵改成標準名：沿欄位路徑（inputs.lots[].state）找到該層 properties，原地換 key（順序與 required 一併處理）；結果在儲存後由後端重跑。 */
  applyRename(id: string, path: string, to: string): void {
    this.specs.update((l) => l.map((s) => {
      if (s.id !== id) return s;
      const copy = structuredClone(s) as unknown as Record<string, any>;
      const [root, ...tokens] = path.split('.');
      let node = copy[root];
      for (const t of tokens.slice(0, -1)) {
        node = node?.properties?.[t.replace(/\[\]$/, '')];
        if (t.endsWith('[]')) node = node?.items;
      }
      const from = tokens[tokens.length - 1];
      if (!node?.properties || !(from in node.properties)) return s;
      node.properties = Object.fromEntries(Object.entries(node.properties).map(([k, v]) => [k === from ? to : k, v]));
      if (Array.isArray(node.required)) node.required = node.required.map((k: string) => (k === from ? to : k));
      delete copy['naming_check']; // 路徑已變，舊結果作廢
      return copy as unknown as ToolSpec;
    }));
    this.specsDirty.set(true);
  }
  removeSpec(id: string): void {
    this.specs.update((l) => l.filter((s) => s.id !== id));
    this.specsDirty.set(true);
  }
  private async loadSpecs(): Promise<void> {
    try {
      const d = await this.interview.toolSpecDraft(this.id);
      this.specDraft = d;
      this.specs.set(structuredClone(d.steps));
      this.vocab.set(d.vocabulary ?? null);
      this.vocabError.set(d.vocabularyError ?? null);
      this.specsDirty.set(false);
      void this.refreshSubmission();
    } catch { /* 載入失敗不影響對話 */ }
  }
  async saveSpecs(): Promise<void> {
    this.specError.set(null);
    try {
      const d = await this.interview.saveToolSpecDraft(this.id, this.specDraft!.version, this.specs());
      this.specDraft = d;
      this.specs.set(structuredClone(d.steps));
      this.vocab.set(d.vocabulary ?? null);
      this.vocabError.set(d.vocabularyError ?? null);
      this.specsDirty.set(false);
      void this.refreshSubmission();
    } catch (e) {
      if ((e as { status?: number }).status === 409) {
        this.specError.set('Tool Spec 已被更新（agent 或其他人剛修改），已載入最新版本，請確認後重新修改。');
        await this.loadSpecs();
      } else this.specError.set(errorMessage(e, '儲存 Tool Spec 失敗。'));
    }
  }

  edit(id: string, field: keyof WfStep, value: unknown): void {
    this.steps.update((l) => l.map((s) => (s.id === id ? { ...s, [field]: value } : s)));
    this.dirty.set(true);
  }
  addStep(): void {
    const ids = new Set(this.steps().map((s) => s.id));
    let n = this.steps().length + 1;
    while (ids.has(`s${n}`)) n++;
    this.steps.update((l) => [...l, { id: `s${n}`, description: '' }]);
    this.dirty.set(true);
  }
  removeStep(id: string): void {
    this.steps.update((l) => l.filter((s) => s.id !== id));
    this.dirty.set(true);
  }

  private async loadDraft(): Promise<void> {
    try {
      const d = await this.interview.workflowDraft(this.id);
      this.wf.set(d);
      this.steps.set(structuredClone(d.steps));
      this.dirty.set(false);
    } catch { /* 草稿載入失敗不影響對話 */ }
  }

  async saveDraft(): Promise<void> {
    this.draftError.set(null);
    try {
      const d = await this.interview.saveWorkflowDraft(this.id, this.wf()!.version, this.steps());
      this.wf.set(d);
      this.steps.set(structuredClone(d.steps));
      this.dirty.set(false);
    } catch (e) {
      if ((e as { status?: number }).status === 409) {
        this.draftError.set('草稿已被更新（agent 或其他人剛修改），已載入最新版本，請確認後重新修改。');
        await this.loadDraft();
      } else this.draftError.set(errorMessage(e, '儲存草稿失敗。'));
    }
  }

  val(e: Event): string {
    return (e.target as HTMLTextAreaElement).value;
  }

  private async init(): Promise<void> {
    try {
      const [t, c] = await Promise.all([this.tickets.get(this.id), this.interview.conversation(this.id)]);
      this.ticket.set(t);
      this.messages.set(c.messages);
      await Promise.all([this.loadDraft(), this.loadSpecs(), this.refreshSubmission()]);
      if (this.sub()?.submissions.length && !t.permissions.canChat) this.tab.set(2);
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
          } else if (m.type === 'draft_changed') {
            if (!this.specsDirty()) void this.loadSpecs();
            if (!this.dirty()) void this.loadDraft(); // 有未存的編輯時不蓋掉，存檔時由版本號偵測衝突
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
      void Promise.all([this.dirty() ? null : this.loadDraft(), this.specsDirty() ? null : this.loadSpecs()]);
      this.messages.set((await this.interview.conversation(this.id)).messages);
    } catch { /* 保留畫面現況 */ }
    this.running.set(false);
  }
}
