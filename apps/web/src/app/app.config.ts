import {
  ApplicationConfig,
  provideBrowserGlobalErrorListeners,
  provideAppInitializer,
  inject,
} from '@angular/core';
import { provideRouter, withHashLocation } from '@angular/router';
import { provideHttpClient, withInterceptorsFromDi, HTTP_INTERCEPTORS } from '@angular/common/http';

import { routes } from './app.routes';
import { ConfigService } from './core/config/config.service';
import { AuthService } from './core/auth/auth.service';
import { appInitializer } from './core/auth/app.initializer';
import { JwtInterceptor } from './core/auth/jwt.interceptor';
import { VERSION_INFO } from './core/version/version.models';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withHashLocation()),
    provideHttpClient(withInterceptorsFromDi()),
    provideAppInitializer(() => {
      const configService = inject(ConfigService);
      const authService = inject(AuthService);
      return appInitializer(configService, authService)();
    }),
    {
      provide: HTTP_INTERCEPTORS,
      useClass: JwtInterceptor,
      multi: true,
    },
    {
      provide: VERSION_INFO,
      useValue: {
        versions: ['1.0.0'],
        readCompleteSaveKey: '',
      },
    },
  ],
};
