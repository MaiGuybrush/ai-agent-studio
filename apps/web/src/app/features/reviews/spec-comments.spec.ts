import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ConfigService } from '../../core/config/config.service';
import { SpecCommentsComponent } from './spec-comments.component';

const base = '/api/tickets/T-1/specs/ts1';
const comment = { id: 1, revision: 1, authorEmpNo: 'E1', authorName: '王工', body: '請補充流程', at: '2026-11-10' };

async function setup() {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(), provideHttpClientTesting(),
      { provide: ConfigService, useValue: { normalizeUrl: (u: string) => u, config: () => ({ apiBaseUrl: '' }) } },
    ],
  });
  const http = TestBed.inject(HttpTestingController);
  const fx = TestBed.createComponent(SpecCommentsComponent);
  fx.componentRef.setInput('ticketId', 'T-1');
  fx.componentRef.setInput('specId', 'ts1');
  fx.detectChanges();
  const el = fx.nativeElement as HTMLElement;
  const tick = async () => { await fx.whenStable(); fx.detectChanges(); };
  return { http, fx, el, tick };
}
const type = (input: HTMLInputElement, v: string) => { input.value = v; input.dispatchEvent(new Event('input')); };

describe('SpecCommentsComponent', () => {
  it('renders the comment thread', async () => {
    const { http, el, tick } = await setup();
    http.expectOne(`${base}/comments`).flush([comment]);
    await tick();
    expect(el.querySelector('.comment')!.textContent).toContain('請補充流程');
    expect(el.querySelector('.comment')!.textContent).toContain('王工');
  });

  it('posts a comment then reloads the thread', async () => {
    const { http, fx, el, tick } = await setup();
    http.expectOne(`${base}/comments`).flush([]);
    await tick();
    type(el.querySelector('.comment-body')!, '好的');
    await tick();
    (el.querySelector('.send-comment') as HTMLButtonElement).click();
    const post = http.expectOne((r) => r.method === 'POST' && r.url === `${base}/comments`);
    expect(post.request.body).toEqual({ body: '好的' });
    post.flush({});
    await fx.whenStable();
    http.expectOne((r) => r.method === 'GET').flush([{ ...comment, body: '好的' }]);
    await tick();
    expect(el.querySelector('.comment')!.textContent).toContain('好的');
  });

  it('reassigns owner and shows notice', async () => {
    const { http, fx, el, tick } = await setup();
    http.expectOne(`${base}/comments`).flush([]);
    await tick();
    type(el.querySelector('.new-owner')!, 'E9');
    type(el.querySelector('.reassign-reason')!, '調職');
    await tick();
    (el.querySelector('.reassign') as HTMLButtonElement).click();
    const post = http.expectOne(`${base}/reassign-owner`);
    expect(post.request.body).toEqual({ newOwnerEmpNo: 'E9', reason: '調職' });
    post.flush({});
    await fx.whenStable();
    fx.detectChanges();
    expect(el.querySelector('.comment-notice')!.textContent).toContain('已轉派');
  });
});
