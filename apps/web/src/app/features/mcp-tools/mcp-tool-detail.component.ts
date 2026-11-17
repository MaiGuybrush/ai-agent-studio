import { Component, computed, input } from '@angular/core';
import { McpTool } from '../../core/mcp-tools/mcp-tools.service';
import { McpSchemaViewComponent } from './mcp-schema-view.component';

/** 右側詳情面板。 */
@Component({
  selector: 'app-mcp-tool-detail',
  standalone: true,
  imports: [McpSchemaViewComponent],
  template: `
    @if (tool(); as t) {
      <div class="flex items-center gap-2">
        <h2 class="qualified-name text-xl font-bold font-mono">{{ t.qualifiedName }}</h2>
        <button type="button" class="copy-name text-xs underline" (click)="copyName(t.qualifiedName)">複製 {{ t.qualifiedName }}</button>
      </div>
      @if (annotations().length) {
        <div class="mt-2 flex flex-wrap gap-1">
          @for (a of annotations(); track a) {
            <span class="annotation px-2 rounded bg-slate-200 text-xs">{{ a }}</span>
          }
        </div>
      }
      <p class="description mt-2 text-sm whitespace-pre-wrap">{{ t.description }}</p>
      <div class="input-schema mt-4"><app-mcp-schema-view title="input schema" [schema]="t.inputSchema" /></div>
      <div class="output-schema mt-4"><app-mcp-schema-view title="output schema" [schema]="t.outputSchema" /></div>
    } @else {
      <p class="placeholder text-sm text-slate-500">請從左側選擇工具。</p>
    }
  `,
})
export class McpToolDetailComponent {
  readonly tool = input<McpTool | null>(null);
  readonly annotations = computed(() =>
    Object.entries(this.tool()?.annotations ?? {})
      .filter(([, v]) => v !== false && v != null)
      .map(([k, v]) => `${k}: ${v}`),
  );

  copyName(name: string) {
    void navigator.clipboard?.writeText(name);
  }
}
