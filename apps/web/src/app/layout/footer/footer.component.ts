import { Component, inject, signal, computed, DestroyRef } from '@angular/core';
import { Router, NavigationEnd } from '@angular/router';
import { filter } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { VERSION_INFO } from '../../core/version/version.models';
import { ConfigService } from '../../core/config/config.service';

@Component({
  selector: 'app-footer',
  standalone: true,
  templateUrl: './footer.component.html',
  styleUrl: './footer.component.css',
})
export class FooterComponent {
  private readonly versionInfo = inject(VERSION_INFO, { optional: true });
  private readonly configService = inject(ConfigService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  private readonly currentUrl = signal<string>(this.router.url || '');

  readonly currentVersion = computed(() => this.versionInfo?.versions?.[0] ?? '');

  readonly activeModule = computed(() => {
    const modules = this.configService.config()?.modulesInfo ?? [];
    if (!modules || modules.length === 0) {
      return null;
    }

    const url = this.currentUrl();
    const matched = modules.find((m) => {
      if (!m.routePattern) {
        return false;
      }
      try {
        return new RegExp(m.routePattern).test(url);
      } catch {
        return false;
      }
    });

    return matched ?? modules[0];
  });

  readonly ownerText = computed(() => {
    const mod = this.activeModule();
    if (!mod || !mod.owner) {
      return '';
    }
    return `Owner: ${mod.owner}(Ext: ${mod.extNo})`;
  });

  constructor() {
    this.router.events
      .pipe(
        filter((event): event is NavigationEnd => event instanceof NavigationEnd),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((event) => {
        this.currentUrl.set(event.urlAfterRedirects || event.url);
      });
  }
}
