import { Injectable, inject, signal, computed } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom, catchError, of } from 'rxjs';
import { RuntimeConfig } from './config.models';

const DEFAULT_CONFIG: RuntimeConfig = {
  appTitle: 'template-app',
  authApiUrl: '/uacapi/adAuth',
  mockAuth: false,
};

@Injectable({
  providedIn: 'root',
})
export class ConfigService {
  private readonly http = inject(HttpClient);
  private readonly _config = signal<RuntimeConfig>(DEFAULT_CONFIG);

  readonly config = computed(() => this._config());
  readonly appTitle = computed(() => this._config().appTitle);
  readonly authApiUrl = computed(() => this._config().authApiUrl);
  readonly isMockAuth = computed(() => !!this._config().mockAuth);
  readonly mockUser = computed(() => this._config().mockUser);
  readonly modulesInfo = computed(() => this._config().modulesInfo);

  normalizeUrl(url: string): string {
    if (!url) return '';
    return url.trim().replace(/\/+$/, '');
  }

  async loadConfig(): Promise<RuntimeConfig> {
    const config = await firstValueFrom(
      this.http.get<RuntimeConfig>('assets/config.json').pipe(
        catchError((err) => {
          console.warn('Failed to load assets/config.json, falling back to defaults:', err);
          return of(DEFAULT_CONFIG);
        }),
      ),
    );

    const normalizedConfig: RuntimeConfig = {
      ...config,
      authApiUrl: this.normalizeUrl(config.authApiUrl),
    };

    this._config.set(normalizedConfig);
    return normalizedConfig;
  }
}
