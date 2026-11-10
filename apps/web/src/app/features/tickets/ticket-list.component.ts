import { Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Ticket, TicketService, errorMessage } from '../../core/tickets/ticket.service';

@Component({
  selector: 'app-ticket-list',
  standalone: true,
  imports: [RouterLink],
  template: `
    <div class="p-4">
      <div class="flex items-center justify-between mb-4">
        <h1 class="text-2xl font-bold">需求單</h1>
        <a class="create px-3 py-1 rounded bg-blue-600 text-white" routerLink="/tickets/new">建立需求單</a>
      </div>
      @if (error(); as e) {
        <p class="error text-sm text-red-600">{{ e }}</p>
      }
      <table class="w-full text-sm bg-white border border-slate-200 rounded">
        <thead>
          <tr class="text-left text-slate-500">
            <th class="p-2">單號</th><th class="p-2">需求描述</th><th class="p-2">狀態</th>
            <th class="p-2">CIM Engineer</th><th class="p-2">Business User</th>
          </tr>
        </thead>
        <tbody>
          @for (t of tickets(); track t.id) {
            <tr class="border-t border-slate-100">
              <td class="p-2"><a class="text-blue-700" [routerLink]="['/tickets', t.id]">{{ t.id }}</a></td>
              <td class="p-2">{{ t.description }}</td>
              <td class="p-2">{{ t.status }}</td>
              <td class="p-2">{{ t.cimEngineerName }}</td>
              <td class="p-2">{{ t.businessUserName }}</td>
            </tr>
          }
        </tbody>
      </table>
    </div>
  `,
})
export class TicketListComponent {
  private readonly svc = inject(TicketService);
  readonly tickets = signal<Ticket[]>([]);
  readonly error = signal<string | null>(null);

  constructor() {
    this.svc.list().then(
      (l) => this.tickets.set(l),
      (e) => this.error.set(errorMessage(e, '無法載入需求單。')),
    );
  }
}
