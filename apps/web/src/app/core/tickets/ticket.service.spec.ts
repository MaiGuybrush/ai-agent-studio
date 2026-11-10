import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TicketService } from './ticket.service';
import { ConfigService } from '../config/config.service';

describe('TicketService', () => {
  let svc: TicketService;
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
    svc = TestBed.inject(TicketService);
    http = TestBed.inject(HttpTestingController);
  });

  it('lists and gets tickets', async () => {
    const l = svc.list();
    http.expectOne('http://api.test/api/tickets').flush([{ id: 'T-1' }]);
    expect((await l).map((t) => t.id)).toEqual(['T-1']);
    const g = svc.get('T 1');
    http.expectOne('http://api.test/api/tickets/T%201').flush({ id: 'T 1' });
    expect((await g).id).toBe('T 1');
  });

  it('creates, cancels, reopens and reassigns with the right payloads', async () => {
    const c = svc.create({ id: 'T-1', description: 'd', businessUserEmpNo: 'B1' });
    let r = http.expectOne('http://api.test/api/tickets');
    expect(r.request.method).toBe('POST');
    expect(r.request.body).toEqual({ id: 'T-1', description: 'd', businessUserEmpNo: 'B1' });
    r.flush({ id: 'T-1' });
    await c;

    const x = svc.cancel('T-1', '不做了');
    r = http.expectOne('http://api.test/api/tickets/T-1/cancel');
    expect(r.request.body).toEqual({ reason: '不做了' });
    r.flush({ id: 'T-1' });
    await x;

    const o = svc.reopen('T-1');
    http.expectOne('http://api.test/api/tickets/T-1/reopen').flush({ id: 'T-1' });
    await o;

    const a = svc.reassign('T-1', 'E2', '調職');
    r = http.expectOne('http://api.test/api/tickets/T-1/reassign');
    expect(r.request.body).toEqual({ newCimEngineerEmpNo: 'E2', reason: '調職' });
    r.flush({ id: 'T-1' });
    await a;
  });
});
