import { Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { TicketService, errorMessage } from '../../core/tickets/ticket.service';

@Component({
  selector: 'app-ticket-create',
  standalone: true,
  template: `
    <form class="p-4 max-w-xl flex flex-col gap-3" (submit)="submit($event)">
      <h1 class="text-2xl font-bold">建立需求單</h1>
      <label>單號
        <input name="id" class="border rounded p-1 w-full" [value]="id()" (input)="id.set(val($event))" />
      </label>
      <label>需求描述（Interview 的開場脈絡）
        <textarea name="description" rows="6" class="border rounded p-1 w-full" [value]="description()" (input)="description.set(val($event))"></textarea>
      </label>
      <label>Business User 工號
        <input name="businessUserEmpNo" class="border rounded p-1 w-full" [value]="bu()" (input)="bu.set(val($event))" />
      </label>
      @if (error(); as e) {
        <p class="error text-sm text-red-600">{{ e }}</p>
      }
      <button type="submit" class="px-3 py-1 rounded bg-blue-600 text-white w-fit" [disabled]="busy()">建立</button>
    </form>
  `,
})
export class TicketCreateComponent {
  private readonly svc = inject(TicketService);
  private readonly router = inject(Router);
  readonly id = signal('');
  readonly description = signal('');
  readonly bu = signal('');
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);

  val(e: Event): string {
    return (e.target as HTMLInputElement).value;
  }

  async submit(e: Event): Promise<void> {
    e.preventDefault();
    this.busy.set(true);
    this.error.set(null);
    try {
      const t = await this.svc.create({
        id: this.id(),
        description: this.description(),
        businessUserEmpNo: this.bu(),
      });
      await this.router.navigate(['/tickets', t.id]);
    } catch (err) {
      this.error.set(errorMessage(err, '建立失敗。'));
    } finally {
      this.busy.set(false);
    }
  }
}
