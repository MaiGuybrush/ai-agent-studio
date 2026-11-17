import { TestBed } from '@angular/core/testing';
import { McpToolDetailComponent } from './mcp-tool-detail.component';
import { McpTool } from '../../core/mcp-tools/mcp-tools.service';

const mk = (o: Partial<McpTool> = {}): McpTool => ({
  name: 'get', qualifiedName: 'gw__get', description: null, inputSchema: {}, outputSchema: null, annotations: null, ...o,
});

function render(tool: McpTool) {
  const fixture = TestBed.createComponent(McpToolDetailComponent);
  fixture.componentRef.setInput('tool', tool);
  fixture.detectChanges();
  const root = fixture.nativeElement as HTMLElement;
  return { fixture, root };
}
const cells = (row: Element) => [...row.querySelectorAll('td')].map((c) => c.textContent?.trim());

describe('McpToolDetailComponent schema', () => {
  const schema = {
    type: 'object',
    required: ['lotId'],
    properties: {
      lotId: { type: 'string', description: '批號' },
      mode: { type: 'string', enum: ['A', 'B'] },
      at: { type: 'string', format: 'date-time' },
      pos: { type: 'object', required: ['x'], properties: { x: { type: 'number' } } },
      tags: { type: 'array', items: { type: 'string' } },
      rows: { type: 'array', items: { type: 'object', properties: { id: { type: 'integer' } } } },
    },
  };

  it('renders nested fields, enum and required in the input table', () => {
    const { root } = render(mk({ inputSchema: schema }));
    const rows = [...root.querySelectorAll('.input-schema tbody tr')].map(cells);
    expect(rows).toEqual([
      ['lotId', 'string', '是', '批號'],
      ['mode', 'string', '', 'enum: A, B'],
      ['at', 'string (date-time)', '', ''],
      ['pos', 'object', '', ''],
      ['pos.x', 'number', '是', ''],
      ['tags', 'array<string>', '', ''],
      ['rows', 'array<object>', '', ''],
      ['rows[].id', 'integer', '', ''],
    ]);
  });

  it('falls back to raw JSON for oneOf / $ref without throwing', () => {
    const s = { type: 'object', properties: { a: { oneOf: [{ type: 'string' }, { type: 'number' }] } } };
    const { root } = render(mk({ inputSchema: s, outputSchema: { $ref: '#/defs/x' } }));
    expect(root.querySelector('.input-schema table')).toBeNull();
    expect(root.querySelector('.input-schema pre')?.textContent).toContain('oneOf');
    expect(root.querySelector('.output-schema pre')?.textContent).toContain('$ref');
  });

  it('toggles to raw JSON and copies it', () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    const { fixture, root } = render(mk({ inputSchema: schema }));
    (root.querySelector('.input-schema .toggle-raw') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(root.querySelector('.input-schema table')).toBeNull();
    expect(root.querySelector('.input-schema pre')?.textContent).toContain('"lotId"');
    (root.querySelector('.input-schema .copy-json') as HTMLButtonElement).click();
    expect(writeText).toHaveBeenCalledWith(JSON.stringify(schema, null, 2));
  });

  it('shows 無 output schema when outputSchema is null', () => {
    const { root } = render(mk());
    expect(root.querySelector('.output-schema')?.textContent).toContain('無 output schema');
  });

  it('shows annotation badges only when present', () => {
    const a = render(mk({ annotations: { readOnlyHint: true, destructiveHint: false } }));
    expect([...a.root.querySelectorAll('.annotation')].map((e) => e.textContent?.trim()))
      .toEqual(['readOnlyHint: true', 'destructiveHint: false']);
    const b = render(mk());
    expect(b.root.querySelector('.annotation')).toBeNull();
  });

  it('copies the server__tool name', () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    const { root } = render(mk());
    (root.querySelector('.copy-name') as HTMLButtonElement).click();
    expect(writeText).toHaveBeenCalledWith('gw__get');
  });
});
