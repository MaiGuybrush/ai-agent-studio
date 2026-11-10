import { TestBed } from '@angular/core/testing';
import { HttpClient, HTTP_INTERCEPTORS } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { JwtInterceptor } from './jwt.interceptor';
import { AuthService } from './auth.service';
import { provideHttpClient, withInterceptorsFromDi } from '@angular/common/http';
import { of } from 'rxjs';
import { vi } from 'vitest';
import { EmpInfo, SsoUrls } from './auth.models';

describe('JwtInterceptor', () => {
  let httpMock: HttpTestingController;
  let httpClient: HttpClient;
  let authService: {
    token: string;
    refreshToken: ReturnType<typeof vi.fn>;
    logOutObs: ReturnType<typeof vi.fn>;
  };

  beforeEach(() => {
    authService = {
      token: 'test-token',
      refreshToken: vi.fn(),
      logOutObs: vi.fn().mockReturnValue(of({} as SsoUrls))
    };

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptorsFromDi()),
        provideHttpClientTesting(),
        { provide: HTTP_INTERCEPTORS, useClass: JwtInterceptor, multi: true },
        { provide: AuthService, useValue: authService }
      ]
    });

    httpMock = TestBed.inject(HttpTestingController);
    httpClient = TestBed.inject(HttpClient);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('should add Authorization header to normal API requests', () => {
    httpClient.get('/api/data').subscribe();
    
    const req = httpMock.expectOne('/api/data');
    expect(req.request.headers.has('Authorization')).toBe(true);
    expect(req.request.headers.get('Authorization')).toBe('Bearer test-token');
    req.flush({});
  });

  it('should not add header if no token', () => {
    Object.defineProperty(authService, 'token', { get: () => '' });
    httpClient.get('/api/data').subscribe();
    
    const req = httpMock.expectOne('/api/data');
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush({});
  });

  it('should skip Authorization header for assets/config.json and auth endpoints', () => {
    httpClient.get('assets/config.json').subscribe();
    const configReq = httpMock.expectOne('assets/config.json');
    expect(configReq.request.headers.has('Authorization')).toBe(false);
    configReq.flush({});

    httpClient.post('/uacapi/adAuth/RefreshToken', {}).subscribe();
    const refreshReq = httpMock.expectOne('/uacapi/adAuth/RefreshToken');
    expect(refreshReq.request.headers.has('Authorization')).toBe(false);
    refreshReq.flush({});
  });

  it('should attempt refresh on 401', () => {
    authService.refreshToken.mockReturnValue(of({ token: 'new-token' } as EmpInfo));
    
    httpClient.get('/api/data').subscribe(res => {
      expect(res).toBeTruthy();
    });

    const req = httpMock.expectOne('/api/data');
    req.flush('401 error', { status: 401, statusText: 'Unauthorized' });

    expect(authService.refreshToken).toHaveBeenCalled();
    
    const retryReq = httpMock.expectOne('/api/data');
    expect(retryReq.request.headers.get('Authorization')).toBe('Bearer new-token');
    retryReq.flush({ data: 'success' });
  });
});
