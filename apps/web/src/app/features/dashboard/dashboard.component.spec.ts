import { ComponentFixture, TestBed } from '@angular/core/testing';
import { DashboardComponent } from './dashboard.component';
import { AuthService } from '../../core/auth/auth.service';
import { Me, MeService } from '../../core/identity/me.service';
import { signal } from '@angular/core';

describe('DashboardComponent', () => {
  let fixture: ComponentFixture<DashboardComponent>;
  const me = signal<Me | null>(null);
  const loadError = signal<string | null>(null);

  beforeEach(async () => {
    me.set(null);
    loadError.set(null);
    await TestBed.configureTestingModule({
      imports: [DashboardComponent],
      providers: [
        { provide: AuthService, useValue: { currentUser: signal({ id: 'EMP888', name: '王小美', orgShop: 'FAB3' }) } },
        { provide: MeService, useValue: { me, loadError, load: () => Promise.resolve() } },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(DashboardComponent);
    fixture.detectChanges();
  });

  const text = (sel: string) => (fixture.nativeElement as HTMLElement).querySelector(sel)?.textContent ?? '';

  it('should render welcome title and user info', () => {
    expect(text('h1')).toContain('Dashboard');
    expect(text('.welcome-card')).toContain('王小美');
    expect(text('.welcome-card')).toContain('EMP888');
  });

  it('shows yes for CIM Engineer', () => {
    me.set({ empNo: 'E1', name: 'A', isCimEngineer: true, deptCode: 'CIM100', error: null });
    fixture.detectChanges();
    expect(text('.identity-status')).toContain('是');
  });

  it('shows no plus the explicit error when dept is unknown (fail closed)', () => {
    me.set({ empNo: 'E1', name: 'A', isCimEngineer: false, deptCode: null, error: '查不到您的部門資訊' });
    fixture.detectChanges();
    expect(text('.identity-status')).toContain('否');
    expect(text('.identity-error')).toContain('查不到您的部門資訊');
  });

  it('shows an error when the backend is unreachable', () => {
    loadError.set('無法取得身分資訊，請稍後再試。');
    fixture.detectChanges();
    expect(text('.identity-error')).toContain('無法取得身分資訊');
  });
});
