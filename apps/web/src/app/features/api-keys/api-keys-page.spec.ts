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
});
