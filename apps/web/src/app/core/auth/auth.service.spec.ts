import { TestBed } from '@angular/core/testing';
import { AuthService } from './auth.service';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { EmpInfo, SsoUrls } from './auth.models';
import { ConfigService } from '../config/config.service';
import { signal, WritableSignal } from '@angular/core';
import { vi } from 'vitest';

describe('AuthService', () => {
  let service: AuthService;
  let httpMock: HttpTestingController;
  let configServiceMock: {
    authApiUrl: WritableSignal<string>;
    isMockAuth: WritableSignal<boolean>;
    mockUser: WritableSignal<Partial<EmpInfo> | null>;
  };

  const defaultApiBaseUrl = 'http://tncimweb1.cminl.oa/uacapi/adAuth';

  beforeEach(() => {
    configServiceMock = {
      authApiUrl: signal(defaultApiBaseUrl),
      isMockAuth: signal(false),
      mockUser: signal<Partial<EmpInfo> | null>(null)
    };

    TestBed.configureTestingModule({
      providers: [
        AuthService,
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ConfigService, useValue: configServiceMock }
      ]
    });
    service = TestBed.inject(AuthService);
    httpMock = TestBed.inject(HttpTestingController);
    
    // Clear localStorage before each test
    localStorage.clear();
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });

  it('should verify certificate with configured absolute URL', () => {
    const mockUser: EmpInfo = {
      no: '123', id: 'test', name: 'Test', department: 'D', orgShop: 'S', 
      location: 'L', shops: [], token: 'jwt', departmentCode: 'DC', refreshToken: 'rt'
    };

    service.verifyCertificate('cert-key').subscribe(user => {
      expect(user).toEqual(mockUser);
      expect(service.currentUser()).toEqual(mockUser);
      expect(localStorage.getItem('CIM_TK')).toBe('jwt');
    });

    const req = httpMock.expectOne(`${defaultApiBaseUrl}/VerifyCert`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ CertificateKey: 'cert-key' });
    req.flush(mockUser);
  });

  it('should support relative path authApiUrl without duplicate slashes', () => {
    configServiceMock.authApiUrl.set('/Apigateway/MesApiCore');

    const mockUser: EmpInfo = {
      no: '123', id: 'test', name: 'Test', department: 'D', orgShop: 'S', 
      location: 'L', shops: [], token: 'jwt', departmentCode: 'DC', refreshToken: 'rt'
    };

    service.verifyCertificate('cert-key').subscribe(user => {
      expect(user).toEqual(mockUser);
    });

    const req = httpMock.expectOne('/Apigateway/MesApiCore/VerifyCert');
    expect(req.request.method).toBe('POST');
    req.flush(mockUser);
  });

  it('should handle mockAuth when enabled without issuing HTTP requests', async () => {
    configServiceMock.isMockAuth.set(true);
    configServiceMock.mockUser.set({
      id: 'DEV001',
      name: 'Local Developer',
      token: 'mock-jwt-token',
      refreshToken: 'mock-refresh-token',
      shops: ['FAB1', 'FAB2']
    });

    const initResult = await service.init();
    expect(initResult).toBeTruthy();
    expect(service.currentUser()?.id).toBe('DEV001');
    expect(service.currentUser()?.name).toBe('Local Developer');
    expect(service.isAuthenticated()).toBe(true);
    expect(localStorage.getItem('CIM_TK')).toBe('mock-jwt-token');

    // No HTTP calls should be pending
    httpMock.expectNone('/VerifyCert');
    httpMock.expectNone('/RefreshToken');
  });

  it('should refresh token', () => {
    const mockUser: EmpInfo = {
      no: '123', id: 'test', name: 'Test', department: 'D', orgShop: 'S', 
      location: 'L', shops: [], token: 'new-jwt', departmentCode: 'DC', refreshToken: 'new-rt'
    };
    
    localStorage.setItem('CIM_RTK', 'old-rt');

    service.refreshToken().subscribe(user => {
      expect(user).toEqual(mockUser);
      expect(localStorage.getItem('CIM_TK')).toBe('new-jwt');
      expect(localStorage.getItem('CIM_RTK')).toBe('new-rt');
    });

    const req = httpMock.expectOne(`${defaultApiBaseUrl}/RefreshToken`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ refreshToken: 'old-rt' });
    req.flush(mockUser);
  });

  it('should check authorize', () => {
    service.checkAuthorize('S1', 'U1', 'F1').subscribe(res => {
      expect(res).toBeTruthy();
    });

    const req = httpMock.expectOne(`${defaultApiBaseUrl}/CheckAuthorize/U1/F1?subSystem=`);
    expect(req.request.method).toBe('GET');
    req.flush({}, { status: 200, statusText: 'OK' });
  });

  it('should handle uac auth', () => {
    const mockUser: EmpInfo = {
      no: '123', id: 'test', name: 'Test', department: 'D', orgShop: 'S', 
      location: 'L', shops: [], token: 'jwt', departmentCode: 'DC', refreshToken: 'rt'
    };

    service.uacAuth('S1', 'U1', 'P1').subscribe(user => {
      expect(user).toEqual(mockUser);
      expect(service.currentUser()).toEqual(mockUser);
    });

    const req = httpMock.expectOne(`${defaultApiBaseUrl}/UacAuth`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ id: 'U1', password: 'P1' });
    req.flush(mockUser);
  });

  it('should redirect to SSO', () => {
    const mockUrls: SsoUrls = { loginPage: 'login-url', logoutPage: 'logout-url' };
    const windowSpy = vi.spyOn(window, 'location', 'get').mockReturnValue({
      ...window.location,
      href: ''
    } as Location);
    
    service.redirectToSso('http://localhost/test');
    
    const req = httpMock.expectOne(`${defaultApiBaseUrl}/SsoUrl`);
    expect(req.request.method).toBe('GET');
    req.flush(mockUrls);
    
    windowSpy.mockRestore();
  });
});
