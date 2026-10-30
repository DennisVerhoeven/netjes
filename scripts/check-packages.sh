#!/usr/bin/env bash
# Packs the NuGet and npm packages into a local feed, scaffolds an app with `netjes new --packages` that restores
# from that feed only, then tests, type-checks (also under create-vite's React-TS flags), builds and runs it in
# Production. Run from the repository root.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
port="${CHECK_PACKAGES_PORT:-5399}"
trap 'fuser -k "$port/tcp" >/dev/null 2>&1 || true; rm -rf "$work"' EXIT
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
# The packed CLI adds the same files to an app that netjes new did not make.
mkdir "$work/existing"
touch "$work/existing/Existing.csproj"
npx netjes agents "$work/existing" >/dev/null
for file in AGENTS.md CLAUDE.md; do
  [[ -s "$work/existing/$file" ]] || { echo "netjes agents did not write $file"; exit 1; }
done
diff -r .claude/skills/netjes "$work/existing/.claude/skills/netjes"

echo "== test, typecheck, publish"
npm test --silent
npm run typecheck --silent
# netjes exports declarations, so the app's compiler options apply to the app's own code alone: one that the package's
# sources do not meet, such as noPropertyAccessFromIndexSignature, does not fail the app.
cat > tsconfig.index-access.json <<'JSON'
{ "extends": "./tsconfig.json", "compilerOptions": { "noPropertyAccessFromIndexSignature": true } }
JSON
npx tsc -p tsconfig.index-access.json
rm tsconfig.index-access.json
npx netjes generate --check
# create-vite's React-TS template also sets noUnusedLocals, erasableSyntaxOnly and more, which an app's tsc applies to
# the template's files and the package's declarations. packages/netjes/tsconfig.strict-app.json holds that flag list
# (the package's own typecheck uses it as well); here the app's own tsconfig.json is extended after it, so the app's
# settings still apply.
cat > tsconfig.strict.json <<JSON
{
  "extends": ["$root/packages/netjes/tsconfig.strict-app.json", "./tsconfig.json"]
}
JSON
npx tsc -p tsconfig.strict.json
rm tsconfig.strict.json
# The package's build/Netjes.targets makes publish run `npm run build` and ship dist/ (client, server and sidecar).
dotnet publish -c Release -o "$work/publish" --nologo -v quiet

echo "== run the published app"
(cd "$work/publish" && ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS="http://127.0.0.1:$port" dotnet App.dll >"$work/app.log" 2>&1) &
disown
for _ in $(seq 1 60); do curl -sf -o /dev/null "http://127.0.0.1:$port/" && break; sleep 1; done
curl -sf "http://127.0.0.1:$port/" | grep -q "Hello from C#" || { cat "$work/app.log"; exit 1; }
curl -sf -o /dev/null "http://127.0.0.1:$port/about"
# The template's favicon: Vite copies public/ into the build, Netjes serves it at the root, the root layout links it.
curl -sf "http://127.0.0.1:$port/" | grep '<link rel="icon" href="/favicon.svg"' >/dev/null || { echo "the home page does not link /favicon.svg"; exit 1; }
[[ "$(curl -sf -o /dev/null -w '%{content_type}' "http://127.0.0.1:$port/favicon.svg")" == image/svg+xml* ]] || { echo "/favicon.svg is not served as an SVG"; cat "$work/app.log"; exit 1; }
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
echo "ok: Netjes $version packages build, test, publish and serve a new app"
