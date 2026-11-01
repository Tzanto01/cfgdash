import { Button } from '../components/ui/button';
import type { AccessProblem } from '../lib/access';

/** Full-page panel shown instead of the shell while an access problem is set. */
export function AccessPanel({ problem, onRetry }: { problem: AccessProblem; onRetry(): void }) {
  return (
    <main id="content" tabIndex={-1} className="flex min-h-screen items-center justify-center p-6">
      <div className="w-full max-w-lg space-y-3 rounded-xl border border-border bg-surface p-6">
        <p className="flex items-center gap-2 font-semibold">
          <img src="/favicon.svg" alt="" className="size-5" />
          cfgdash
        </p>
        {problem.kind === 'signed-out' && (
          <>
            <h1 tabIndex={-1} className="text-2xl leading-[30px] font-semibold">
              Open the link printed in your terminal
            </h1>
            <p>
              Each run of cfgdash prints a new link with a one-time token; old links stop working when cfgdash restarts or stops
              after 30 idle minutes.
            </p>
          </>
        )}
        {problem.kind === 'forbidden' && (
          <>
            <h1 tabIndex={-1} className="text-2xl leading-[30px] font-semibold">
              This device or account isn't allowed
            </h1>
            <p>Over the tailnet, cfgdash only answers the owner's logins and tagged devices of the same tailnet.</p>
            <p className="text-muted">
              Reason: <code className="font-mono">{problem.reason}</code>
            </p>
          </>
        )}
        {problem.kind === 'bad-host' && (
          <>
            <h1 tabIndex={-1} className="text-2xl leading-[30px] font-semibold">
              Use the address cfgdash printed
            </h1>
            <p>Open cfgdash through the address it printed (127.0.0.1 or the tailnet name).</p>
          </>
        )}
        <Button onClick={onRetry}>Try again</Button>
      </div>
    </main>
  );
}
