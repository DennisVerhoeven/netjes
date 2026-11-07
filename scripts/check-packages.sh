#!/usr/bin/env bash
# Packs the NuGet and npm packages into a local feed, scaffolds an app with `netjes new --packages` that restores
# from that feed only, then runs its Vitest and C# tests, type-checks it (also under create-vite's React-TS flags),
# builds and runs it in Production, and runs its Playwright suite. Run from the repository root; the scaffold goes in a
# temporary folder, outside this repository's global.json, whose test runner the app's test project does not use.
# On Linux outside CI, install Chromium's system libraries once first: `npx playwright install-deps chromium`.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
port="${CHECK_PACKAGES_PORT:-5399}"
# The ports of the app's Playwright suite, which it reads as E2E_DEV_PORT and the rest. Names of their own, so that an
# environment that moves the examples' suites does not put this check on the same ports; by default they stay clear of
# the examples' ports.
e2e_dev_port="${CHECK_PACKAGES_E2E_DEV_PORT:-5396}"
e2e_prod_port="${CHECK_PACKAGES_E2E_PROD_PORT:-5397}"
e2e_vite_port="${CHECK_PACKAGES_E2E_VITE_PORT:-5398}"
app_pid=""
# Stops the published app this script started, by its PID: on every platform, and nothing else that listens on a port.
# Playwright stops the servers it starts itself, and the app's Node sidecar exits with the app.
stop_app() {
  [[ -n "$app_pid" ]] || return 0
  kill "$app_pid" 2>/dev/null || return 0
  for _ in $(seq 1 30); do
    kill -0 "$app_pid" 2>/dev/null || return 0
    sleep 1
  done
  kill -9 "$app_pid" 2>/dev/null || true
}
trap 'stop_app; rm -rf "$work"' EXIT
feed="$work/feed"
# Its own NuGet cache: a package rebuilt under the same version must not be served from an earlier run.
export NUGET_PACKAGES="$work/nuget-packages"
version="$(node -p "require('$root/packages/netjes/package.json').version")"

echo "== pack $version"
dotnet pack "$root/src/Netjes/Netjes.csproj" -c Release -o "$feed" --nologo -v quiet
dotnet pack "$root/src/Netjes.Images.SkiaSharp/Netjes.Images.SkiaSharp.csproj" -c Release -o "$feed" --nologo -v quiet
(cd "$root/packages/netjes" && npm pack --silent --pack-destination "$feed" >/dev/null)
# nuget.org shows each package's icon: the Netjes mark, at the package root, named in its .nuspec.
for package in Netjes Netjes.Images.SkiaSharp; do
  nupkg="$feed/$package.$version.nupkg"
  unzip -Z1 "$nupkg" | grep -x "icon.png" >/dev/null || { echo "$package.$version.nupkg has no icon.png at its root"; exit 1; }
  unzip -p "$nupkg" "$package.nuspec" | grep "<icon>icon.png</icon>" >/dev/null || { echo "$package.nuspec names no icon.png"; exit 1; }
done

echo "== scaffold"
node "$root/packages/netjes/bin/netjes.mjs" new "$work/app" --packages --skip-install
cd "$work/app"
# Netjes comes from the local feed only; everything else from nuget.org.
cat > nuget.config <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local"><package pattern="Netjes" /><package pattern="Netjes.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
XML
npm install --no-audit --no-fund --silent "$feed/netjes-$version.tgz"
npm install --engine-strict --no-audit --no-fund --silent

echo "== agent files"
for file in AGENTS.md CLAUDE.md .claude/skills/netjes/SKILL.md .claude/skills/netjes/references/routing.md; do
  [[ -s "$file" ]] || { echo "netjes new did not write $file"; exit 1; }
done
grep -q "^# App$" AGENTS.md || { echo "AGENTS.md is not titled after the app"; exit 1; }
# A new app has both test suites, so its AGENTS.md runs them.
for command in "dotnet test tests/App.Tests" "npm run test:e2e"; do
  grep -q "$command" AGENTS.md || { echo "the new app's AGENTS.md does not run $command"; exit 1; }
done
# The packed CLI adds the same files to an app that netjes new did not make.
mkdir "$work/existing"
touch "$work/existing/Existing.csproj"
npx netjes agents "$work/existing" >/dev/null
for file in AGENTS.md CLAUDE.md; do
  [[ -s "$work/existing/$file" ]] || { echo "netjes agents did not write $file"; exit 1; }
done
diff -r .claude/skills/netjes "$work/existing/.claude/skills/netjes"
# An app with neither suite gets an AGENTS.md that does not send an agent to commands that fail there.
if grep -E "dotnet test|test:e2e" "$work/existing/AGENTS.md"; then echo "an existing app's AGENTS.md runs test suites it does not have"; exit 1; fi
if grep "<!--netjes:" AGENTS.md "$work/existing/AGENTS.md"; then echo "AGENTS.md still has section markers"; exit 1; fi

