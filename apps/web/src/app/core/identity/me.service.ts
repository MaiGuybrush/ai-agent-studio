import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { ConfigService } from '../config/config.service';

/** C# 後端 GET /api/me 的回應。身分與 CIM Engineer 判定一律由後端決定，前端只顯示。 */
export interface Me {
  empNo: string;
  name: string | null;
  isCimEngineer: boolean;
  deptCode: string | null;
  /** 查不到部門時的明確錯誤（此時 isCimEngineer 一定是 false） */
  error: string | null;
}

@Injectable({ providedIn: 'root' })
export class MeService {
  private readonly http = inject(HttpClient);
  private readonly config = inject(ConfigService);

  readonly me = signal<Me | null>(null);
  /** 後端無法連線或回非 2xx 時的訊息 */
  readonly loadError = signal<string | null>(null);

  async load(): Promise<void> {
    const base = this.config.normalizeUrl(String(this.config.config()['apiBaseUrl'] ?? ''));
    try {
      this.me.set(await firstValueFrom(this.http.get<Me>(`${base}/api/me`)));
      this.loadError.set(null);
    } catch {
      this.me.set(null);
      this.loadError.set('無法取得身分資訊，請稍後再試。');
    }
  }
}
