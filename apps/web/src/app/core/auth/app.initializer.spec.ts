import { appInitializer } from './app.initializer';
import { of } from 'rxjs';
import { vi } from 'vitest';
import { AuthService } from './auth.service';
import { ConfigService } from '../config/config.service';
import { EmpInfo } from './auth.models';

describe('appInitializer', () => {
  let authService: {
    init: ReturnType<typeof vi.fn>;
    verifyCertificate: ReturnType<typeof vi.fn>;
    tryAuth: ReturnType<typeof vi.fn>;
    removeURLParameter: ReturnType<typeof vi.fn>;
  };
  let configService: {
    loadConfig: ReturnType<typeof vi.fn>;
  };

  const dummyUser: EmpInfo = {
    no: '1',
    id: 'test',
    name: 'Test',
    department: 'D',
    orgShop: 'S',
    location: 'L',
    shops: [],
    token: 'jwt',
    departmentCode: 'DC',
    refreshToken: 'rt'
  };

  beforeEach(() => {
    configService = {
      loadConfig: vi.fn().mockResolvedValue({
        appTitle: 'template-app',
        authApiUrl: '/uacapi/adAuth',
        mockAuth: false
      })
    };

    authService = {
      init: vi.fn().mockResolvedValue(null),
      verifyCertificate: vi.fn(),
      tryAuth: vi.fn(),
      removeURLParameter: vi.fn().mockImplementation((url: string) => url)
    };
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('should bypass remote auth if mockAuth is enabled and handled by authService.init()', async () => {
    authService.init.mockResolvedValue({ id: 'DEV001', name: 'Mock Dev' } as EmpInfo);

    const initFn = appInitializer(configService as unknown as ConfigService, authService as unknown as AuthService);
    await initFn();

    expect(configService.loadConfig).toHaveBeenCalled();
    expect(authService.init).toHaveBeenCalled();
    expect(authService.verifyCertificate).not.toHaveBeenCalled();
    expect(authService.tryAuth).not.toHaveBeenCalled();
  });

  it('should call verifyCertificate if mockAuth is disabled and CertificateKey is present', async () => {
    const mockLocation = { 
      href: 'http://localhost?CertificateKey=123',
      search: '?CertificateKey=123' 
    } as Location;

    vi.spyOn(window, 'location', 'get').mockReturnValue(mockLocation);
    
    const replaceStateSpy = vi.fn();
    vi.spyOn(window, 'history', 'get').mockReturnValue({
      ...window.history,
      replaceState: replaceStateSpy
    });

    authService.verifyCertificate.mockReturnValue(of(dummyUser));
    
    const initFn = appInitializer(configService as unknown as ConfigService, authService as unknown as AuthService);
    await initFn();

    expect(configService.loadConfig).toHaveBeenCalled();
    expect(authService.init).toHaveBeenCalled();
    expect(authService.verifyCertificate).toHaveBeenCalledWith('123');
    expect(replaceStateSpy).toHaveBeenCalled();
  });

  it('should call tryAuth if mockAuth is disabled and CertificateKey is missing', async () => {
    const mockLocation = { 
      href: 'http://localhost/app',
      search: '' 
    } as Location;

    vi.spyOn(window, 'location', 'get').mockReturnValue(mockLocation);

    authService.tryAuth.mockReturnValue(of(dummyUser));

    const initFn = appInitializer(configService as unknown as ConfigService, authService as unknown as AuthService);
    await initFn();

    expect(configService.loadConfig).toHaveBeenCalled();
    expect(authService.init).toHaveBeenCalled();
    expect(authService.tryAuth).toHaveBeenCalledWith('http://localhost/app', true);
  });
});
