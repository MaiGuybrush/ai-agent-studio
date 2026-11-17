import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApiKeysPageComponent } from './api-keys-page.component';
import { ConfigService } from '../../core/config/config.service';
import { ApiKey } from '../../core/api-keys/api-keys.service';

const key = (o: Partial<ApiKey>): ApiKey => ({
  id: 'k1', empId: 'E1', keyPrefix: 'pk_abcd', label: 'my key', scopes: ['mes'],
  expiresAt: null, createdAt: '2026-01-01T00:00:00Z', lastUsedAt: null, ...o,
});

describe('ApiKeysPageComponent', () => {
  function setup() {
    TestBed.configureTestingModule({
      imports: [ApiKeysPageComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(),
        { provide: ConfigService, useValue: { authApiUrl: () => 'http://uac/auth' } },
      ],
    });
    const fixture = TestBed.createComponent(ApiKeysPageComponent);
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    const req = http.expectOne('http://uac/auth/api-keys');
    const root = () => fixture.nativeElement as HTMLElement;
    return { fixture, req, root };
  }

  it('lists keys with all-systems badge and expired mark', () => {
    const { fixture, req, root } = setup();
    req.flush([
      key({ id: 'a', label: 'alpha', scopes: ['*'] }),
      key({ id: 'b', label: 'beta', expiresAt: '2020-01-01T00:00:00Z' }),
      key({ id: 'c', label: 'gamma', expiresAt: '2999-01-01T00:00:00Z' }),
    ]);
    fixture.detectChanges();
    const rows = root().querySelectorAll('tbody tr');
    expect(rows.length).toBe(3);
    expect(rows[0].textContent).toContain('alpha');
    expect(rows[0].textContent).toContain('pk_abcd');
    expect(rows[0].querySelector('.badge-all')?.textContent).toContain('全部系統');
    expect(rows[1].textContent).toContain('已過期');
    expect(rows[2].textContent).not.toContain('已過期');
  });

  it('shows empty state with a create button', () => {
    const { fixture, req, root } = setup();
    req.flush([]);
    fixture.detectChanges();
    expect(root().querySelector('.empty')).toBeTruthy();
    expect(root().querySelector('button.create')?.textContent).toContain('建立');
  });

  it('shows an explicit error when loading fails', () => {
    const { fixture, req, root } = setup();
    req.flush('x', { status: 500, statusText: 'err' });
    fixture.detectChanges();
    expect(root().querySelector('.error')?.textContent).toContain('無法載入');
  });

  describe('revoke', () => {
    function withKeys() {
      const s = setup();
      s.req.flush([key({ id: 'a', label: 'alpha', scopes: ['mes', 'erp'] }), key({ id: 'b', label: 'beta' })]);
      s.fixture.detectChanges();
      const open = () => {
        (s.root().querySelector('tbody tr button.revoke') as HTMLButtonElement).click();
        s.fixture.detectChanges();
      };
      return { ...s, open, http: TestBed.inject(HttpTestingController) };
    }

    it('confirm dialog shows label and scopes; cancel sends nothing', () => {
      const { fixture, root, open, http } = withKeys();
      open();
      const dlg = root().querySelector('.revoke-dialog')!;
      expect(dlg.textContent).toContain('alpha');
      expect(dlg.textContent).toContain('mes');
      expect(dlg.textContent).toContain('erp');
      (dlg.querySelector('button.cancel') as HTMLButtonElement).click();
      fixture.detectChanges();
      expect(root().querySelector('.revoke-dialog')).toBeNull();
      expect(root().querySelectorAll('tbody tr').length).toBe(2);
      http.verify();
    });

    it('confirm sends DELETE and removes the row', () => {
      const { fixture, root, open, http } = withKeys();
      open();
      (root().querySelector('.revoke-dialog button.confirm') as HTMLButtonElement).click();
      const del = http.expectOne('http://uac/auth/api-keys/a');
      expect(del.request.method).toBe('DELETE');
      del.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();
      const rows = root().querySelectorAll('tbody tr');
      expect(rows.length).toBe(1);
      expect(rows[0].textContent).toContain('beta');
      expect(root().querySelector('.revoke-dialog')).toBeNull();
    });

    it('failure shows an error and keeps the list', () => {
      const { fixture, root, open, http } = withKeys();
      open();
      (root().querySelector('.revoke-dialog button.confirm') as HTMLButtonElement).click();
      http.expectOne('http://uac/auth/api-keys/a').flush('x', { status: 500, statusText: 'err' });
      fixture.detectChanges();
      expect(root().querySelector('.revoke-error')?.textContent).toContain('撤銷失敗');
      expect(root().querySelectorAll('tbody tr').length).toBe(2);
    });
  });

  describe('create', () => {
    const BASE = 'http://uac/auth/api-keys';
    function openForm() {
      const ctx = setup();
      ctx.req.flush([]);
      ctx.fixture.detectChanges();
      const http = TestBed.inject(HttpTestingController);
      const q = <T extends HTMLElement>(sel: string) => ctx.root().querySelector(sel) as T;
      q('button.create').click();
      http.expectOne(`${BASE}/systems`).flush(['mes', 'wms']);
      ctx.fixture.detectChanges();
      const type = (sel: string, v: string) => {
        const el = q<HTMLInputElement>(sel); el.value = v; el.dispatchEvent(new Event('input')); el.dispatchEvent(new Event('change'));
        ctx.fixture.detectChanges();
      };
      const click = (sel: string) => { q(sel).click(); ctx.fixture.detectChanges(); };
      return { ...ctx, http, q, type, click };
    }

    it('blocks submit until label and a scope are given, label max 50', () => {
      const { q, type, click, http } = openForm();
      expect(q<HTMLButtonElement>('button.submit').disabled).toBe(true);
      type('input.label', 'a'.repeat(51));
      click('input.scope-mes');
      expect(q<HTMLButtonElement>('button.submit').disabled).toBe(true);
      type('input.label', 'a'.repeat(50));
      expect(q<HTMLButtonElement>('button.submit').disabled).toBe(false);
      http.expectNone(BASE);
    });

    it('* disables other scopes and shows a risk warning', () => {
      const { q, click, root } = openForm();
      click('input.scope-mes');
      click('input.scope-all');
      expect(q<HTMLInputElement>('input.scope-mes').disabled).toBe(true);
      expect(root().querySelector('.risk')).toBeTruthy();
    });

    it('defaults to 90 days and posts label, scopes and expiresAt', () => {
      const { q, type, click, http } = openForm();
      type('input.label', 'ci');
      click('input.scope-mes');
      click('input.scope-wms');
      const before = Date.now();
      click('button.submit');
      const r = http.expectOne(BASE);
      expect(r.request.method).toBe('POST');
      expect(r.request.body.label).toBe('ci');
      expect(r.request.body.scopes).toEqual(['mes', 'wms']);
      const days = (new Date(r.request.body.expiresAt).getTime() - before) / 86400000;
      expect(days).toBeGreaterThan(89.99);
      expect(days).toBeLessThan(90.01);
    });

    it('sends * alone, 30 days, custom date and null for never', () => {
      const { q, type, click, http, fixture } = openForm();
      type('input.label', 'x');
      click('input.scope-all');
      q<HTMLSelectElement>('select.expiry').value = '30';
      q('select.expiry').dispatchEvent(new Event('change')); fixture.detectChanges();
      click('button.submit');
      let r = http.expectOne(BASE);
      expect(r.request.body.scopes).toEqual(['*']);
      expect(Math.round((new Date(r.request.body.expiresAt).getTime() - Date.now()) / 86400000)).toBe(30);
      r.flush('x', { status: 400, statusText: 'bad' });

      q<HTMLSelectElement>('select.expiry').value = 'custom';
      q('select.expiry').dispatchEvent(new Event('change')); fixture.detectChanges();
      type('input.custom-date', '2030-05-01');
      click('button.submit');
      r = http.expectOne(BASE);
      expect(r.request.body.expiresAt).toContain('2030-05-0');
      r.flush('x', { status: 400, statusText: 'bad' });

      q<HTMLSelectElement>('select.expiry').value = 'none';
      q('select.expiry').dispatchEvent(new Event('change')); fixture.detectChanges();
      expect(q('.never-warning')).toBeTruthy();
      click('button.submit');
      r = http.expectOne(BASE);
      expect(r.request.body.expiresAt).toBeNull();
    });

    it('shows the one-time key, stores nothing, clears on close and refreshes list', () => {
      const { q, type, click, http, fixture, root } = openForm();
      type('input.label', 'ci');
      click('input.scope-mes');
      click('button.submit');
      http.expectOne(BASE).flush({ ...key({ label: 'ci' }), fullKey: 'uak_SECRET' });
      fixture.detectChanges();
      expect(q('.full-key').textContent).toContain('uak_SECRET');
      expect(q('.dialog-note').textContent).toContain('無法再看到');
      expect(JSON.stringify(localStorage)).not.toContain('uak_SECRET');
      expect(JSON.stringify(sessionStorage)).not.toContain('uak_SECRET');
      click('button.close-key');
      expect(root().textContent).not.toContain('uak_SECRET');
      http.expectOne(BASE).flush([key({ label: 'ci' })]);
      fixture.detectChanges();
      expect(root().querySelectorAll('tbody tr').length).toBe(1);
    });

    it('shows UAC error and keeps the form values on failure', () => {
      const { q, type, click, http, fixture } = openForm();
      type('input.label', 'ci');
      click('input.scope-mes');
      click('button.submit');
      http.expectOne(BASE).flush({ error: 'label taken' }, { status: 400, statusText: 'bad' });
      fixture.detectChanges();
      expect(q('.form-error').textContent).toContain('label taken');
      expect(q<HTMLInputElement>('input.label').value).toBe('ci');
      expect(q<HTMLInputElement>('input.scope-mes').checked).toBe(true);
    });
  });
});
