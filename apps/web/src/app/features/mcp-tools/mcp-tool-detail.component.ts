import { Component, input } from '@angular/core';
import { McpTool } from '../../core/mcp-tools/mcp-tools.service';

/** 右側詳情面板；獨立元件，#42 在此加 schema 表格、複製鈕、annotations 徽章。 */
@Component({
  selector: 'app-mcp-tool-detail',
  standalone: true,
  template: `
    @if (tool(); as t) {
      <h2 class="qualified-name text-xl font-bold font-mono">{{ t.qualifiedName }}</h2>
      <p class="description mt-2 text-sm whitespace-pre-wrap">{{ t.description }}</p>
    } @else {
      <p class="placeholder text-sm text-slate-500">請從左側選擇工具。</p>
    }
  `,
})
export class McpToolDetailComponent {
  readonly tool = input<McpTool | null>(null);
}
