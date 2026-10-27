import { ActivatedRouteSnapshot, Routes } from '@angular/router';
import { OverviewPage } from './pages/overview-page';
import { SearchPage } from './pages/search-page';
import { ViewPage } from './pages/view-page';
import { VIEW_DEFINITIONS } from './views';

function viewTitle(route: ActivatedRouteSnapshot): string {
  const label = VIEW_DEFINITIONS.find((v) => v.id === route.paramMap.get('view'))?.label ?? 'Nothing here';
  return `${label} · cfgdash`;
}

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'overview' },
  { path: 'overview', component: OverviewPage, title: 'Overview · cfgdash' },
  { path: 'search', component: SearchPage, title: 'Search · cfgdash' },
  { path: ':view', component: ViewPage, title: viewTitle },
  { path: '**', redirectTo: 'overview' }
];