echo "== Vitest glue"
# netjes() adds a setup file of its own after an app's own setup files: createTestApp() starts from the app's
# netjes({ query }) defaults without their waits, and Testing Library advances Vitest's fake timers. The three files
# are removed before the tests and type checks below, which would pick them up.
cat > vitest.glue.config.ts <<'TS'
import netjes from 'netjes/vite';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  plugins: [netjes({ query: { defaultOptions: { queries: { staleTime: Infinity, retry: 1, retryDelay: 1000 } } } })],
  test: { include: ['glue.test.tsx'], setupFiles: ['./glue-setup.ts'] },
});
TS
cat > glue-setup.ts <<'TS'
// The app's own setup file. With one, netjes() leaves its cleanup file out, so the app lists it.
import 'netjes/testing/setup';
TS
cat > glue.test.tsx <<'TSX'
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createTestApp } from 'netjes/testing';
import { useState } from 'react';
import { afterEach, expect, it, vi } from 'vitest';

const jest = () => (globalThis as { jest?: unknown }).jest;

afterEach(() => {
  vi.useRealTimers();
});

it("starts from the app's query defaults, without their waits", () => {
  expect(createTestApp().queryClient.getDefaultOptions().queries).toMatchObject({ staleTime: Infinity, retry: 1, retryDelay: 0 });
});

it('has a jest global only while the timers are fake', () => {
  expect(typeof jest()).toBe('undefined');
  vi.useFakeTimers();
  expect(typeof jest()).toBe('object');
  vi.useRealTimers();
  expect(typeof jest()).toBe('undefined');
});

it('clicks with user-event under fake timers', async () => {
  vi.useFakeTimers();
  function Counter() {
    const [count, setCount] = useState(0);
    return <button type="button" onClick={() => setCount(count + 1)}>{`Clicked ${count}`}</button>;
  }
  render(<Counter />, { wrapper: createTestApp().Wrapper });
  const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });

  await user.click(screen.getByRole('button', { name: 'Clicked 0' }));

  expect(screen.getByRole('button', { name: 'Clicked 1' })).toBeDefined();
});
TSX
npx vitest run --config vitest.glue.config.ts
rm vitest.glue.config.ts glue-setup.ts glue.test.tsx

echo "== test and typecheck"
npm test --silent
npm run typecheck --silent
# netjes exports declarations, so the app's compiler options apply to the app's own code alone: one that the package's
# sources do not meet, such as noPropertyAccessFromIndexSignature, does not fail the app.
for config in tsconfig.app.json tsconfig.node.json; do
  cat > tsconfig.index-access.json <<JSON
{ "extends": "./$config", "compilerOptions": { "noPropertyAccessFromIndexSignature": true } }
JSON
  npx tsc -p tsconfig.index-access.json
done
rm tsconfig.index-access.json
npx netjes generate --check
# create-vite's React-TS template also sets noUnusedLocals, erasableSyntaxOnly and more, which an app's tsc applies to
# the template's files and the package's declarations. packages/netjes/tsconfig.strict-app.json holds that flag list
# (the package's own typecheck uses it as well); here each of the app's own configs is extended after it, so the app's
# settings still apply.
for config in tsconfig.app.json tsconfig.node.json; do
  cat > tsconfig.strict.json <<JSON
{
  "extends": ["$root/packages/netjes/tsconfig.strict-app.json", "./$config"]
}
JSON
  npx tsc -p tsconfig.strict.json
done
rm tsconfig.strict.json

echo "== typecheck covers every TypeScript file"
# An error fails the typecheck in a folder the template does not have, and in the C# tests' folder.
# $1: a file to write; $2: its code.
typecheck_fails_on() {
  mkdir -p "$(dirname "$1")"
  printf '%s\n' "$2" > "$1"
  if npm run typecheck --silent > "$work/typecheck" 2>&1; then echo "npm run typecheck passed with an error in $1"; exit 1; fi
  grep -qF "$1" "$work/typecheck" || { echo "npm run typecheck failed, but not on $1:"; cat "$work/typecheck"; exit 1; }
  rm "$1"
}
typecheck_fails_on lib/stray.ts 'export const stray: number = "text";'
typecheck_fails_on tests/stray.test.ts 'export const stray: number = "text";'
rmdir lib
# Node's types are for the files that run in Node: not in a component, which runs in the browser too.
typecheck_fails_on components/stray.ts 'export const stray = process.argv;'
mkdir scripts
cat > scripts/stray.ts <<'TS'
import { readFileSync } from 'node:fs';

export const stray = readFileSync(process.argv[1] ?? '', 'utf8');
TS
npm run typecheck --silent
rm -r scripts

echo "== C# tests"
# With no client build, which nothing above has made. The scaffold has no global.json, so dotnet test uses its default
# runner, as in a new app.
[[ ! -e dist ]] || { echo "dist/ is there before the C# tests, which must not need it"; exit 1; }
dotnet test tests/App.Tests -c Release --nologo

