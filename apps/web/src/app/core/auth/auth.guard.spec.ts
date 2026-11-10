import { TestBed } from '@angular/core/testing';
import { CanActivateFn, ActivatedRouteSnapshot, RouterStateSnapshot } from '@angular/router';
import { authGuard } from './auth.guard';
import { AuthService } from './auth.service';
import { vi } from 'vitest';

describe('authGuard', () => {
  const executeGuard: CanActivateFn = (...guardParameters) => 
      TestBed.runInInjectionContext(() => authGuard(...guardParameters));

  let authServiceMock: {
    isAuthenticated: ReturnType<typeof vi.fn>;
    redirectToSso: ReturnType<typeof vi.fn>;
  };

  beforeEach(() => {
    authServiceMock = {
      isAuthenticated: vi.fn(),
      redirectToSso: vi.fn()
    };
    
    TestBed.configureTestingModule({
      providers: [
        { provide: AuthService, useValue: authServiceMock }
      ]
    });
  });

  it('should be created', () => {
    expect(executeGuard).toBeTruthy();
  });

  it('should allow access if user is authenticated', () => {
    authServiceMock.isAuthenticated.mockReturnValue(true);
    
    const result = executeGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot);
    
    expect(result).toBe(true);
  });

  it('should redirect if not authenticated', () => {
    authServiceMock.isAuthenticated.mockReturnValue(false);
    
    const result = executeGuard({} as ActivatedRouteSnapshot, { url: '/test' } as RouterStateSnapshot);
    
    expect(result).toBe(false);
    expect(authServiceMock.redirectToSso).toHaveBeenCalled();
  });
});
