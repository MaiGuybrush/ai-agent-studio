import { Component, inject } from '@angular/core';
import { VersionInfoService } from '../../core/version/version-info.service';

@Component({
  selector: 'app-version-dialog',
  standalone: true,
  templateUrl: './version-dialog.component.html',
  styleUrl: './version-dialog.component.css',
})
export class VersionDialogComponent {
  readonly versionInfoService = inject(VersionInfoService);

  readonly isOpen = this.versionInfoService.isOpen;
  readonly displayVersions = this.versionInfoService.displayVersions;
  readonly unreadVersions = this.versionInfoService.unreadVersions;
  readonly versionContents = this.versionInfoService.versionContents;

  isUnread(version: string): boolean {
    return this.unreadVersions().includes(version);
  }

  getContent(version: string): string {
    return this.versionContents()[version] || '<div class="loading-state">載入說明中...</div>';
  }

  acknowledge(): void {
    this.versionInfoService.acknowledgeVersionInfo();
  }

  close(): void {
    this.versionInfoService.closeDialog();
  }
}
