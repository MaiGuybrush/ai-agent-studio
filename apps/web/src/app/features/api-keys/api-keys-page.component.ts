import { Component, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ApiKey, ApiKeysService } from '../../core/api-keys/api-keys.service';

@Component({
  selector: 'app-api-keys-page',
  standalone: true,
  imports: [DatePipe],
  template: `
    <div class="p-4">
      <div class="flex items-center justify-between mb-4">
        <h1 class="text-2xl font-bold">Personal API Key</h1>
        <!-- 建立功能由 #36 接上 -->
        <button class="create px-3 py-1 rounded bg-blue-600 text-white" type="button">建立</button>
      </div>
      @if (error(); as e) {
        <p class="error text-sm text-red-600">{{ e }}</p>
      } @else if (keys(); as list) {
        @if (list.length === 0) {
          <p class="empty text-sm text-slate-500">目前沒有 API Key。</p>
        } @else {
          <table class="w-full text-sm bg-white border border-slate-200 rounded">
            <thead>
              <tr class="text-left text-slate-500">
                <th class="p-2">Label</th><th class="p-2">Key 前綴</th><th class="p-2">Scopes</th>
                <th class="p-2">建立時間</th><th class="p-2">最後使用</th><th class="p-2">到期日</th>
              </tr>
            </thead>
            <tbody>
              @for (k of list; track k.id) {
                <tr class="border-t border-slate-100">
                  <td class="p-2">{{ k.label }}</td>
                  <td class="p-2 font-mono">{{ k.keyPrefix }}</td>
                  <td class="p-2">
                    @for (s of k.scopes; track s) {
                      @if (s === '*') {
                        <span class="badge-all px-2 rounded bg-amber-100 text-amber-800">全部系統</span>
                      } @else {
                        <span class="px-2 mr-1 rounded bg-slate-100">{{ s }}</span>
                      }
                    }
                  </td>
                  <td class="p-2">{{ k.createdAt | date: 'yyyy-MM-dd HH:mm' }}</td>
                  <td class="p-2">{{ k.lastUsedAt ? (k.lastUsedAt | date: 'yyyy-MM-dd HH:mm') : '從未使用' }}</td>
                  <td class="p-2">
                    @if (k.expiresAt) {
                      {{ k.expiresAt | date: 'yyyy-MM-dd' }}
                      @if (isExpired(k)) { <span class="expired text-red-600">已過期</span> }
                    } @else { 不過期 }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        }
      } @else {
        <p class="text-sm text-slate-500">載入中…</p>
      }
    </div>
  `,
})
export class ApiKeysPageComponent {
  private readonly api = inject(ApiKeysService);
  readonly keys = signal<ApiKey[] | null>(null);
  readonly error = signal<string | null>(null);

  constructor() {
    this.api.list().subscribe({
      next: (l) => this.keys.set(l),
      error: () => this.error.set('無法載入 API Key 列表，請稍後再試。'),
    });
  }

  isExpired(k: ApiKey): boolean {
    return !!k.expiresAt && new Date(k.expiresAt).getTime() < Date.now();
  }
}
