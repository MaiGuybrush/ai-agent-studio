import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { of } from 'rxjs';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { McpToolsPageComponent } from './mcp-tools-page.component';
import { ConfigService } from '../../core/config/config.service';

const tool = (name: string) => ({
  name, qualifiedName: `gw__${name}`, description: null, inputSchema: {}, outputSchema: null, annotations: null,
});

describe('McpToolsPageComponent deep link', () => {
  function setup(params: Record<string, string>) {
    TestBed.configureTestingModule({
      imports: [McpToolsPageComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(),
        { provide: ConfigService, useValue: { normalizeUrl: (u: string) => u, config: () => ({ apiBaseUrl: 'http://api' }) } },
        { provide: ActivatedRoute, useValue: { paramMap: of(convertToParamMap(params)) } },
      ],
    });
    const fixture = TestBed.createComponent(McpToolsPageComponent);
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    http.expectOne('http://api/api/mcp-tools/servers').flush({ servers: [{ id: 'gw', type: 'http' }] });
    http.expectOne('http://api/api/mcp-tools/servers/gw').flush(
      { id: 'gw', type: 'http', fetchedAt: null, error: null, tools: [tool('get'), tool('put')] });
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('selects the tool named in the URL', () => {
    const root = setup({ server: 'gw', tool: 'put' });
    expect(root.querySelector('.qualified-name')?.textContent).toContain('gw__put');
    expect(root.querySelector('.not-found')).toBeNull();
  });

  it('shows not found for unknown server or tool', () => {
    for (const p of [{ server: 'nope', tool: 'get' }, { server: 'gw', tool: 'nope' }]) {
      TestBed.resetTestingModule();
      const root = setup(p);
      expect(root.querySelector('.not-found')?.textContent).toContain('找不到');
    }
  });

  describe('slow server', () => {
    function setupSlow(params: Record<string, string>) {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        imports: [McpToolsPageComponent],
        providers: [
          provideHttpClient(), provideHttpClientTesting(),
          { provide: ConfigService, useValue: { normalizeUrl: (u: string) => u, config: () => ({ apiBaseUrl: 'http://api' }) } },
          { provide: ActivatedRoute, useValue: { paramMap: of(convertToParamMap(params)) } },
        ],
      });
      const fixture = TestBed.createComponent(McpToolsPageComponent);
      const http = TestBed.inject(HttpTestingController);
      fixture.detectChanges();
      http.expectOne('http://api/api/mcp-tools/servers').flush({ servers: [{ id: 'gw', type: 'http' }] });
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;
      const finish = (tools: ReturnType<typeof tool>[]) => {
        http.expectOne('http://api/api/mcp-tools/servers/gw').flush({ id: 'gw', type: 'http', fetchedAt: null, error: null, tools });
        fixture.detectChanges();
      };
      return { root, finish };
    }

    it('does not show not-found while loading, then selects the tool once it arrives', () => {
      const { root, finish } = setupSlow({ server: 'gw', tool: 'put' });
      expect(root.querySelector('.not-found')).toBeNull();
      finish([tool('put')]);
      expect(root.querySelector('.not-found')).toBeNull();
      expect(root.querySelector('.qualified-name')?.textContent).toContain('gw__put');
    });

    it('shows not-found after the server arrives without the tool', () => {
      const { root, finish } = setupSlow({ server: 'gw', tool: 'nope' });
      expect(root.querySelector('.not-found')).toBeNull();
      finish([tool('get')]);
      expect(root.querySelector('.not-found')).toBeTruthy();
    });
  });
});
