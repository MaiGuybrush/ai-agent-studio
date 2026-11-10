import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth.guard';
import { DashboardComponent } from './features/dashboard/dashboard.component';
import { TicketListComponent } from './features/tickets/ticket-list.component';
import { TicketCreateComponent } from './features/tickets/ticket-create.component';
import { TicketDetailComponent } from './features/tickets/ticket-detail.component';
import { InterviewPageComponent } from './features/interview/interview-page.component';
import { ReviewPendingComponent } from './features/reviews/review-pending.component';
import { ReviewPageComponent } from './features/reviews/review-page.component';

export const routes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    redirectTo: 'dashboard'
  },
  {
    path: 'dashboard',
    component: DashboardComponent,
    canActivate: [authGuard]
  },
  { path: 'tickets', component: TicketListComponent, canActivate: [authGuard] },
  { path: 'tickets/new', component: TicketCreateComponent, canActivate: [authGuard] },
  { path: 'tickets/:id', component: TicketDetailComponent, canActivate: [authGuard] },
  { path: 'tickets/:id/interview', component: InterviewPageComponent, canActivate: [authGuard] },
  { path: 'reviews', component: ReviewPendingComponent, canActivate: [authGuard] },
  { path: 'reviews/:ticketId/:specId', component: ReviewPageComponent, canActivate: [authGuard] },
  {
    path: '**',
    redirectTo: 'dashboard'
  }
];
