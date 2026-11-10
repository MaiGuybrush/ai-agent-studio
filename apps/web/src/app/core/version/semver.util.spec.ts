import { describe, it, expect } from 'vitest';
import { compareSemver } from './semver.util';

describe('compareSemver', () => {
  it('should return 0 when versions are identical', () => {
    expect(compareSemver('1.0.0', '1.0.0')).toBe(0);
    expect(compareSemver('2.1.3', '2.1.3')).toBe(0);
  });

  it('should return > 0 when first version is newer (major, minor, or patch)', () => {
    expect(compareSemver('2.0.0', '1.0.0')).toBeGreaterThan(0);
    expect(compareSemver('1.2.0', '1.1.0')).toBeGreaterThan(0);
    expect(compareSemver('1.0.1', '1.0.0')).toBeGreaterThan(0);
  });

  it('should return < 0 when first version is older', () => {
    expect(compareSemver('1.0.0', '2.0.0')).toBeLessThan(0);
    expect(compareSemver('1.1.0', '1.2.0')).toBeLessThan(0);
    expect(compareSemver('1.0.0', '1.0.1')).toBeLessThan(0);
  });

  it('should handle prefix v or V gracefully', () => {
    expect(compareSemver('v1.2.0', '1.1.0')).toBeGreaterThan(0);
    expect(compareSemver('V1.0.0', 'v1.0.0')).toBe(0);
  });

  it('should treat any version as greater than empty/null/undefined', () => {
    expect(compareSemver('1.0.0', '')).toBeGreaterThan(0);
    expect(compareSemver('', '1.0.0')).toBeLessThan(0);
    expect(compareSemver('', '')).toBe(0);
  });

  it('should fix the legacy bug: compareSemver > 0 correctly catches minor/patch releases', () => {
    const lastRead = '1.0.0';
    const versions = ['1.2.0', '1.1.0', '1.0.0'];
    const unread = versions.filter((v) => compareSemver(v, lastRead) > 0);
    expect(unread).toEqual(['1.2.0', '1.1.0']);
  });
});
