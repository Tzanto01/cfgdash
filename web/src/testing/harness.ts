import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting, TestRequest } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter, Router, withComponentInputBinding } from '@angular/router';
import { accessInterceptor } from '../app/access';
import { App } from '../app/app';
import { routes } from '../app/app.routes';
import { Snapshot } from '../app/models';
import { makeSnapshot } from './fixtures';

export interface AppHarness {
  fixture: ComponentFixture<App>;
  http: HttpTestingController;
  router: Router;
  el: HTMLElement;
  settle(): Promise<void>;
  /** Answers the pending request for `path` (default: success with `body`). */
  answer(path: string, body: object, status?: number): Promise<TestRequest>;
  navigate(url: string): Promise<void>;
  text(): string;
  buttons(name: RegExp | string): HTMLButtonElement[];
  button(name: RegExp | string): HTMLButtonElement;
  click(element: Element): Promise<void>;
}

function matches(text: string, name: RegExp | string): boolean {
  const value = text.replace(/\s+/g, ' ').trim();
  return typeof name === 'string' ? value === name : name.test(value);
}

/** Boots the real App with the real routes at `url`; only HTTP is replaced. The snapshot is answered unless `snapshot` is null. */
export async function startApp(url: string, snapshot: Snapshot | null = makeSnapshot()): Promise<AppHarness> {
  TestBed.configureTestingModule({
    providers: [
      provideRouter(routes, withComponentInputBinding()),
      provideHttpClient(withInterceptors([accessInterceptor])),
      provideHttpClientTesting()
    ]
  });
  const fixture = TestBed.createComponent(App);
  const router = TestBed.inject(Router);
  const http = TestBed.inject(HttpTestingController);
  const el = fixture.nativeElement as HTMLElement;

  const settle = async (): Promise<void> => {
    for (let i = 0; i < 3; i++) {
      await fixture.whenStable();
    }
  };
  const answer = async (path: string, body: object, status = 200): Promise<TestRequest> => {
    await settle();
    const req = http.expectOne((r) => r.url === path);
    if (status === 200) {
      req.flush(body);
    } else {
      req.flush(body, { status, statusText: String(status) });
    }
    await settle();
    return req;
  };
  const harness: AppHarness = {
    fixture,
    http,
    router,
    el,
    settle,
    answer,
    navigate: async (target) => {
      await router.navigateByUrl(target);
      await settle();
    },
    text: () => (el.textContent ?? '').replace(/\s+/g, ' '),
    buttons: (name) => [...el.querySelectorAll('button')].filter((b) => matches(b.getAttribute('aria-label') ?? b.textContent ?? '', name)),
    button: (name) => {
      const found = harness.buttons(name);
      if (found.length !== 1) {
        throw new Error(`Expected one button ${String(name)}, found ${found.length}`);
      }
      return found[0];
    },
    click: async (element) => {
      (element as HTMLElement).click();
      await settle();
    }
  };

  await router.navigateByUrl(url);
  await settle();
  if (snapshot) {
    await answer('/api/v1/snapshot', snapshot);
  }
  return harness;
}
