import { Routes } from '@angular/router';
import { OverviewPage } from './pages/overview-page';
import { SearchPage } from './pages/search-page';
import { ViewPage } from './pages/view-page';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'overview' },
  { path: 'overview', component: OverviewPage, title: 'Overview · cfgdash' },
  { path: 'search', component: SearchPage, title: 'Search · cfgdash' },
  { path: ':view', component: ViewPage, title: 'cfgdash' }
];
