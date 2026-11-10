import { Injectable, signal, computed, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, tap, catchError, of, delay } from 'rxjs';
import { EmpInfo, SsoUrls } from './auth.models';
import { ConfigService } from '../config/config.service';

const TOKEN_KEY = "CIM_TK";
const USER_KEY = "CIM_USER";
const REFRESH_KEY = "CIM_RTK";

@Injectable({
  providedIn: 'root'
})
export class AuthService {
  private http = inject(HttpClient);
  private configService = inject(ConfigService);
  
  // Base URL for Auth API from runtime configuration
  get authApiUrl(): string {
    return this.configService.authApiUrl();
  }
  
  // State
  private _currentUser = signal<EmpInfo | null>(null);
  
  // Public signals
  currentUser = computed(() => this._currentUser());
  isAuthenticated = computed(() => !!this._currentUser()?.token);

  constructor() {
    this.loadUserFromStorage();
  }

  get token(): string {
    return localStorage.getItem(TOKEN_KEY) ?? "";
  }

  private loadUserFromStorage() {
    const userJSON = localStorage.getItem(USER_KEY);
    if (userJSON) {
      try {
        const user = JSON.parse(userJSON) as EmpInfo;
        this._currentUser.set(user);
      } catch (e) {
        console.error('Failed to parse user from storage', e);
        this.clearLoginInfo();
      }
    }
  }

  async init(): Promise<EmpInfo | null> {
    if (this.configService.isMockAuth()) {
      const configUser = this.configService.mockUser();
      const syntheticUser: EmpInfo = {
        no: configUser?.no ?? '1',
        id: configUser?.id ?? 'DEV001',
        name: configUser?.name ?? 'Local Developer',
        email: configUser?.email ?? 'dev@innolux.com',
        department: configUser?.department ?? 'CIM',
        orgShop: configUser?.orgShop ?? 'FAB1',
        location: configUser?.location ?? 'Tainan',
        shops: configUser?.shops ?? ['FAB1', 'FAB2'],
        token: configUser?.token ?? 'mock-dev-token',
        departmentCode: configUser?.departmentCode ?? 'CIM01',
        refreshToken: configUser?.refreshToken ?? 'mock-dev-refresh-token'
      };
      this.saveUser(syntheticUser);
      return syntheticUser;
    }
    return null;
  }

  saveUser(user: EmpInfo | null) {
    if (user) {
      localStorage.setItem(USER_KEY, JSON.stringify(user));
      localStorage.setItem(TOKEN_KEY, user.token);
      localStorage.setItem(REFRESH_KEY, user.refreshToken);
      this._currentUser.set(user);
    } else {
      this.clearLoginInfo();
    }
  }

  clearLoginInfo() {
    localStorage.removeItem(USER_KEY);
    localStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(REFRESH_KEY);
    this._currentUser.set(null);
  }

  verifyCertificate(certKey: string): Observable<EmpInfo> {
    return this.http.post<EmpInfo>(`${this.authApiUrl}/VerifyCert`, { CertificateKey: certKey })
      .pipe(tap(res => {
        if (res) {
          this.saveUser(res);
        }
      }));
  }

  refreshToken(): Observable<EmpInfo> {
    const refreshToken = localStorage.getItem(REFRESH_KEY);
    return this.http.post<EmpInfo>(`${this.authApiUrl}/RefreshToken`, { refreshToken })
      .pipe(tap(res => {
        if (res) {
          this.saveUser(res);
        }
      }));
  }

  getSsoUrls(): Observable<SsoUrls> {
    return this.http.get<SsoUrls>(`${this.authApiUrl}/SsoUrl`);
  }

  private resolveAbsoluteRedirectUrl(url: string): string {
    if (url.startsWith('http://') || url.startsWith('https://')) {
      return url;
    }
    const origin = window.location.origin;
    const path = url.startsWith('/') ? url : `/${url}`;
    return `${origin}${path}`;
  }

  logOutObs(): Observable<SsoUrls> {
    this.clearLoginInfo();
    return this.http.get<SsoUrls>(`${this.authApiUrl}/Logout`).pipe(
      tap(urls => {
        const returnUrl = this.removeURLParameter(window.location.href, 'CertificateKey');
        const logoutPage = this.resolveAbsoluteRedirectUrl(urls.logoutPage);
        window.location.href = `${logoutPage}?url=${encodeURIComponent(returnUrl)}`;
      })
    );
  }

  tryAuth(returnUrl: string, autoRedirectSso: boolean): Observable<EmpInfo | null> {
    const refreshToken = localStorage.getItem(REFRESH_KEY);
    
    return this.http.post<EmpInfo>(`${this.authApiUrl}/RefreshToken`, { refreshToken })
      .pipe(
        tap(user => {
          this.saveUser(user);
        }),
        catchError(e => {
          console.log(`tryAuth error:`, e);
          return of(null);
        }),
        tap(user => {
          if (!user && autoRedirectSso) {
            // slight delay then redirect
            this.getSsoUrls().pipe(delay(100)).subscribe(() => {
              this.redirectToSso(returnUrl);
            });
          }
        })
      );
  }

  removeURLParameter(url: string, parameter: string): string {
    try {
      const urlObj = new URL(url, window.location.origin);
      urlObj.searchParams.delete(parameter);
      
      // Also check hash part if using HashLocationStrategy (e.g., /#/?CertificateKey=...)
      if (urlObj.hash.includes('?')) {
        const [hashPath, hashQuery] = urlObj.hash.split('?');
        const hashParams = new URLSearchParams(hashQuery);
        hashParams.delete(parameter);
        const newHashQuery = hashParams.toString();
        urlObj.hash = newHashQuery ? `${hashPath}?${newHashQuery}` : hashPath;
      }
      return urlObj.toString();
    } catch {
      // Fallback string replacement
      const regex = new RegExp(`[?&]${parameter}=[^&#]*(#.*)?$`);
      return url.replace(regex, '$1');
    }
  }

  redirectToSso(returnUrl: string) {
    this.getSsoUrls().subscribe(urls => {
      const cleanReturnUrl = this.removeURLParameter(returnUrl, 'CertificateKey');
      const loginPage = this.resolveAbsoluteRedirectUrl(urls.loginPage);
      window.location.href = `${loginPage}?url=${encodeURIComponent(cleanReturnUrl)}`;
    });
  }

  checkAuthorize(shop: string, id: string, functionId: string, subSystem?: string): Observable<unknown> {
    return this.http.get(
      `${this.authApiUrl}/CheckAuthorize/${id}/${functionId}?subSystem=${subSystem ?? ''}`,
      { observe: 'response' }
    );
  }

  uacAuth(shop: string, id: string, password: string): Observable<EmpInfo> {
    return this.http.post<EmpInfo>(`${this.authApiUrl}/UacAuth`, { id, password })
      .pipe(tap(user => {
        if (user) {
          this.saveUser(user);
        }
      }));
  }
}
