import { Component, computed, input, signal } from '@angular/core';

interface Row { name: string; type: string; required: boolean; note: string }

type Schema = Record<string, unknown>;
const isObj = (v: unknown): v is Schema => typeof v === 'object' && v !== null && !Array.isArray(v);
const UNSUPPORTED = ['oneOf', 'anyOf', 'allOf', 'not', '$ref'];

/** 攤平成欄位列；遇到表格畫不出來的結構回傳 null（呼叫端退回原始 JSON）。 */
function flatten(schema: Schema, prefix: string, rows: Row[]): boolean {
  if (UNSUPPORTED.some((k) => k in schema)) return false;
  const props = schema['properties'];
  if (props !== undefined && !isObj(props)) return false;
  const required = Array.isArray(schema['required']) ? (schema['required'] as string[]) : [];
  for (const [key, raw] of Object.entries(props ?? {})) {
    if (!isObj(raw) || UNSUPPORTED.some((k) => k in raw)) return false;
    const name = prefix + key;
    const t = raw['type'];
    if (typeof t !== 'string') return false;
    const items = raw['items'];
    if (t === 'array' && !(isObj(items) && typeof items['type'] === 'string')) return false;
    const inner = t === 'array' ? (items as Schema) : null;
    let type = t === 'array' ? `array<${inner!['type']}>` : t;
    if (typeof raw['format'] === 'string') type += ` (${raw['format']})`;
    const desc = typeof raw['description'] === 'string' ? raw['description'] : '';
    const en = Array.isArray(raw['enum']) ? `enum: ${raw['enum'].join(', ')}` : '';
    rows.push({ name, type, required: required.includes(key), note: [desc, en].filter(Boolean).join(' ') });
    if (t === 'object' && !flatten(raw, name + '.', rows)) return false;
    if (inner && inner['type'] === 'object' && !flatten(inner, name + '[].', rows)) return false;
  }
  return true;
}

/** 單一 schema 的欄位表格；可切換原始 JSON 並複製。 */
@Component({
  selector: 'app-mcp-schema-view',
  standalone: true,
  template: `
    <div class="flex items-center gap-2 mb-1">
      <h3 class="font-semibold">{{ title() }}</h3>
      @if (schema() !== null) {
        @if (rows()) {
          <button type="button" class="toggle-raw text-xs underline" (click)="raw.set(!raw())">
            {{ raw() ? '欄位表格' : '原始 JSON' }}
          </button>
        }
        <button type="button" class="copy-json text-xs underline" (click)="copy()">複製 JSON</button>
      }
    </div>
    @if (schema() === null) {
      <p class="text-sm text-slate-500">無 {{ title() }}</p>
    } @else if (rows(); as rs) {
      @if (raw()) {
        <pre class="text-xs bg-slate-100 p-2 overflow-auto">{{ json() }}</pre>
      } @else if (rs.length === 0) {
        <p class="text-sm text-slate-500">無欄位</p>
      } @else {
        <table class="text-sm w-full">
          <thead><tr><th class="text-left">名稱</th><th class="text-left">型別</th><th class="text-left">必填</th><th class="text-left">說明</th></tr></thead>
          <tbody>
            @for (r of rs; track r.name) {
              <tr><td class="font-mono">{{ r.name }}</td><td>{{ r.type }}</td><td>{{ r.required ? '是' : '' }}</td><td>{{ r.note }}</td></tr>
            }
          </tbody>
        </table>
      }
    } @else {
      <pre class="text-xs bg-slate-100 p-2 overflow-auto">{{ json() }}</pre>
    }
  `,
})
export class McpSchemaViewComponent {
  readonly title = input.required<string>();
  readonly schema = input<unknown>(null);
  readonly raw = signal(false);
  readonly json = computed(() => JSON.stringify(this.schema(), null, 2));
  readonly rows = computed<Row[] | null>(() => {
    const s = this.schema();
    if (!isObj(s)) return null;
    const rows: Row[] = [];
    return flatten(s, '', rows) ? rows : null;
  });

  copy() {
    void navigator.clipboard?.writeText(this.json());
  }
}
