import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { VersionInfoService } from './version-info.service';
import { VERSION_INFO, VersionInfo, DEFAULT_READ_VERSION_SAVE_KEY } from './version.models';
import { beforeEach, afterEach, describe, it, expect } from 'vitest';

describe('VersionInfoService', () => {
  let service: VersionInfoService;
  let httpTesting: HttpTestingController;
  let mockVersionInfo: VersionInfo;

  beforeEach(() => {
    localStorage.clear();

    mockVersionInfo = {
      versions: ['1.0.0'],
      readCompleteSaveKey: 'test-app-readVersionInfo'
    };

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        VersionInfoService,
        { provide: VERSION_INFO, useValue: mockVersionInfo }
      ]
    });

    service = TestBed.inject(VersionInfoService);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpTesting.verify();
    localStorage.clear();
  });

  it('should initialize with dialog closed and empty display lists', () => {
    expect(service.isOpen()).toBe(false);
    expect(service.displayVersions()).toEqual([]);
    expect(service.unreadVersions()).toEqual([]);
  });

  it('should open dialog and fetch release note on showVersionInfoDialog(false) when no prior version in localStorage', () => {
    service.showVersionInfoDialog(false);

    expect(service.isOpen()).toBe(true);
    expect(service.displayVersions()).toEqual(['1.0.0']);
    expect(service.unreadVersions()).toEqual(['1.0.0']);

    const req = httpTesting.expectOne('./assets/versions/1.0.0/version-1.0.0.md');
    expect(req.request.method).toBe('GET');
    req.flush('# 1.0.0 Release Notes\n- Feature A\n<script>alert("xss")</script>');

    expect(service.versionContents()['1.0.0']).toContain('Feature A');
    expect(service.versionContents()['1.0.0']).not.toContain('<script>');
  });

  it('should not open dialog on showVersionInfoDialog(false) when already up to date', () => {
    localStorage.setItem('test-app-readVersionInfo', '1.0.0');

    service.showVersionInfoDialog(false);

    expect(service.isOpen()).toBe(false);
    expect(service.displayVersions()).toEqual([]);
    httpTesting.expectNone('./assets/versions/1.0.0/version-1.0.0.md');
  });

  it('should filter only newer versions when upgrading from 1.0.0 to 1.2.0', () => {
    mockVersionInfo.versions = ['1.2.0', '1.1.0', '1.0.0'];
    localStorage.setItem('test-app-readVersionInfo', '1.0.0');

    service.showVersionInfoDialog(false);

    expect(service.isOpen()).toBe(true);
    expect(service.displayVersions()).toEqual(['1.2.0', '1.1.0']);
    expect(service.unreadVersions()).toEqual(['1.2.0', '1.1.0']);

    const req1 = httpTesting.expectOne('./assets/versions/1.2.0/version-1.2.0.md');
    const req2 = httpTesting.expectOne('./assets/versions/1.1.0/version-1.1.0.md');
    httpTesting.expectNone('./assets/versions/1.0.0/version-1.0.0.md');

    req1.flush('## 1.2.0\n- Bugfix');
    req2.flush('## 1.1.0\n- New feature');

    expect(service.versionContents()['1.2.0']).toContain('Bugfix');
    expect(service.versionContents()['1.1.0']).toContain('New feature');
  });

  it('should display all versions on showVersionInfoDialog(true) even when already read', () => {
    mockVersionInfo.versions = ['1.2.0', '1.1.0', '1.0.0'];
    localStorage.setItem('test-app-readVersionInfo', '1.2.0');

    service.showVersionInfoDialog(true);

    expect(service.isOpen()).toBe(true);
    expect(service.displayVersions()).toEqual(['1.2.0', '1.1.0', '1.0.0']);

    httpTesting.expectOne('./assets/versions/1.2.0/version-1.2.0.md').flush('v1.2.0');
    httpTesting.expectOne('./assets/versions/1.1.0/version-1.1.0.md').flush('v1.1.0');
    httpTesting.expectOne('./assets/versions/1.0.0/version-1.0.0.md').flush('v1.0.0');
  });

  it('should save latest version to localStorage under configured key on acknowledgeVersionInfo()', () => {
    mockVersionInfo.versions = ['1.2.0', '1.1.0', '1.0.0'];
    service.showVersionInfoDialog(true);
    expect(service.isOpen()).toBe(true);

    service.acknowledgeVersionInfo();

    expect(service.isOpen()).toBe(false);
    expect(localStorage.getItem('test-app-readVersionInfo')).toBe('1.2.0');

    // Clean up pending requests
    httpTesting.match(() => true).forEach((req) => req.flush(''));
  });

  it('should fallback to DEFAULT_READ_VERSION_SAVE_KEY when readCompleteSaveKey is empty', () => {
    mockVersionInfo.readCompleteSaveKey = '';
    mockVersionInfo.versions = ['1.0.0'];

    service.showVersionInfoDialog(false);
    expect(service.isOpen()).toBe(true);

    service.acknowledgeVersionInfo();
    expect(localStorage.getItem(DEFAULT_READ_VERSION_SAVE_KEY)).toBe('1.0.0');

    httpTesting.match(() => true).forEach((req) => req.flush(''));
  });

  it('should dynamically update storage key on setUserContext() to support shared workstation employee isolation', () => {
    service.setUserContext('USER_A', 'myApp');
    expect(mockVersionInfo.readCompleteSaveKey).toBe('USER_A-myApp-readVersionInfo');

    service.setUserContext('USER_B', 'myApp');
    expect(mockVersionInfo.readCompleteSaveKey).toBe('USER_B-myApp-readVersionInfo');
  });

  it('should close dialog without saving on closeDialog()', () => {
    service.showVersionInfoDialog(false);
    expect(service.isOpen()).toBe(true);

    service.closeDialog();

    expect(service.isOpen()).toBe(false);
    expect(localStorage.getItem('test-app-readVersionInfo')).toBeNull();

    httpTesting.match(() => true).forEach((req) => req.flush(''));
  });
});
