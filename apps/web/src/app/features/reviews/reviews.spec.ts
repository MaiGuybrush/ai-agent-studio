import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { ReviewDetail, ReviewService } from '../../core/reviews/review.service';
import { ReviewPendingComponent } from './review-pending.component';
import { ReviewPageComponent } from './review-page.component';

const flush = async (fx: { whenStable(): Promise<unknown>; detectChanges(): void }) => {
  await fx.whenStable();
  fx.detectChanges();
};
const el = (fx: { nativeElement: unknown }) => fx.nativeElement as HTMLElement;

function detail(o: Partial<ReviewDetail> = {}): ReviewDetail {
  return {
    ticketId: 'T-1', specId: 'ts1', revision: 1, kind: 'New', state: 'Pending', canReply: true,
    content: { purpose: '查工單' }, workflow: [{ description: '查工單' }], cimEngineerName: '王工', businessUserName: '陳業',
    waitingDays: 3, history: [], ...o,
  };
}
function setup(svc: Partial<Record<keyof ReviewService, unknown>>) {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: ReviewService, useValue: svc },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ ticketId: 'T-1', specId: 'ts1' }) } } },
    ],
  });
}

describe('ReviewPendingComponent', () => {
  it('lists assigned specs with waiting days and links to the evaluation screen', async () => {
    setup({ pending: () => Promise.resolve([{ ...detail(), purpose: '查工單' }]) });
    const fx = TestBed.createComponent(ReviewPendingComponent);
    await flush(fx);
    const row = el(fx).querySelector('tbody tr')!;
    expect(row.textContent).toContain('T-1');
    expect(row.textContent).toContain('王工');
    expect(row.querySelector('.waiting')!.textContent).toContain('3 天');
    expect(row.querySelector('a')!.getAttribute('href')).toBe('/reviews/T-1/ts1');
  });
});

describe('ReviewPageComponent', () => {
  it('shows read-only content without a conversation and the reply form', async () => {
    setup({ get: () => Promise.resolve(detail()) });
    const fx = TestBed.createComponent(ReviewPageComponent);
    await flush(fx);
    expect(el(fx).querySelector('.workflow')!.textContent).toContain('查工單');
    expect(el(fx).querySelector('form.reply')).toBeTruthy();
    expect(el(fx).textContent).not.toContain('對話');
  });

  it('hides the form once the CIM Engineer has handled it', async () => {
    setup({ get: () => Promise.resolve(detail({ canReply: false })) });
    const fx = TestBed.createComponent(ReviewPageComponent);
    await flush(fx);
    expect(el(fx).querySelector('form.reply')).toBeNull();
    expect(el(fx).querySelector('.locked')).toBeTruthy();
  });

  it('shows the server validation errors on a rejected reply', async () => {
    setup({ get: () => Promise.resolve(detail()), reply: () => Promise.reject({ error: { errors: ['附條件時，條件內容必填。'] } }) });
    const fx = TestBed.createComponent(ReviewPageComponent);
    await flush(fx);
    fx.componentInstance.submit();
    await flush(fx);
    expect(el(fx).querySelector('.error')!.textContent).toContain('條件內容必填');
  });
});
