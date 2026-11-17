import { Component, computed, inject, signal } from '@angular/core';
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
        <button class="create px-3 py-1 rounded bg-blue-600 text-white" type="button" (click)="openForm()">建立</button>
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

      @if (formOpen()) {
        <div class="fixed inset-0 bg-black/40 flex items-center justify-center">
          <form class="form bg-white rounded p-4 w-96 space-y-3" (submit)="$event.preventDefault(); submit()">
            <h2 class="font-bold">建立 API Key</h2>
            <label class="block text-sm">Label
              <input class="label border rounded p-1 w-full" [value]="label()" (input)="label.set($any($event.target).value)" />
            </label>
            @if (label().length > 50) { <p class="text-xs text-red-600">Label 上限 50 字</p> }
            <fieldset class="text-sm">
              <legend>Scopes</legend>
              <label class="block"><input type="checkbox" class="scope-all" [checked]="all()" (change)="all.set($any($event.target).checked)" /> 全部系統（*）</label>
              @if (all()) { <p class="risk text-xs text-amber-700">風險提示：此 key 可存取所有系統，請謹慎使用。</p> }
              @for (s of systems(); track s) {
                <label class="block"><input type="checkbox" [class]="'scope-' + s" [disabled]="all()" [checked]="selected().has(s)" (change)="toggle(s, $any($event.target).checked)" /> {{ s }}</label>
              }
            </fieldset>
            <label class="block text-sm">到期日
              <select class="expiry border rounded p-1" [value]="expiry()" (change)="expiry.set($any($event.target).value)">
                <option value="30">30 天</option><option value="90" [selected]="expiry() === '90'">90 天</option><option value="180">180 天</option>
                <option value="custom">自訂日期</option><option value="none">不過期</option>
              </select>
            </label>
            @if (expiry() === 'custom') {
              <input type="date" class="custom-date border rounded p-1" [value]="customDate()" (input)="customDate.set($any($event.target).value)" />
            }
            @if (expiry() === 'none') { <p class="never-warning text-xs text-amber-700">不過期的 key 外洩風險較高，建議設定到期日。</p> }
            @if (formError(); as e) { <p class="form-error text-sm text-red-600">{{ e }}</p> }
            <div class="flex justify-end gap-2">
              <button type="button" class="cancel px-3 py-1" (click)="formOpen.set(false)">取消</button>
              <button type="submit" class="submit px-3 py-1 rounded bg-blue-600 text-white disabled:opacity-50" [disabled]="!valid() || busy()">建立</button>
            </div>
          </form>
        </div>
      }
      @if (createdKey(); as fk) {
        <div class="fixed inset-0 bg-black/40 flex items-center justify-center">
          <div class="bg-white rounded p-4 w-96 space-y-3">
            <h2 class="font-bold">API Key 已建立</h2>
            <code class="full-key block break-all bg-slate-100 p-2">{{ fk }}</code>
            <p class="dialog-note text-sm">請立即複製；關閉後將無法再看到完整 key。</p>
            <div class="flex justify-end gap-2">
              <button type="button" class="copy px-3 py-1" (click)="copy(fk)">複製</button>
              <button type="button" class="close-key px-3 py-1 rounded bg-blue-600 text-white" (click)="closeKey()">關閉</button>
            </div>
          </div>
        </div>
      }
    </div>
  `,
})
export class ApiKeysPageComponent {
  private readonly api = inject(ApiKeysService);
  readonly keys = signal<ApiKey[] | null>(null);
  readonly error = signal<string | null>(null);

  readonly formOpen = signal(false);
  readonly systems = signal<string[]>([]);
  readonly label = signal('');
  readonly selected = signal<ReadonlySet<string>>(new Set());
  readonly all = signal(false);
  readonly expiry = signal('90');
  readonly customDate = signal('');
  readonly formError = signal<string | null>(null);
  readonly busy = signal(false);
  /** 一次性完整 key：只留在記憶體，關閉即清除。 */
  readonly createdKey = signal<string | null>(null);

  readonly valid = computed(
    () =>
      this.label().trim().length > 0 && this.label().length <= 50 &&
      (this.all() || this.selected().size > 0) &&
      (this.expiry() !== 'custom' || !!this.customDate()),
  );

  constructor() {
    this.load();
  }

  private load() {
    this.api.list().subscribe({
      next: (l) => this.keys.set(l),
      error: () => this.error.set('無法載入 API Key 列表，請稍後再試。'),
    });
  }

  openForm() {
    this.label.set(''); this.selected.set(new Set()); this.all.set(false);
    this.expiry.set('90'); this.customDate.set(''); this.formError.set(null);
    this.formOpen.set(true);
    this.api.systems().subscribe({
      next: (s) => this.systems.set(s.filter((x) => x !== '*')),
      error: () => this.formError.set('無法載入系統清單。'),
    });
  }

  toggle(s: string, on: boolean) {
    const n = new Set(this.selected());
    if (on) n.add(s); else n.delete(s);
    this.selected.set(n);
  }

  submit() {
    if (!this.valid() || this.busy()) return;
    const e = this.expiry();
    const expiresAt =
      e === 'none' ? null
      : e === 'custom' ? new Date(this.customDate()).toISOString()
      : new Date(Date.now() + Number(e) * 86400000).toISOString();
    this.busy.set(true);
    this.formError.set(null);
    this.api.create({ label: this.label().trim(), scopes: this.all() ? ['*'] : [...this.selected()], expiresAt }).subscribe({
      next: (r) => { this.busy.set(false); this.formOpen.set(false); this.createdKey.set(r.fullKey); },
      error: (err) => { this.busy.set(false); this.formError.set(err?.error?.error ?? '建立失敗，請稍後再試。'); },
    });
  }

  copy(k: string) {
    void navigator.clipboard?.writeText(k);
  }

  closeKey() {
    this.createdKey.set(null);
    this.load();
  }

  isExpired(k: ApiKey): boolean {
    return !!k.expiresAt && new Date(k.expiresAt).getTime() < Date.now();
  }
}
