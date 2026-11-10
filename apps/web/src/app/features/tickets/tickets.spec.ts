import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { Ticket, TicketPermissions, TicketService } from '../../core/tickets/ticket.service';
import { TicketListComponent } from './ticket-list.component';
import { TicketCreateComponent } from './ticket-create.component';
import { TicketDetailComponent } from './ticket-detail.component';

const none: TicketPermissions = {
  canCancel: false, canReopen: false, canReassign: false, canViewConversation: false, canChat: false, canSubmit: false, canReopenInterview: false,
};
function ticket(p: Partial<TicketPermissions> = {}, o: Partial<Ticket> = {}): Ticket {
  return {
    id: 'T-1', description: '換線通知', cimEngineerEmpNo: 'E1', cimEngineerName: '王工',
    businessUserEmpNo: 'B1', businessUserName: '陳業', status: '訪談中', statusCode: 'Interviewing', cancelReason: null,
    createdAt: '2026-01-01T00:00:00Z', permissions: { ...none, ...p }, ...o,
  };
}

function setup(svc: Partial<Record<keyof TicketService, unknown>>, id = 'T-1') {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: TicketService, useValue: svc },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id }) } } },
    ],
  });
}
const flush = async (fx: { whenStable(): Promise<unknown>; detectChanges(): void }) => {
  await fx.whenStable();
  fx.detectChanges();
};
const el = (fx: { nativeElement: unknown }) => fx.nativeElement as HTMLElement;
const click = (root: HTMLElement, sel: string) => (root.querySelector(sel) as HTMLElement).click();
const type = (root: HTMLElement, sel: string, v: string) => {
  const i = root.querySelector(sel) as HTMLInputElement;
  i.value = v;
  i.dispatchEvent(new Event('input'));
};

describe('TicketListComponent', () => {
  it('lists tickets with links to detail and to create', async () => {
    setup({ list: () => Promise.resolve([ticket(), ticket({}, { id: 'T-2', status: '已取消' })]) });
    const fx = TestBed.createComponent(TicketListComponent);
    await flush(fx);
    const rows = el(fx).querySelectorAll('tbody tr');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('換線通知');
    expect(rows[1].textContent).toContain('已取消');
    expect(rows[0].querySelector('a')!.getAttribute('href')).toBe('/tickets/T-1');
    expect(el(fx).querySelector('a.create')!.getAttribute('href')).toBe('/tickets/new');
  });

  it('shows the error when loading fails', async () => {
    setup({ list: () => Promise.reject({ error: { detail: '壞了' } }) });
    const fx = TestBed.createComponent(TicketListComponent);
    await flush(fx);
    expect(el(fx).querySelector('.error')!.textContent).toContain('壞了');
  });
});

describe('TicketCreateComponent', () => {
  it('submits and goes to the new ticket', async () => {
    const create = vi.fn().mockResolvedValue(ticket());
    setup({ create });
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const fx = TestBed.createComponent(TicketCreateComponent);
    await flush(fx);
    type(el(fx), '[name=id]', 'T-1');
    type(el(fx), '[name=description]', '換線通知');
    type(el(fx), '[name=businessUserEmpNo]', 'B1');
    click(el(fx), 'button[type=submit]');
    await flush(fx);
    expect(create).toHaveBeenCalledWith({ id: 'T-1', description: '換線通知', businessUserEmpNo: 'B1' });
    expect(nav).toHaveBeenCalledWith(['/tickets', 'T-1']);
  });

  it('shows the backend error and stays on the page', async () => {
    setup({ create: () => Promise.reject({ error: { detail: '單號 T-1 已存在。' } }) });
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const fx = TestBed.createComponent(TicketCreateComponent);
    await flush(fx);
    type(el(fx), '[name=id]', 'T-1');
    type(el(fx), '[name=description]', 'd');
    type(el(fx), '[name=businessUserEmpNo]', 'B1');
    click(el(fx), 'button[type=submit]');
    await flush(fx);
    expect(el(fx).querySelector('.error')!.textContent).toContain('已存在');
    expect(nav).not.toHaveBeenCalled();
  });
});

