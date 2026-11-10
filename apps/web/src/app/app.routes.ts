import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth.guard';
import { DashboardComponent } from './features/dashboard/dashboard.component';

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
  {
    path: '**',
    redirectTo: 'dashboard'
  }
];
