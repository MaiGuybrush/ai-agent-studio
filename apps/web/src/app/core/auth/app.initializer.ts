import { AuthService } from './auth.service';
import { ConfigService } from '../config/config.service';
import { lastValueFrom, of } from 'rxjs';
import { catchError, tap } from 'rxjs/operators';

function extractCertificateKey(): string | null {
  // 1. Try search params
  const search = window.location.search || '';
  const searchParams = new URLSearchParams(search);
  const keyFromSearch = searchParams.get('CertificateKey');
  if (keyFromSearch) return keyFromSearch;

  // 2. Try hash query params (for HashLocationStrategy, e.g. /#/?CertificateKey=xxx)
  const hash = window.location.hash || '';
  if (hash.includes('?')) {
    const hashQuery = hash.split('?')[1];
    const hashParams = new URLSearchParams(hashQuery);
    const keyFromHash = hashParams.get('CertificateKey');
    if (keyFromHash) return keyFromHash;
  }

  return null;
}

export function appInitializer(configService: ConfigService, authService: AuthService) {
  return async () => {
    // 1. Load runtime configuration
    await configService.loadConfig();

    // 2. Initialize auth service (handles mockAuth bypass)
    const mockUser = await authService.init();
    if (mockUser) {
      console.log('AppInit: MockAuth initialized for', mockUser.id);
      return mockUser;
    }

    // 3. Parse URL for CertificateKey (from search or hash)
    const certificateKey = extractCertificateKey();
    const currentUrl = window.location.href;

    let authObs$;

    if (certificateKey) {
      console.log('AppInit: Verifying Certificate');
      authObs$ = authService.verifyCertificate(certificateKey);
    } else {
      console.log('AppInit: Trying Auth (Restore Session)');
      authObs$ = authService.tryAuth(currentUrl, true);
    }

    // 4. Execute Auth Logic
    return lastValueFrom(
      authObs$.pipe(
        tap(user => {
          if (user) {
            console.log('AppInit: Auth Success', user.id);
            // Clean URL if CertificateKey was present
            if (certificateKey) {
               const cleanUrl = authService.removeURLParameter(window.location.href, 'CertificateKey');
               window.history.replaceState({}, '', cleanUrl);
            }
          } else {
             console.log('AppInit: Auth Failed or No Session');
          }
        }),
        catchError(err => {
            console.error('AppInit: Error', err);
            return of(null);
        })
      )
    );
  };
}
