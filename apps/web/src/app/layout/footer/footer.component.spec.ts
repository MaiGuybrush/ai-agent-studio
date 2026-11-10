import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter, Router, NavigationEnd } from '@angular/router';
import { signal, WritableSignal } from '@angular/core';
import { Subject } from 'rxjs';
import { describe, it, expect, beforeEach, vi } from 'vitest';
import { FooterComponent } from './footer.component';
import { VERSION_INFO, VersionInfo } from '../../core/version/version.models';
import { ConfigService } from '../../core/config/config.service';
import { ModuleInfoConfig, RuntimeConfig } from '../../core/config/config.models';

describe('FooterComponent', () => {
  let fixture: ComponentFixture<FooterComponent>;
  let router: Router;
  let configSignal: WritableSignal<RuntimeConfig>;
  let versionInfoValue: VersionInfo;

  beforeEach(async () => {
    versionInfoValue = {
      versions: ['1.0.0'],
      readCompleteSaveKey: '--readVersionInfo',
    };

    const initialModules: ModuleInfoConfig[] = [
      {
        moduleName: 'Dashboard',
        version: '1.0.0',
        owner: 'Local Developer',
        extNo: '12345',
        routePattern: '^/dashboard',
      },
      {
        moduleName: 'Reports',
        version: '1.0.0',
        owner: '李大同',
        extNo: '67890',
        routePattern: '^/reports',
      },
    ];

    configSignal = signal<RuntimeConfig>({
      appTitle: 'MES App',
      authApiUrl: '/uacapi',
      modulesInfo: initialModules,
    });

    const configServiceMock = {
      config: configSignal,
    };

    await TestBed.configureTestingModule({
      imports: [FooterComponent],
      providers: [
        provideRouter([]),
        { provide: VERSION_INFO, useValue: versionInfoValue },
        { provide: ConfigService, useValue: configServiceMock },
      ],
    }).compileComponents();

    router = TestBed.inject(Router);
    vi.spyOn(router, 'url', 'get').mockReturnValue('/dashboard');

    fixture = TestBed.createComponent(FooterComponent);
    fixture.detectChanges();
  });

  it('should render three-column footer with bottom-front, bottom-center, and bottom-end', () => {
    const compiled = fixture.nativeElement as HTMLElement;
    const frontCol = compiled.querySelector('.bottom-front');
    const centerCol = compiled.querySelector('.bottom-center');
    const endCol = compiled.querySelector('.bottom-end');

    expect(frontCol).toBeTruthy();
    expect(centerCol).toBeTruthy();
    expect(endCol).toBeTruthy();

    expect(frontCol?.classList.contains('footer-version')).toBe(true);
    expect(centerCol?.classList.contains('footer-copyright')).toBe(true);
    expect(endCol?.classList.contains('footer-owner')).toBe(true);

    expect(frontCol?.textContent?.trim()).toBe('1.0.0');
    expect(centerCol?.textContent?.trim()).toBe('© 2026 Innolux Inc. All rights reserved.');
    expect(endCol?.textContent?.trim()).toBe('Owner: Local Developer(Ext: 12345)');
  });

  it('should update active module owner when route changes to matching pattern', () => {
    vi.spyOn(router, 'url', 'get').mockReturnValue('/reports');
    (router.events as Subject<unknown>).next(new NavigationEnd(1, '/reports', '/reports'));
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    const endCol = compiled.querySelector('.bottom-end');
    expect(endCol?.textContent?.trim()).toBe('Owner: 李大同(Ext: 67890)');
  });

  it('should fallback to first module entry when route does not match any pattern', () => {
    vi.spyOn(router, 'url', 'get').mockReturnValue('/unknown-page');
    (router.events as Subject<unknown>).next(
      new NavigationEnd(2, '/unknown-page', '/unknown-page'),
    );
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    const endCol = compiled.querySelector('.bottom-end');
    expect(endCol?.textContent?.trim()).toBe('Owner: Local Developer(Ext: 12345)');
  });

  it('should handle undefined or empty modulesInfo gracefully without crashing', () => {
    configSignal.update((c) => ({ ...c, modulesInfo: undefined }));
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    const endCol = compiled.querySelector('.bottom-end');
    expect(endCol?.textContent?.trim()).toBe('');
  });
});
