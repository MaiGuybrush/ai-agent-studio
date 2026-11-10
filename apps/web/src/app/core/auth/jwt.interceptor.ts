import { Injectable, inject } from '@angular/core';
import { HttpRequest, HttpHandler, HttpEvent, HttpInterceptor, HttpErrorResponse } from '@angular/common/http';
import { BehaviorSubject, Observable, throwError } from 'rxjs';
import { catchError, filter, switchMap, take } from 'rxjs/operators';
import { AuthService } from './auth.service';
import { EmpInfo } from './auth.models';

const TOKEN_HEADER_KEY = 'Authorization';

@Injectable()
export class JwtInterceptor implements HttpInterceptor {
  private authService = inject(AuthService);
  private isRefreshing = false;
  private refreshTokenSubject: BehaviorSubject<string | null> = new BehaviorSubject<string | null>(null);

  intercept(request: HttpRequest<unknown>, next: HttpHandler): Observable<HttpEvent<unknown>> {
    // Skip public or bootstrap requests
    if (
      request.url.includes('assets/config.json') ||
      request.url.includes('/Auth/CheckAuthorize') ||
      request.url.endsWith('/VerifyCert') ||
      request.url.endsWith('/RefreshToken') ||
      request.url.endsWith('/SsoUrl') ||
      request.url.endsWith('/Logout')
    ) {
      return next.handle(request);
    }

    let authReq = request;
    const token = this.authService.token;
    if (token) {
      authReq = this.addTokenHeader(request, token);
    }

    return next.handle(authReq).pipe(
      catchError(error => {
        if (error instanceof HttpErrorResponse && error.status === 401) {
          return this.handle401Error(authReq, next);
        }
        return throwError(() => error);
      })
    );
  }

  private handle401Error(request: HttpRequest<unknown>, next: HttpHandler): Observable<HttpEvent<unknown>> {
    if (!this.isRefreshing) {
      this.isRefreshing = true;
      this.refreshTokenSubject.next(null);

      return this.authService.refreshToken().pipe(
        switchMap((user: EmpInfo) => {
          this.isRefreshing = false;
          if (user && user.token) {
            this.refreshTokenSubject.next(user.token);
            return next.handle(this.addTokenHeader(request, user.token));
          } else {
            this.refreshTokenSubject.next(null);
            return this.authService.logOutObs().pipe(
              switchMap(() => throwError(() => new Error('Refresh token failed')))
            );
          }
        }),
        catchError((err) => {
          this.isRefreshing = false;
          this.refreshTokenSubject.next(null);
          return this.authService.logOutObs().pipe(
            switchMap(() => throwError(() => err))
          );
        })
      );
    } else {
      return this.refreshTokenSubject.pipe(
        filter(token => token !== null),
        take(1),
        switchMap(token => {
          return next.handle(this.addTokenHeader(request, token!));
        })
      );
    }
  }

  private addTokenHeader(request: HttpRequest<unknown>, token: string) {
    return request.clone({ headers: request.headers.set(TOKEN_HEADER_KEY, 'Bearer ' + token) });
  }
}