echo "== publish"
# The package's build/Netjes.targets makes publish run `npm run build` and ship dist/ (client, server and sidecar).
dotnet publish -c Release -o "$work/publish" --nologo -v quiet
# The test project and the TypeScript configs stay out of what goes to a server.
[[ ! -e "$work/publish/tests" ]] || { echo "the publish folder holds tests/"; exit 1; }
[[ ! -e "$work/publish/App.Tests.dll" ]] || { echo "the publish folder holds App.Tests.dll"; exit 1; }
leftover=$(find "$work/publish" -maxdepth 1 -name 'tsconfig*.json')
[[ -z "$leftover" ]] || { echo "the publish folder holds TypeScript configs: $leftover"; exit 1; }

echo "== run the published app"
# exec, so that the PID is the app's.
(cd "$work/publish" && ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS="http://127.0.0.1:$port" exec dotnet App.dll >"$work/app.log" 2>&1) &
app_pid=$!
for _ in $(seq 1 60); do curl -sf -o /dev/null "http://127.0.0.1:$port/" && break; sleep 1; done
curl -sf "http://127.0.0.1:$port/" | grep -q "Hello from C#" || { cat "$work/app.log"; exit 1; }
curl -sf -o /dev/null "http://127.0.0.1:$port/about"
# The template's favicon: Vite copies public/ into the build, Netjes serves it at the root, the root layout links it.
curl -sf "http://127.0.0.1:$port/" | grep '<link rel="icon" href="/favicon.svg"' >/dev/null || { echo "the home page does not link /favicon.svg"; exit 1; }
[[ "$(curl -sf -o /dev/null -w '%{content_type}' "http://127.0.0.1:$port/favicon.svg")" == image/svg+xml* ]] || { echo "/favicon.svg is not served as an SVG"; cat "$work/app.log"; exit 1; }
# The template's skip link, which the server renders: the first element Tab reaches, after a page load and after a
# client navigation, when the router starts the focus over at the top of the document.
curl -sf "http://127.0.0.1:$port/" | grep 'href="#content"' >/dev/null || { echo "the home page has no skip link to #content"; exit 1; }
# A page that is not found carries one <title>: its own, through metadata, with the layout template applied.
titles=$(curl -s "http://127.0.0.1:$port/no/such/page" | grep -o "<title>[^<]*</title>")
[[ "$titles" =~ ^\<title\>Not\ found\ ·\ [^\<]+\</title\>$ ]] || { echo "not-found titles: $titles"; exit 1; }
curl -sf "http://127.0.0.1:$port/healthz" | grep -q Healthy
# The greeting form posted without its field asks for the name, with JavaScript (the client router sends the token in a
# header and the page's URL in the body) and without (the browser posts the token field).
read -r field header value < <(curl -sf -c "$work/cookies" -H "X-Netjes-Data: 1" "http://127.0.0.1:$port/" \
  | node -e 'let s = ""; process.stdin.on("data", (d) => (s += d)).on("end", () => { const a = JSON.parse(s).antiforgery; console.log(a.formFieldName, a.headerName, a.requestToken); })')
greet="http://127.0.0.1:$port/_netjes/actions/home.greet"
status=$(curl -s -o "$work/greet" -w '%{http_code}' -b "$work/cookies" -H "X-Netjes-Data: 1" -H "$header: $value" --data-urlencode "__netjes_return=/" "$greet")
[[ "$status" == 422 ]] && grep -q "Tell us your name." "$work/greet" || { echo "home.greet without a name: $status $(cat "$work/greet")"; cat "$work/app.log"; exit 1; }
status=$(curl -s -o "$work/greet" -w '%{http_code}' -b "$work/cookies" --data-urlencode "$field=$value" --data-urlencode "__netjes_return=/" "$greet")
[[ "$status" == 422 ]] && grep -q "Tell us your name." "$work/greet" || { echo "home.greet without a name or JavaScript: $status"; cat "$work/app.log"; exit 1; }

echo "== end-to-end"
# The app's Playwright is the newest in the template's range, not the version the repository's lockfile pins, so it can
# need another Chromium. An install also removes every browser in the shared cache that no Playwright it knows of still
# uses, and it knows only the folders that ran an install and are still there: without the flag, this check could remove
# the Chromium that the examples' end-to-end suites launch. Its system libraries only in CI: on Linux, --with-deps runs
# apt-get through sudo, which asks for a password or fails in a shell that cannot ask, and needs apt.
PLAYWRIGHT_SKIP_BROWSER_GC=1 npx playwright install ${CI:+--with-deps} chromium
CI=1 E2E_DEV_PORT="$e2e_dev_port" E2E_PROD_PORT="$e2e_prod_port" E2E_VITE_PORT="$e2e_vite_port" npm run test:e2e --silent

echo "ok: Netjes $version packages build, test, publish and serve a new app"
