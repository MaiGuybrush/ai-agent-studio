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
});
