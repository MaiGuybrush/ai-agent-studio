import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Ticket, TicketService } from '../../core/tickets/ticket.service';
import { ChatMessage, Conversation, InterviewService } from '../../core/interview/interview.service';
import { SseMessage } from '../../core/interview/sse';
import { InterviewPageComponent } from './interview-page.component';

const ticket = (canChat: boolean, o: Partial<Ticket> = {}): Ticket => ({
  id: 'T-1', description: '換線通知需求', cimEngineerEmpNo: 'E1', cimEngineerName: '王工',
  businessUserEmpNo: 'B1', businessUserName: '陳業', status: '訪談中', cancelReason: null,
  createdAt: '2026-01-01T00:00:00Z',
  permissions: { canCancel: canChat, canReopen: false, canReassign: true, canViewConversation: true, canChat },
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
  const svc = {
    conversation: vi.fn(() => Promise.resolve(structuredClone(conv.current))),
    send: vi.fn(),
    reconnect: vi.fn(),
    cancel: vi.fn(() => Promise.resolve()),
  };
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: TicketService, useValue: { get: () => Promise.resolve(opts.ticket) } },
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
  return { fx, root, svc, conv, settle, say };
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
});
