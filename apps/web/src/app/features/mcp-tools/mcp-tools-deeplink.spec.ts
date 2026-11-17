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
    http.expectOne('http://api/api/mcp-tools').flush({
      servers: [{ id: 'gw', type: 'http', error: null, tools: [tool('get'), tool('put')] }],
    });
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
});
