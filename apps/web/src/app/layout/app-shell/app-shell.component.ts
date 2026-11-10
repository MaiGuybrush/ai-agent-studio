import { Component, inject, signal, input, effect, ElementRef, HostListener } from '@angular/core';
import { RouterOutlet, RouterLink, RouterLinkActive } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { ConfigService } from '../../core/config/config.service';
import { NavItem } from '../nav.models';
import { VersionInfoService } from '../../core/version/version-info.service';
import { VersionDialogComponent } from '../version-dialog/version-dialog.component';
import { FooterComponent } from '../footer/footer.component';

const DEFAULT_NAV_ITEMS: NavItem[] = [
  {
    label: '儀表板 / Dashboard',
    route: '/dashboard',
    icon: 'dashboard',
  },
  {
    label: '需求單 / Tickets',
    route: '/tickets',
    icon: 'tickets',
  },
];

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, VersionDialogComponent, FooterComponent],
  templateUrl: './app-shell.component.html',
  styleUrl: './app-shell.component.css',
})
export class AppShellComponent {
  private readonly authService = inject(AuthService);
  private readonly configService = inject(ConfigService);
  private readonly versionInfoService = inject(VersionInfoService);
  private readonly elementRef = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly currentUser = this.authService.currentUser;
  readonly appTitle = this.configService.appTitle;

  readonly isSidebarCollapsed = signal<boolean>(false);
  readonly isUserMenuOpen = signal<boolean>(false);

  readonly navItems = input<NavItem[]>(DEFAULT_NAV_ITEMS);

  constructor() {
    effect(() => {
      const user = this.currentUser();
      if (user) {
        const empId = user.no || user.id || '';
        const appTitle = this.configService.appTitle() || '';
        this.versionInfoService.setUserContext(empId, appTitle);
        this.versionInfoService.showVersionInfoDialog(false);
      }
    });
  }

  toggleSidebar(): void {
    this.isSidebarCollapsed.update((val) => !val);
  }

  toggleUserMenu(): void {
    this.isUserMenuOpen.update((val) => !val);
  }

  closeUserMenu(): void {
    this.isUserMenuOpen.set(false);
  }

  openVersionInfo(): void {
    this.closeUserMenu();
    this.versionInfoService.showVersionInfoDialog(true);
  }

  logout(): void {
    this.closeUserMenu();
    this.authService.logOutObs().subscribe();
  }

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    if (!this.isUserMenuOpen()) {
      return;
    }

    const container = this.elementRef.nativeElement.querySelector('.user-menu-container');
    const target = event.target;
    if (container && target instanceof Node && !container.contains(target)) {
      this.closeUserMenu();
    }
  }
}
