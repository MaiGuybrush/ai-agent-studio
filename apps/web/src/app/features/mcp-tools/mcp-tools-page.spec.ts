import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { McpToolsPageComponent } from './mcp-tools-page.component';
import { ConfigService } from '../../core/config/config.service';
import { McpServer, McpTool } from '../../core/mcp-tools/mcp-tools.service';

const tool = (server: string, name: string, o: Partial<McpTool> = {}): McpTool => ({
  name, qualifiedName: `${server}__${name}`, description: null, inputSchema: {}, outputSchema: null, annotations: null, ...o,
});
const server = (id: string, tools: McpTool[] = [], error: string | null = null, fetchedAt = '2026-01-01T03:07:00Z'): McpServer => ({ id, type: 'http', fetchedAt, error, tools });

describe('McpToolsPageComponent', () => {
  function setup() {
    TestBed.configureTestingModule({
      imports: [McpToolsPageComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        { provide: ConfigService, useValue: { normalizeUrl: (u: string) => u, config: () => ({ apiBaseUrl: 'http://api' }) } },
      ],
    });
    const fixture = TestBed.createComponent(McpToolsPageComponent);
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    const req = http.expectOne('http://api/api/mcp-tools');
    const root = () => fixture.nativeElement as HTMLElement;
    return { fixture, req, root };
  }

  it('shows a loading indicator until the response arrives', () => {
    const { root } = setup();
    expect(root().querySelector('.loading')).toBeTruthy();
  });

  it('lists servers in given order and tools sorted case-insensitively', () => {
    const { fixture, req, root } = setup();
    req.flush({ servers: [server('b', [tool('b', 'zeta'), tool('b', 'Alpha'), tool('b', 'beta')]), server('a', [tool('a', 'x')])] });
    fixture.detectChanges();
    expect([...root().querySelectorAll('.server-title')].map((e) => e.textContent?.trim())).toEqual(['b', 'a']);
    expect([...root().querySelectorAll('.server')[0].querySelectorAll('.tool')].map((e) => e.textContent?.trim()))
      .toEqual(['Alpha', 'beta', 'zeta']);
  });

  it('shows qualified name and plain-text description of the selected tool', () => {
    const { fixture, req, root } = setup();
    req.flush({ servers: [server('gw', [tool('gw', 'get', { description: '第一行\n<b>第二行</b>' })])] });
    fixture.detectChanges();
    (root().querySelector('.tool') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(root().querySelector('.qualified-name')?.textContent).toContain('gw__get');
    const d = root().querySelector('.description') as HTMLElement;
    expect(d.textContent).toContain('第一行\n<b>第二行</b>');
    expect(d.querySelector('b')).toBeNull();
    expect(d.classList).toContain('whitespace-pre-wrap'); // 保留換行（Tailwind 在測試中不載入，故驗 class）
  });

  it('shows an error badge for a failed server while others render normally', () => {
    const { fixture, req, root } = setup();
    req.flush({ servers: [server('down', [], 'timeout'), server('ok', [tool('ok', 't')])] });
    fixture.detectChanges();
    const secs = root().querySelectorAll('.server');
    expect(secs[0].querySelector('.badge-error')?.textContent).toContain('無法連線');
    expect(secs[0].querySelector('.server-error')?.textContent).toContain('timeout');
    expect(secs[1].querySelector('.badge-error')).toBeNull();
    expect(secs[1].querySelectorAll('.tool').length).toBe(1);
  });

  it('shows an empty state when there are no servers', () => {
    const { fixture, req, root } = setup();
    req.flush({ servers: [] });
    fixture.detectChanges();
    expect(root().querySelector('.empty')).toBeTruthy();
  });

  it('shows an explicit error when loading fails', () => {
    const { fixture, req, root } = setup();
    req.flush('x', { status: 500, statusText: 'err' });
    fixture.detectChanges();
    expect(root().querySelector('.error')?.textContent).toContain('無法載入');
  });

  describe('freshness', () => {
    const hhmm = (iso: string) => {
      const d = new Date(iso);
      return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
    };

    it('shows 更新於 HH:mm beside each server title', () => {
      const { fixture, req, root } = setup();
      req.flush({ servers: [server('a', [], null, '2026-01-01T03:07:00Z'), server('b', [], null, '2026-01-01T09:45:00Z')] });
      fixture.detectChanges();
      const times = [...root().querySelectorAll('.server .fetched-at')].map((e) => e.textContent?.trim());
      expect(times).toEqual([`更新於 ${hhmm('2026-01-01T03:07:00Z')}`, `更新於 ${hhmm('2026-01-01T09:45:00Z')}`]);
    });

    it('refresh button refetches with refresh=true, shows busy state, then updates the view and time', () => {
      const { fixture, req, root } = setup();
      const http = TestBed.inject(HttpTestingController);
      req.flush({ servers: [server('a', [tool('a', 'old')], null, '2026-01-01T03:07:00Z')] });
      fixture.detectChanges();
      const btn = root().querySelector('.refresh') as HTMLButtonElement;
      expect(btn.disabled).toBe(false);
      btn.click();
      fixture.detectChanges();
      expect(btn.disabled).toBe(true);
      expect(root().querySelector('.refreshing')).toBeTruthy();
      http.expectOne('http://api/api/mcp-tools?refresh=true')
        .flush({ servers: [server('a', [tool('a', 'new')], null, '2026-01-01T09:45:00Z')] });
      fixture.detectChanges();
      expect(btn.disabled).toBe(false);
      expect(root().querySelector('.refreshing')).toBeNull();
      expect(root().querySelector('.tool')?.textContent?.trim()).toBe('new');
      expect(root().querySelector('.fetched-at')?.textContent).toContain(hhmm('2026-01-01T09:45:00Z'));
    });

    it('keeps the old view when refresh fails', () => {
      const { fixture, req, root } = setup();
      const http = TestBed.inject(HttpTestingController);
      req.flush({ servers: [server('a', [tool('a', 'old')])] });
      fixture.detectChanges();
      (root().querySelector('.refresh') as HTMLButtonElement).click();
      http.expectOne('http://api/api/mcp-tools?refresh=true').flush('x', { status: 500, statusText: 'err' });
      fixture.detectChanges();
      expect(root().querySelector('.tool')?.textContent?.trim()).toBe('old');
      expect(root().querySelector('.refresh-error')).toBeTruthy();
      expect((root().querySelector('.refresh') as HTMLButtonElement).disabled).toBe(false);
    });
  });

  describe('search', () => {
    const load = () => {
      const { fixture, req, root } = setup();
      req.flush({ servers: [
        server('Gateway', [
          tool('Gateway', 'getLot', { description: 'Fetch WIP', inputSchema: { type: 'object', properties: { filter: { type: 'object', properties: { lotId: { type: 'string' } } } } } }),
          tool('Gateway', 'ping', { outputSchema: { properties: { Status: { type: 'string' } } } }),
        ]),
        server('down', [], 'timeout'),
        server('other', [tool('other', 'noop')]),
      ] });
      fixture.detectChanges();
      const search = (q: string) => {
        const el = root().querySelector('input.search') as HTMLInputElement;
        el.value = q;
        el.dispatchEvent(new Event('input'));
        fixture.detectChanges();
      };
      const tools = () => [...root().querySelectorAll('.tool')].map((e) => e.textContent?.trim());
      const servers = () => [...root().querySelectorAll('.server-title')].map((e) => e.textContent?.trim());
      return { search, tools, servers, root };
    };

    it.each([
      ['gateway', ['getLot', 'ping']], // server id，不分大小寫
      ['GETL', ['getLot']], // tool name 子字串
      ['wip', ['getLot']], // description
      ['lotid', ['getLot']], // 巢狀 input 欄位
      ['status', ['ping']], // output 欄位
    ])('matches %s', (q, expected) => {
      const { search, tools } = load();
      search(q);
      expect(tools()).toEqual(expected);
    });

    it('collapses servers without matching tools, including failed ones', () => {
      const { search, servers } = load();
      search('lotId');
      expect(servers()).toEqual(['Gateway']);
    });

    it('shows no-result message and restores on clear', () => {
      const { search, tools, servers, root } = load();
      search('zzz');
      expect(root().querySelector('.no-results')?.textContent).toContain('zzz');
      search('');
      expect(root().querySelector('.no-results')).toBeNull();
      expect(tools().length).toBe(3);
      expect(servers()).toEqual(['Gateway', 'down', 'other']);
    });
  });
});
