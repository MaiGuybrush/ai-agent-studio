import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from '../config/config.service';

/** UAC GET /api-keys 的項目（只有 POST 回應另含 fullKey）。 */
export interface ApiKey {
  id: string;
  empId: string;
  keyPrefix: string;
  label: string;
  /** `*` 代表全部系統 */
  scopes: string[];
  expiresAt: string | null;
  createdAt: string;
  lastUsedAt: string | null;
}

/** 瀏覽器直連 UAC（JWT 由 JwtInterceptor 加上），不經 C#。#36/#37 在此加 create/revoke/systems。 */
@Injectable({ providedIn: 'root' })
export class ApiKeysService {
  private readonly http = inject(HttpClient);
  private readonly config = inject(ConfigService);

  private get base(): string {
    return `${this.config.authApiUrl()}/api-keys`;
  }

  list(): Observable<ApiKey[]> {
    return this.http.get<ApiKey[]>(this.base);
  }
}
