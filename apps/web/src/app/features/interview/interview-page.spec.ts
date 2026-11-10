import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Ticket, TicketService } from '../../core/tickets/ticket.service';
import { ChatMessage, ComplianceReport, Conversation, InterviewService, SubmissionState, ToolSpecDraft, WorkflowDraft } from '../../core/interview/interview.service';
import { SseMessage } from '../../core/interview/sse';
import { InterviewPageComponent } from './interview-page.component';

const ticket = (canChat: boolean, o: Partial<Ticket> = {}): Ticket => ({
  id: 'T-1', description: '換線通知需求', cimEngineerEmpNo: 'E1', cimEngineerName: '王工',
  businessUserEmpNo: 'B1', businessUserName: '陳業', status: '訪談中', statusCode: 'Interviewing', cancelReason: null,
  createdAt: '2026-01-01T00:00:00Z',
  permissions: { canCancel: canChat, canReopen: false, canReassign: true, canViewConversation: true, canChat, canSubmit: canChat, canReopenInterview: false },
  ...o,
});

/** 讓測試手動推送 SSE 事件的串流。 */
function controlled() {
  const queue: (SseMessage | Error | 'end')[] = [];
  let wake: (() => void) | null = null;
  const push = (m: SseMessage | Error | 'end') => { queue.push(m); wake?.(); };
  async function* gen(): AsyncGenerator<SseMessage> {
    while (true) {
      while (!queue.length) await new Promise<void>((r) => (wake = r));
      const m = queue.shift()!;
      if (m === 'end') return;
      if (m instanceof Error) throw m;
      yield m;
    }
  }
  return { push, iterable: gen() };
}
const text = (id: number, t: string): SseMessage => ({ id: String(id), type: 'text', data: { text: t } });

function setup(opts: { ticket: Ticket; conv?: Conversation }) {
  const conv = { current: opts.conv ?? { running: false, messages: [] as ChatMessage[] } };
  const draft = { current: { version: 0, steps: [], humanEdits: [] } as WorkflowDraft };
  const specs = { current: { version: 0, steps: [], humanEdits: [], missing: {} } as ToolSpecDraft };
  const sub = { current: { blockers: [], pendingCount: 0, stage: 1, canSubmit: false, suggestedOwners: [], toolSpecs: [], submissions: [] } as SubmissionState };
  const tk = { current: opts.ticket };
  const svc = {
    submissionState: vi.fn(() => Promise.resolve(structuredClone(sub.current))),
    submit: vi.fn(),
    confirmTerm: vi.fn(() => Promise.resolve({})),
    conversation: vi.fn(() => Promise.resolve(structuredClone(conv.current))),
    oldConversation: vi.fn(() => Promise.resolve({ messages: [{ role: 'user', text: '舊訊息' }] })),
    send: vi.fn(),
    reconnect: vi.fn(),
    cancel: vi.fn(() => Promise.resolve()),
    workflowDraft: vi.fn(() => Promise.resolve(structuredClone(draft.current))),
    saveWorkflowDraft: vi.fn(),
    toolSpecDraft: vi.fn(() => Promise.resolve(structuredClone(specs.current))),
    saveToolSpecDraft: vi.fn(),
    handleSpec: vi.fn(() => Promise.resolve({})),
    deleteSpec: vi.fn(() => Promise.resolve({})),
    markImplemented: vi.fn((): Promise<ComplianceReport> => Promise.resolve({ passed: true, failures: [], warnings: [] })),
  };
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: TicketService, useValue: { get: () => Promise.resolve(tk.current) } },
      { provide: InterviewService, useValue: svc },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'T-1' }) } } },
    ],
  });
  const fx = TestBed.createComponent(InterviewPageComponent);
  const root = fx.nativeElement as HTMLElement;
  const settle = async () => { for (let i = 0; i < 5; i++) { await fx.whenStable(); await Promise.resolve(); fx.detectChanges(); } };
  const say = async (msg: string) => {
    const i = root.querySelector('textarea[name=message]') as HTMLTextAreaElement;
    i.value = msg;
    i.dispatchEvent(new Event('input'));
    fx.detectChanges();
    (root.querySelector('button.send') as HTMLButtonElement).click();
    await settle();
  };
  return { fx, root, svc, conv, draft, specs, sub, tk, settle, say };
}
const bubbles = (root: HTMLElement) => Array.from(root.querySelectorAll('.msg')).map((m) => m.textContent!.trim());

