import { Component, inject } from '@angular/core';
import { AuthService } from '../../core/auth/auth.service';
import { MeService } from '../../core/identity/me.service';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  template: `
    <div class="dashboard-container">
      <h1 class="text-2xl font-bold mb-4">AI Agent Studio / Dashboard</h1>
      <div class="welcome-card p-6 bg-white rounded-lg shadow-sm border border-slate-200">
        <h2 class="text-lg font-semibold text-slate-800">歡迎回來，{{ currentUser()?.name || me()?.name || '使用者' }}！</h2>
        <p class="text-sm text-slate-500 mt-1">目前登入工號：{{ me()?.empNo || currentUser()?.id }} | 所屬廠區：{{ currentUser()?.orgShop || 'N/A' }}</p>
      </div>
      <div class="identity-card p-6 mt-4 bg-white rounded-lg shadow-sm border border-slate-200">
        @if (me(); as m) {
          <p class="identity-status font-semibold">我是 CIM Engineer：{{ m.isCimEngineer ? '是' : '否' }}</p>
          @if (m.error) {
            <p class="identity-error text-sm text-red-600 mt-1">{{ m.error }}</p>
          }
        } @else if (loadError(); as e) {
          <p class="identity-error text-sm text-red-600">{{ e }}</p>
        } @else {
          <p class="text-sm text-slate-500">身分確認中…</p>
        }
      </div>
    </div>
  `,
  styles: [`
    .dashboard-container {
      padding: 16px;
    }
  `]
})
export class DashboardComponent {
  private authService = inject(AuthService);
  private meService = inject(MeService);
  readonly currentUser = this.authService.currentUser;
  readonly me = this.meService.me;
  readonly loadError = this.meService.loadError;

  constructor() {
    void this.meService.load();
  }
}
