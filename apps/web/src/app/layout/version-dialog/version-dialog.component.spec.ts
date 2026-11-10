import { ComponentFixture, TestBed } from '@angular/core/testing';
import { VersionDialogComponent } from './version-dialog.component';
import { VersionInfoService } from '../../core/version/version-info.service';
import { signal } from '@angular/core';
import { vi, describe, it, expect, beforeEach } from 'vitest';

describe('VersionDialogComponent', () => {
  let fixture: ComponentFixture<VersionDialogComponent>;
  let mockVersionInfoService: {
    isOpen: ReturnType<typeof signal<boolean>>;
    displayVersions: ReturnType<typeof signal<string[]>>;
    unreadVersions: ReturnType<typeof signal<string[]>>;
    versionContents: ReturnType<typeof signal<Record<string, string>>>;
    isLoading: ReturnType<typeof signal<boolean>>;
    acknowledgeVersionInfo: ReturnType<typeof vi.fn>;
    closeDialog: ReturnType<typeof vi.fn>;
  };

  beforeEach(async () => {
    mockVersionInfoService = {
      isOpen: signal(true),
      displayVersions: signal(['1.1.0', '1.0.0']),
      unreadVersions: signal(['1.1.0']),
      versionContents: signal({
        '1.1.0': '<p>Release 1.1.0 content</p>',
        '1.0.0': '<p>Release 1.0.0 content</p>',
      }),
      isLoading: signal(false),
      acknowledgeVersionInfo: vi.fn(),
      closeDialog: vi.fn(),
    };

    await TestBed.configureTestingModule({
      imports: [VersionDialogComponent],
      providers: [{ provide: VersionInfoService, useValue: mockVersionInfoService }],
    }).compileComponents();

    fixture = TestBed.createComponent(VersionDialogComponent);
    fixture.detectChanges();
  });

  it('should render dialog when isOpen is true', () => {
    const dialog = fixture.nativeElement.querySelector('.version-modal-dialog');
    expect(dialog).toBeTruthy();
  });

  it('should render cards for all displayVersions', () => {
    const cards = fixture.nativeElement.querySelectorAll('.timeline-card');
    expect(cards.length).toBe(2);
    expect(fixture.nativeElement.textContent).toContain('Release 1.1.0 content');
    expect(fixture.nativeElement.textContent).toContain('Release 1.0.0 content');
  });

  it('should render NEW badge only for unread versions', () => {
    const cards = fixture.nativeElement.querySelectorAll('.timeline-card');
    const firstCardBadge = cards[0].querySelector('.badge-new');
    const secondCardBadge = cards[1].querySelector('.badge-new');

    expect(firstCardBadge).toBeTruthy();
    expect(firstCardBadge.textContent).toContain('NEW');
    expect(secondCardBadge).toBeNull();
  });

  it('should invoke acknowledgeVersionInfo when "我知道了" is clicked', () => {
    const confirmBtn = fixture.nativeElement.querySelector('.btn-confirm');
    expect(confirmBtn).toBeTruthy();
    expect(confirmBtn.textContent).toContain('我知道了');

    confirmBtn.click();
    expect(mockVersionInfoService.acknowledgeVersionInfo).toHaveBeenCalled();
  });

  it('should invoke closeDialog when close button is clicked', () => {
    const closeBtn = fixture.nativeElement.querySelector('.btn-close');
    expect(closeBtn).toBeTruthy();

    closeBtn.click();
    expect(mockVersionInfoService.closeDialog).toHaveBeenCalled();
  });
});