describe('InterviewPageComponent', () => {
  it('renders three columns: ticket info with 6 stages, chat with history, tab placeholders', async () => {
    const { fx, root, settle } = setup({
      ticket: ticket(true),
      conv: { running: false, messages: [{ role: 'user', text: '昨天問的' }, { role: 'assistant', text: '昨天答的' }] },
    });
    fx.detectChanges(); await settle();
    const left = root.querySelector('.col-left')!.textContent!;
    expect(left).toContain('T-1');
    expect(left).toContain('訪談中');
    expect(left).toContain('王工');
    expect(left).toContain('陳業');
    expect(left).toContain('換線通知需求');
    expect(root.querySelectorAll('.col-left .stage').length).toBe(6);
    expect(bubbles(root)).toEqual(['昨天問的', '昨天答的']);
    expect(Array.from(root.querySelectorAll('.col-right .tab')).map((t) => t.textContent!.trim()))
      .toEqual(['Workflow Definition', 'Tool Spec', '評估進度']);
  });

  it('shows the reopen reason and lets the user read an old conversation', async () => {
    const { fx, root, settle } = setup({
      ticket: ticket(true),
      conv: { running: false, messages: [], reopenReason: '需求變更', previous: [{ id: 7, reopenReason: null, createdAt: '2026-01-02T00:00:00Z' }] },
    });
    fx.detectChanges(); await settle();
    expect(root.querySelector('.reopen-reason')!.textContent).toContain('需求變更');
    (root.querySelector('.old-link') as HTMLElement).click();
    await settle();
    expect(root.querySelector('.old-conversation')!.textContent).toContain('舊訊息');
  });

  it('streams the reply into the chat, locks the input while running, unlocks after done', async () => {
    const { fx, root, svc, conv, settle, say } = setup({ ticket: ticket(true) });
    fx.detectChanges(); await settle();
    const s = controlled();
    svc.send.mockReturnValue(s.iterable);

    await say('我要做換線通知');
    expect(svc.send).toHaveBeenCalledWith('T-1', '我要做換線通知');
    expect(bubbles(root)).toEqual(['我要做換線通知']);
    expect((root.querySelector('textarea[name=message]') as HTMLTextAreaElement).disabled).toBe(true);

    s.push(text(1, '請問'));
    s.push(text(2, '觸發時機？'));
    await settle();
    expect(bubbles(root)).toEqual(['我要做換線通知', '請問觸發時機？']);

    conv.current = { running: false, messages: [{ role: 'user', text: '我要做換線通知' }, { role: 'assistant', text: '請問觸發時機？' }] };
    s.push({ id: '3', type: 'done', data: { usage: {} } });
    s.push('end');
    await settle();
    expect(bubbles(root)).toEqual(['我要做換線通知', '請問觸發時機？']);
    expect((root.querySelector('textarea[name=message]') as HTMLTextAreaElement).disabled).toBe(false);
  });

  it('shows a clear message for a busy model and offers retry without resending text', async () => {
    const { fx, root, svc, conv, settle, say } = setup({ ticket: ticket(true) });
    fx.detectChanges(); await settle();
    const s = controlled();
    svc.send.mockReturnValueOnce(s.iterable);
    await say('問題');
    conv.current = { running: false, messages: [{ role: 'user', text: '問題' }] }; // 後端捨棄半截回覆，只留使用者訊息
    s.push(text(1, '半句'));
    s.push({ id: '2', type: 'error', data: { code: 'model_busy', message: '429' } });
    s.push('end');
    await settle();
    expect(root.querySelector('.error')!.textContent).toContain('模型額度不足或忙碌，請稍後再試');
    expect(bubbles(root)).toEqual(['問題']);

    const retry = controlled();
    svc.send.mockReturnValueOnce(retry.iterable);
    (root.querySelector('button.retry') as HTMLButtonElement).click();
    await settle();
    expect(svc.send).toHaveBeenLastCalledWith('T-1', null);
    expect(root.querySelector('.error')).toBeNull();
    retry.push('end'); await settle();
  });

  it('reconnects with Last-Event-ID when the stream drops mid-run', async () => {
    const { fx, root, svc, settle, say } = setup({ ticket: ticket(true) });
    fx.detectChanges(); await settle();
    const first = controlled();
    const second = controlled();
    svc.send.mockReturnValue(first.iterable);
    svc.reconnect.mockReturnValue(second.iterable);
    await say('q');
    first.push(text(1, 'A'));
    first.push(new Error('network'));
    await settle();
    expect(svc.reconnect).toHaveBeenCalledWith('T-1', '1');
    second.push(text(2, 'B'));
    await settle();
    expect(bubbles(root)).toEqual(['q', 'AB']);
    second.push('end'); await settle();
  });

  it('opening mid-run (e.g. after refresh) resumes the stream and locks the input', async () => {
    const { fx, root, svc, settle } = setup({
      ticket: ticket(true),
      conv: { running: true, messages: [{ role: 'user', text: '問題' }] },
    });
    const s = controlled();
    svc.reconnect.mockReturnValue(s.iterable);
    fx.detectChanges(); await settle();
    expect(svc.reconnect).toHaveBeenCalledWith('T-1', null);
    expect((root.querySelector('textarea[name=message]') as HTMLTextAreaElement).disabled).toBe(true);
    s.push(text(1, '續'));
    await settle();
    expect(bubbles(root)).toEqual(['問題', '續']);
    s.push('end'); await settle();
  });

  it('abort button calls cancel while running', async () => {
    const { fx, root, svc, settle, say } = setup({ ticket: ticket(true) });
    fx.detectChanges(); await settle();
    const s = controlled();
    svc.send.mockReturnValue(s.iterable);
    await say('q');
    (root.querySelector('button.abort') as HTMLButtonElement).click();
    await settle();
    expect(svc.cancel).toHaveBeenCalledWith('T-1');
    s.push('end'); await settle();
  });

  it('draft_changed refreshes the Workflow Definition tab live and marks human-edited fields', async () => {
    const { fx, root, svc, draft, settle, say } = setup({ ticket: ticket(true) });
    fx.detectChanges(); await settle();
    expect(root.querySelectorAll('.step').length).toBe(0);
    const s = controlled();
    svc.send.mockReturnValue(s.iterable);
    await say('開始');

    draft.current = { version: 2, steps: [{ id: 's1', description: '收單' }, { id: 's2', description: '查詢' }], humanEdits: ['steps.s2.description'] };
    s.push({ id: '1', type: 'draft_changed', data: { version: 2 } });
    await settle();
    expect(Array.from(root.querySelectorAll('.step .f-description')).map((t) => (t as HTMLTextAreaElement).value)).toEqual(['收單', '查詢']);
    const marked = Array.from(root.querySelectorAll('.step')).map((st) => !!st.querySelector('.badge'));
    expect(marked).toEqual([false, true]); // 只標示人工修改過的欄位
    s.push('end'); await settle();
  });

  it('saves engineer edits with the version number and reloads with a message on conflict', async () => {
    const { fx, root, svc, draft, settle } = setup({ ticket: ticket(true) });
    draft.current = { version: 3, steps: [{ id: 's1', description: 'A' }], humanEdits: [] };
    fx.detectChanges(); await settle();
    const edit = (v: string) => {
      const t = root.querySelector('.f-description') as HTMLTextAreaElement;
      t.value = v; t.dispatchEvent(new Event('input')); fx.detectChanges();
    };
    const save = async () => { (root.querySelector('button.save') as HTMLButtonElement).click(); await settle(); };

    edit('B');
    svc.saveWorkflowDraft.mockResolvedValue({ version: 4, steps: [{ id: 's1', description: 'B' }], humanEdits: ['steps.s1.description'] });
    await save();
    expect(svc.saveWorkflowDraft).toHaveBeenCalledWith('T-1', 3, [{ id: 's1', description: 'B' }]);
    expect(root.querySelector('.step .badge')).not.toBeNull();

    edit('C');
    draft.current = { version: 5, steps: [{ id: 's1', description: 'agent 剛改' }], humanEdits: [] };
    svc.saveWorkflowDraft.mockRejectedValue({ status: 409 });
    await save();
    expect(root.querySelector('.draft-error')!.textContent).toContain('已被更新');
    expect((root.querySelector('.f-description') as HTMLTextAreaElement).value).toBe('agent 剛改');
  });

  it('read-only for a non-responsible CIM Engineer: sees history, no input', async () => {
    const { fx, root, settle } = setup({
      ticket: ticket(false),
      conv: { running: false, messages: [{ role: 'user', text: '別人的對話' }] },
    });
    fx.detectChanges(); await settle();
    expect(bubbles(root)).toEqual(['別人的對話']);
    expect(root.querySelector('textarea[name=message]')).toBeNull();
    expect(root.querySelector('.readonly')!.textContent).toContain('唯讀');
  });

  it('Tool Spec tab lists specs with missing fields and human-edit marks, and saves edits with the version', async () => {
    const { fx, root, svc, specs, settle } = setup({ ticket: ticket(true) });
    specs.current = {
      version: 2, humanEdits: ['tool_specs.ts1.purpose'], missing: { ts1: ['inputs', 'system_owner'] },
      steps: [{ id: 'ts1', kind: 'New', purpose: '查工單', system: 'MES' }],
    };
    fx.detectChanges(); await settle();
    (root.querySelectorAll('.col-right .tab')[1] as HTMLElement).click();
    fx.detectChanges(); await settle();
    const card = root.querySelector('.spec')!;
    expect(card.textContent).toContain('New');
    expect(card.textContent).toContain('MES');
    expect(card.querySelector('.missing')!.textContent).toContain('inputs');
    expect(card.querySelector('.missing')!.textContent).toContain('system_owner');
    expect(card.querySelector('.f-purpose')!.classList.contains('human')).toBe(true);

    const input = card.querySelector('.f-purpose') as HTMLTextAreaElement;
    input.value = '查工單狀態';
    input.dispatchEvent(new Event('input'));
    fx.detectChanges();
    svc.saveToolSpecDraft.mockResolvedValue({ ...specs.current, version: 3 });
    (root.querySelector('.save-specs') as HTMLButtonElement).click();
    await settle();
    expect(svc.saveToolSpecDraft).toHaveBeenCalledWith('T-1', 2, [{ id: 'ts1', kind: 'New', purpose: '查工單狀態', system: 'MES' }]);
  });

  it('shows Naming Check status per field (nested too) and one-click renames to the standard name', async () => {
    const { fx, root, svc, specs, settle } = setup({ ticket: ticket(true) });
    specs.current = {
      version: 2, humanEdits: [], missing: {},
      vocabulary: { sha: 'abcdef1234567890', fetchedAt: '2026-11-10T08:00:00Z', warning: '詞彙更新失敗，沿用 abcdef1234567890' },
      steps: [{
        id: 'ts1', kind: 'New',
        inputs: { type: 'object', properties: { equipmentId: { type: 'string' }, detail: { type: 'object', properties: { state: { type: 'string' } } } } },
        naming_check: {
          sha: 'abcdef1234567890',
          fields: {
            'inputs.equipmentId': { status: 'rename', suggestion: 'eqptId' },
            'inputs.detail': { status: 'new' },
            'inputs.detail.state': { status: 'rename', suggestion: 'status' },
          },
        },
      }],
    };
    fx.detectChanges(); await settle();
    (root.querySelectorAll('.col-right .tab')[1] as HTMLElement).click();
    fx.detectChanges(); await settle();
    const rows = Array.from(root.querySelectorAll('.naming-row')).map((r) => r.textContent!.replace(/\s+/g, ' ').trim());
    expect(rows[0]).toContain('inputs.equipmentId'); expect(rows[0]).toContain('建議改名'); expect(rows[0]).toContain('eqptId');
    expect(rows[1]).toContain('新名詞');
    expect(rows[2]).toContain('inputs.detail.state');
    expect(root.querySelector('.vocab-info')!.textContent).toContain('abcdef1');
    expect(root.querySelector('.vocab-warning')!.textContent).toContain('沿用');

    (root.querySelectorAll('.apply-rename')[1] as HTMLButtonElement).click(); // 巢狀欄位 state → status
    (root.querySelectorAll('.apply-rename')[0] as HTMLButtonElement).click(); // equipmentId → eqptId
    fx.detectChanges();
    svc.saveToolSpecDraft.mockResolvedValue({ ...specs.current, version: 3 });
    (root.querySelector('.save-specs') as HTMLButtonElement).click();
    await settle();
    const saved = svc.saveToolSpecDraft.mock.calls.at(-1)![2][0] as { inputs: { properties: Record<string, { properties?: object }> } };
    expect(Object.keys(saved.inputs.properties)).toEqual(['eqptId', 'detail']); // 順序保留
    expect(Object.keys(saved.inputs.properties['detail'].properties!)).toEqual(['status']);
  });

  const owner = (system: string, empNo: string, name: string) => ({ system, empNo, name });
  const blocker = (code: string, message: string) => ({ code, message }) as SubmissionState['blockers'][number];
  const openSpecTab = async (fx: { detectChanges(): void }, root: HTMLElement, settle: () => Promise<void>) => {
    fx.detectChanges(); await settle();
    (root.querySelectorAll('.col-right .tab')[1] as HTMLElement).click();
    fx.detectChanges(); await settle();
  };
  const type = (el: HTMLInputElement, v: string) => { el.value = v; el.dispatchEvent(new Event('input')); };
  const refresh = async (fx: { componentInstance: unknown; detectChanges(): void }, settle: () => Promise<void>) => {
    await (fx.componentInstance as { refreshSubmission(): Promise<void> }).refreshSubmission(); fx.detectChanges(); await settle();
  };

  it('shows pending count at all times but expands the blocker list only from stage 5, submit button only at stage 6', async () => {
    const { fx, root, sub, settle } = setup({ ticket: ticket(true) });
    sub.current = { ...sub.current, stage: 3, pendingCount: 2, blockers: [blocker('owner', 'ts1 缺 System Owner'), blocker('new_term', 'x 是新名詞')] };
    fx.detectChanges(); await settle();
    expect(root.querySelector('.pending-count')!.textContent).toContain('送出前待處理 2 項');
    expect(root.querySelector('.blocker-list')).toBeNull();
    expect(root.querySelector('button.submit-interview')).toBeNull();

    sub.current = { ...sub.current, stage: 5 };
    await refresh(fx, settle);
    expect(Array.from(root.querySelectorAll('.blocker-list li')).map((l) => l.textContent!.trim())).toEqual(['ts1 缺 System Owner', 'x 是新名詞']);
    expect(root.querySelector('button.submit-interview')).toBeNull();

    sub.current = { ...sub.current, stage: 6, pendingCount: 0, blockers: [], canSubmit: true };
    await refresh(fx, settle);
    expect(root.querySelector('.blocker-list')).toBeNull();
    expect((root.querySelector('button.submit-interview') as HTMLButtonElement).disabled).toBe(false);
  });

  it('submits, disables the input, and switches the right side to evaluation progress', async () => {
    const { fx, root, svc, sub, tk, settle } = setup({ ticket: ticket(true) });
    sub.current = { ...sub.current, stage: 6, canSubmit: true };
    fx.detectChanges(); await settle();
    svc.submit.mockImplementation(async () => {
      tk.current = ticket(false, { status: '評估中' });
      sub.current = { ...sub.current, canSubmit: false, toolSpecs: [{ specId: 'ts1', kind: 'New', state: 'Pending', revision: 1 }, { specId: 'ts2', kind: 'Reuse', state: 'Implemented', revision: 1 }], submissions: [{ number: 1 }] };
      return { number: 1, status: '評估中' };
    });
    (root.querySelector('button.submit-interview') as HTMLButtonElement).click();
    await settle();
    expect(svc.submit).toHaveBeenCalledWith('T-1');
    expect(root.querySelector('textarea[name=message]')).toBeNull();
    expect(root.querySelector('.col-left')!.textContent).toContain('評估中');
    const progress = Array.from(root.querySelectorAll('.progress-row')).map((r) => r.textContent!.replace(/\s+/g, ' ').trim());
    expect(progress[0]).toContain('ts1'); expect(progress[0]).toContain('待評估');
    expect(progress[1]).toContain('ts2'); expect(progress[1]).toContain('已實作');
  });

  it('shows the server error when submit is rejected', async () => {
    const { fx, root, svc, sub, settle } = setup({ ticket: ticket(true) });
    sub.current = { ...sub.current, stage: 6, canSubmit: true };
    fx.detectChanges(); await settle();
    svc.submit.mockRejectedValue({ status: 409, error: { title: '還有未處理的項目，無法送出。' } });
    (root.querySelector('button.submit-interview') as HTMLButtonElement).click();
    await settle();
    expect(root.querySelector('.submit-error')!.textContent).toContain('還有未處理的項目');
  });

  it('marks the agent-suggested System Owner in the selector and writes the chosen empNo to the draft', async () => {
    const { fx, root, svc, specs, sub, settle } = setup({ ticket: ticket(true) });
    sub.current = { ...sub.current, suggestedOwners: [owner('MES', '10000001', '王MES'), owner('ERP', '10000002', '李ERP')] };
    specs.current = { version: 1, humanEdits: [], missing: {}, steps: [{ id: 'ts1', kind: 'New', system: 'MES' }] };
    await openSpecTab(fx, root, settle);
    const sel = root.querySelector('select.owner-select') as HTMLSelectElement;
    const opts = Array.from(sel.options).map((o) => o.textContent!.trim());
    expect(opts.find((o) => o.includes('王MES'))).toContain('建議');
    expect(opts.find((o) => o.includes('李ERP'))).not.toContain('建議');
    sel.value = '10000002'; sel.dispatchEvent(new Event('change')); fx.detectChanges();
    svc.saveToolSpecDraft.mockResolvedValue({ ...specs.current, version: 2 });
    (root.querySelector('.save-specs') as HTMLButtonElement).click(); await settle();
    expect((svc.saveToolSpecDraft.mock.calls.at(-1)![2][0] as { system_owner: string }).system_owner).toBe('10000002');
  });

  it('renders pending terms as known and confirms a new term only with an explanation', async () => {
    const { fx, root, svc, specs, settle } = setup({ ticket: ticket(true) });
    specs.current = {
      version: 1, humanEdits: [], missing: {},
      steps: [{ id: 'ts1', kind: 'New', naming_check: { sha: 's', fields: { 'inputs.lotGrade': { status: 'new' }, 'inputs.waferSize': { status: 'pending' } } } }],
    };
    await openSpecTab(fx, root, settle);
    const rows = Array.from(root.querySelectorAll('.naming-row')).map((r) => r.textContent!.replace(/\s+/g, ' ').trim());
    expect(rows[1]).toContain('Pending Term');
    expect(root.querySelectorAll('.confirm-term').length).toBe(1);
    const btn = root.querySelector('button.confirm-term') as HTMLButtonElement;
    expect(btn.disabled).toBe(true);
    type(root.querySelector('input.term-explanation') as HTMLInputElement, '   '); fx.detectChanges();
    expect(btn.disabled).toBe(true);
    type(root.querySelector('input.term-explanation') as HTMLInputElement, '批號等級'); fx.detectChanges();
    expect(btn.disabled).toBe(false);
    btn.click(); await settle();
    expect(svc.confirmTerm).toHaveBeenCalledWith('T-1', 'lotGrade', '批號等級');
    expect(svc.toolSpecDraft.mock.calls.length).toBeGreaterThan(1); // 確認後重新載入，狀態變成 pending
  });

  it('records the reason on the field when a suggested rename is not applied', async () => {
    const { fx, root, svc, specs, settle } = setup({ ticket: ticket(true) });
    specs.current = {
      version: 1, humanEdits: [], missing: {},
      steps: [{ id: 'ts1', kind: 'New', naming_check: { sha: 's', fields: { 'inputs.equipmentId': { status: 'rename', suggestion: 'eqptId' } } } }],
    };
    await openSpecTab(fx, root, settle);
    type(root.querySelector('input.rename-reason') as HTMLInputElement, '舊系統沿用'); fx.detectChanges();
    svc.saveToolSpecDraft.mockResolvedValue({ ...specs.current, version: 2 });
    (root.querySelector('.save-specs') as HTMLButtonElement).click(); await settle();
    expect((svc.saveToolSpecDraft.mock.calls.at(-1)![2][0] as { naming_reasons: Record<string, string> }).naming_reasons).toEqual({ 'inputs.equipmentId': '舊系統沿用' });
  });

  describe('handling evaluation results (#28)', () => {
    const specs = [
      { specId: 'ts1', kind: 'New', state: 'Conditional', revision: 1 },
      { specId: 'ts2', kind: 'New', state: 'Infeasible', revision: 1 },
      { specId: 'ts3', kind: 'New', state: 'Feasible', revision: 1 },
    ];
    const handled = async (o: Partial<SubmissionState>) => {
      const r = setup({ ticket: ticket(false, { status: '評估中' }) });
      r.sub.current = { ...r.sub.current, stage: 6, toolSpecs: specs, submissions: [{ number: 1 }], canHandle: true, ...o };
      r.fx.detectChanges(); await r.settle();
      return r;
    };
    const row = (root: HTMLElement, id: string) => Array.from(root.querySelectorAll('.progress-row')).find((r) => r.textContent!.includes(id)) as HTMLElement;

    it('shows per-state actions to the responsible engineer and calls the API', async () => {
      const { root, svc, settle } = await handled({});
      expect(row(root, 'ts1').querySelector('.accept-condition')).not.toBeNull();
      expect(row(root, 'ts1').querySelector('.delete-spec')).toBeNull();
      expect(row(root, 'ts2').querySelector('.delete-spec')).not.toBeNull();
      expect(row(root, 'ts3').querySelector('.mark-implementing')).not.toBeNull();
      (row(root, 'ts1').querySelector('.accept-condition') as HTMLButtonElement).click(); await settle();
      expect(svc.handleSpec).toHaveBeenCalledWith('T-1', 'ts1', 'accept-condition');
      (row(root, 'ts3').querySelector('.mark-implementing') as HTMLButtonElement).click(); await settle();
      expect(svc.handleSpec).toHaveBeenCalledWith('T-1', 'ts3', 'implementing');
      (row(root, 'ts2').querySelector('.delete-spec') as HTMLButtonElement).click(); await settle();
      expect(svc.deleteSpec).toHaveBeenCalledWith('T-1', 'ts2');
    });

    it('sends the edited purpose for re-review', async () => {
      const { fx, root, svc, settle } = await handled({});
      (row(root, 'ts2').querySelector('.revise-open') as HTMLButtonElement).click(); fx.detectChanges();
      const ta = row(root, 'ts2').querySelector('textarea.revise-purpose') as HTMLTextAreaElement;
      ta.value = '新的用途'; ta.dispatchEvent(new Event('input')); fx.detectChanges();
      (row(root, 'ts2').querySelector('.revise-send') as HTMLButtonElement).click(); await settle();
      expect(svc.handleSpec).toHaveBeenCalledWith('T-1', 'ts2', 'revise', { purpose: '新的用途' });
    });

    it('hides actions for anyone but the responsible engineer', async () => {
      const a = await handled({ canHandle: false });
      expect(a.root.querySelector('.accept-condition, .delete-spec, .mark-implementing')).toBeNull();
    });

    it('points to Cancellation when all specs are infeasible', async () => {
      const b = await handled({ allInfeasible: true, toolSpecs: specs.map((s) => ({ ...s, state: 'Infeasible' })) });
      expect(b.root.querySelector('.cancel-hint')!.textContent).toContain('Cancellation');
    });
  });

  describe('interface compliance check (#29)', () => {
    const specs = [{ specId: 'ts1', kind: 'New', state: 'Implementing', revision: 1 }, { specId: 'ts2', kind: 'New', state: 'Feasible', revision: 1 }];
    const setupImpl = async () => {
      const r = setup({ ticket: ticket(false, { status: '實作中' }) });
      r.sub.current = { ...r.sub.current, stage: 6, toolSpecs: specs, submissions: [{ number: 1 }], canHandle: true };
      r.fx.detectChanges(); await r.settle();
      return r;
    };
    const row = (root: HTMLElement, id: string) => Array.from(root.querySelectorAll('.progress-row')).find((r) => r.textContent!.includes(id)) as HTMLElement;
    const fill = (el: HTMLInputElement, v: string) => { el.value = v; el.dispatchEvent(new Event('input')); };

    it('only offers the check on implementing specs and sends serverKey and toolName', async () => {
      const { fx, root, svc, settle } = await setupImpl();
      expect(row(root, 'ts2').querySelector('.implemented-open')).toBeNull();
      (row(root, 'ts1').querySelector('.implemented-open') as HTMLButtonElement).click(); fx.detectChanges();
      fill(row(root, 'ts1').querySelector('input.impl-server') as HTMLInputElement, 'gw');
      fill(row(root, 'ts1').querySelector('input.impl-tool') as HTMLInputElement, 'get_order'); fx.detectChanges();
      (row(root, 'ts1').querySelector('.implemented-send') as HTMLButtonElement).click(); await settle();
      expect(svc.markImplemented).toHaveBeenCalledWith('T-1', 'ts1', 'gw', 'get_order');
    });

    it('shows the failure report and warnings', async () => {
      const { fx, root, svc, settle } = await setupImpl();
      svc.markImplemented.mockResolvedValueOnce({ passed: false, failures: ['inputs.eqptId：型別不符'], warnings: ['outputs.equipmentId 命中別名'] });
      (row(root, 'ts1').querySelector('.implemented-open') as HTMLButtonElement).click(); fx.detectChanges();
      fill(row(root, 'ts1').querySelector('input.impl-server') as HTMLInputElement, 'gw');
      fill(row(root, 'ts1').querySelector('input.impl-tool') as HTMLInputElement, 't'); fx.detectChanges();
      (row(root, 'ts1').querySelector('.implemented-send') as HTMLButtonElement).click(); await settle(); fx.detectChanges();
      expect(row(root, 'ts1').querySelector('.compliance-failures')!.textContent).toContain('型別不符');
      expect(row(root, 'ts1').querySelector('.compliance-warnings')!.textContent).toContain('別名');
    });
  });
});