describe('TicketDetailComponent', () => {
  const buttons = (fx: { nativeElement: unknown }) =>
    Array.from(el(fx).querySelectorAll('button.action')).map((b) => b.getAttribute('data-action'));

  it('shows only the actions the backend permits', async () => {
    setup({ get: () => Promise.resolve(ticket({ canCancel: true, canReassign: true, canViewConversation: true })) });
    const fx = TestBed.createComponent(TicketDetailComponent);
    await flush(fx);
    expect(el(fx).textContent).toContain('換線通知');
    expect(el(fx).textContent).toContain('王工');
    expect(buttons(fx)).toEqual(['cancel', 'reassign']);
    expect(el(fx).querySelector('a.interview')!.getAttribute('href')).toBe('/tickets/T-1/interview');
  });

  it('completed ticket offers copy-as-markdown to everyone and writes to the clipboard', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    setup({
      get: () => Promise.resolve(ticket({}, { status: '已完成', statusCode: 'Completed' })),
      markdown: () => Promise.resolve('# md'),
    });
    const fx = TestBed.createComponent(TicketDetailComponent);
    await flush(fx);
    click(el(fx), 'button.copy-md');
    await flush(fx);
    expect(writeText).toHaveBeenCalledWith('# md');
    expect(el(fx).querySelector('button.copy-md')!.textContent).toContain('已複製');
  });

  it('shows the read-only workflow, tool specs and review results from the backend', async () => {
    setup({
      get: () => Promise.resolve(ticket()),
      readOnly: () => Promise.resolve({
        ticket: ticket(),
        workflow: [{ id: 's1', description: '查工單', tool_spec: 'ts1' }],
        toolSpecs: [{
          specId: 'ts1', kind: 'New', state: 'Feasible', revision: 1, acceptedCondition: null,
          content: { purpose: '查工單用途', system: 'MES', side_effect: 'read', inputs: {}, outputs: {} },
          reviews: [{ revision: 1, result: 'Feasible', reason: null, condition: null, estimatedDays: 2, reviewerEmpNo: 'O1', at: '2026-01-02T00:00:00Z' }],
          implemented: null,
        }],
      }),
    });
    const fx = TestBed.createComponent(TicketDetailComponent);
    await flush(fx);
    await flush(fx);
    expect(el(fx).querySelector('.readonly-workflow .step')!.textContent).toContain('查工單');
    const spec = el(fx).querySelector('.readonly-specs .spec')!.textContent!;
    expect(spec).toContain('查工單用途');
    expect(spec).toContain('可行');
    expect(el(fx).querySelector('.review')!.textContent).toContain('2 人日');
  });

  it('no copy button before completion', async () => {
    setup({ get: () => Promise.resolve(ticket()) });
    const fx = TestBed.createComponent(TicketDetailComponent);
    await flush(fx);
    expect(el(fx).querySelector('button.copy-md')).toBeNull();
  });

  it('read-only viewer sees no actions and no interview link', async () => {
    setup({ get: () => Promise.resolve(ticket()) });
    const fx = TestBed.createComponent(TicketDetailComponent);
    await flush(fx);
    expect(buttons(fx)).toEqual([]);
    expect(el(fx).querySelector('a.interview')).toBeNull();
  });

  it('cancel needs a reason, then updates the ticket', async () => {
    const cancel = vi.fn().mockResolvedValue(ticket({ canReopen: true }, { status: '已取消', cancelReason: '不做了' }));
    setup({ get: () => Promise.resolve(ticket({ canCancel: true })), cancel });
    const fx = TestBed.createComponent(TicketDetailComponent);
    await flush(fx);
    click(el(fx), '[data-action=cancel]');
    fx.detectChanges();
    expect((el(fx).querySelector('button.confirm') as HTMLButtonElement).disabled).toBe(true);
    type(el(fx), '[name=reason]', '不做了');
    fx.detectChanges();
    click(el(fx), 'button.confirm');
    await flush(fx);
    expect(cancel).toHaveBeenCalledWith('T-1', '不做了');
    expect(el(fx).textContent).toContain('已取消');
    expect(el(fx).textContent).toContain('不做了');
    expect(buttons(fx)).toEqual(['reopen']); // 按鈕隨後端回傳的權限切換
  });

  it('reopen interview needs a reason, then shows the refreshed ticket', async () => {
    const reopenInterview = vi.fn().mockResolvedValue(ticket({ canCancel: true }));
    setup({ get: () => Promise.resolve(ticket({ canReopenInterview: true }, { status: '已完成', statusCode: 'Completed' })), reopenInterview });
    const fx = TestBed.createComponent(TicketDetailComponent);
    await flush(fx);
    click(el(fx), '[data-action=reopen-interview]');
    fx.detectChanges();
    expect((el(fx).querySelector('button.confirm') as HTMLButtonElement).disabled).toBe(true);
    type(el(fx), '[name=reason]', '需求變更');
    fx.detectChanges();
    click(el(fx), 'button.confirm');
    await flush(fx);
    expect(reopenInterview).toHaveBeenCalledWith('T-1', '需求變更');
    expect(el(fx).querySelector('[data-action=reopen-interview]')).toBeNull();
  });

  it('reopen and reassign call the service; errors are shown', async () => {
    const reopen = vi.fn().mockResolvedValue(ticket({ canCancel: true }));
    const reassign = vi.fn().mockRejectedValue({ error: { detail: '查無工號 E9，無法改派。' } });
    setup({ get: () => Promise.resolve(ticket({ canReopen: true, canReassign: true }, { status: '已取消' })), reopen, reassign });
    const fx = TestBed.createComponent(TicketDetailComponent);
    await flush(fx);

    click(el(fx), '[data-action=reassign]');
    fx.detectChanges();
    type(el(fx), '[name=newCimEngineerEmpNo]', 'E9');
    type(el(fx), '[name=reason]', '調職');
    fx.detectChanges();
    click(el(fx), 'button.confirm');
    await flush(fx);
    expect(reassign).toHaveBeenCalledWith('T-1', 'E9', '調職');
    expect(el(fx).querySelector('.error')!.textContent).toContain('查無工號');

    click(el(fx), '[data-action=reopen]');
    await flush(fx);
    expect(reopen).toHaveBeenCalledWith('T-1');
    expect(buttons(fx)).toEqual(['cancel']);
  });
});
