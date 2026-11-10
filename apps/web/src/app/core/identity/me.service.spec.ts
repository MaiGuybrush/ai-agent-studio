import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { MeService } from './me.service';
import { ConfigService } from '../config/config.service';
import { signal } from '@angular/core';

describe('MeService', () => {
  let svc: MeService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ConfigService,
          useValue: {
            config: signal({ apiBaseUrl: 'http://api.test/' }),
            normalizeUrl: (u: string) => u.replace(/\/+$/, ''),
          },
        },
      ],
    });
    svc = TestBed.inject(MeService);
    http = TestBed.inject(HttpTestingController);
  });

  it('loads identity from the backend', async () => {
    const p = svc.load();
    http.expectOne('http://api.test/api/me').flush({
      empNo: 'E1', name: 'A', isCimEngineer: true, deptCode: 'CIM100', error: null,
    });
    await p;
    expect(svc.me()?.isCimEngineer).toBe(true);
    expect(svc.loadError()).toBeNull();
  });

  it('shows an error when the backend fails', async () => {
    const p = svc.load();
    http.expectOne('http://api.test/api/me').flush('x', { status: 500, statusText: 'err' });
    await p;
    expect(svc.me()).toBeNull();
    expect(svc.loadError()).toBeTruthy();
  });
});
