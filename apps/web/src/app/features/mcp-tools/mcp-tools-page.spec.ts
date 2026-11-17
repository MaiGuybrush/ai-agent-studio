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
const server = (id: string, tools: McpTool[] = [], error: string | null = null): McpServer => ({ id, type: 'http', error, tools });

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
});
