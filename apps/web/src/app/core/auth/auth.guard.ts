import { inject } from '@angular/core';
import { CanActivateFn } from '@angular/router';
import { AuthService } from './auth.service';

export const authGuard: CanActivateFn = () => {
  const authService = inject(AuthService);
  
  if (authService.isAuthenticated()) {
    return true;
  }

  // Redirect to SSO
  authService.redirectToSso(window.location.href);
  return false;
};
