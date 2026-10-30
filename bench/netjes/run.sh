#!/usr/bin/env bash
# Netjes's benchmark, run on demand. Publishes the benchmark app (bench/netjes/app) in Release, starts it in
# Production, measures it with bench.mjs and stops it. The restart under load runs twice: on the app as started, then
# on the app started again with Netjes__ClientRenderFallback=false. With --target it measures an app that is already
# running. Results go to bench/netjes/results/ as JSON and markdown. docs/benchmarks.md explains what is measured and
# how to read it. Needs `npm ci` at the repository root first (the app builds with the repository's own packages).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"

port="${BENCH_PORT:-5480}"
sidecars=1
target=""
routes="$here/routes.json"
out="$here/results"
server_cpus=""
client_cpus=""
relaunch=0
args=()

usage() {
  cat <<EOF
Usage: bench/netjes/run.sh [options] [bench.mjs options]

  --port <n>            Port for the benchmark app (default 5480, or BENCH_PORT)
  --sidecars <n>        Netjes:SidecarProcesses for the app (default 1)
  --server-cpus <list>  Pin the app and its sidecars to these CPUs (taskset -c, e.g. 0-7)
  --client-cpus <list>  Pin the load generator to these CPUs (e.g. 8-11)
  --target <url>        Measure an app that is already running instead (no build, no memory, no restart)
  --routes <file>       Route list (default bench/netjes/routes.json)
  --out <dir>           Where results go (default bench/netjes/results)

Everything else goes to bench.mjs: --quick, --rates, --concurrency, --warmup, --duration, --provisional, --note, ...
(node bench/netjes/bench.mjs --help lists them).
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --port) port="$2"; shift 2 ;;
    --sidecars) sidecars="$2"; shift 2 ;;
    --server-cpus) server_cpus="$2"; shift 2 ;;
    --client-cpus) client_cpus="$2"; shift 2 ;;
    --target) target="$2"; shift 2 ;;
    --routes) routes="$2"; shift 2 ;;
    --out) out="$2"; shift 2 ;;
    # Not for use by hand: bench.mjs runs it between the two restarts (see below).
    --relaunch-app-without-fallback) relaunch=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) args+=("$1"); shift ;;
  esac
done

client=(node)
if [[ -n "$client_cpus" ]]; then client=(taskset -c "$client_cpus" node); fi

if [[ -n "$target" ]]; then
  exec "${client[@]}" "$here/bench.mjs" --target "$target" --routes "$routes" --out "$out" ${args[@]+"${args[@]}"}
fi

url="http://127.0.0.1:$port"
publish="$here/.out/app"
log="$here/.out/app.log"
pid_file="$here/.out/app.pid"
server=(dotnet NetjesBench.dll)
if [[ -n "$server_cpus" ]]; then server=(taskset -c "$server_cpus" dotnet NetjesBench.dll); fi

# Starts the published app in Production, with any further settings given as NAME=value, writes its pid to $pid_file
# and waits until /healthz answers Healthy. Its output goes to $log.
start_app() {
  (
    cd "$publish"
    exec env ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS="$url" Netjes__SidecarProcesses="$sidecars" "$@" "${server[@]}"
  ) >"$log" 2>&1 </dev/null &
  local pid=$!
  echo "$pid" >"$pid_file"
  for _ in $(seq 1 120); do
    if ! kill -0 "$pid" 2>/dev/null; then
      rm -f "$pid_file"
      echo "run.sh: the app exited (its log: $log)" >&2
      return 1
    fi
    if [[ "$(curl -s --max-time 2 "$url/healthz" || true)" == "Healthy" ]]; then return 0; fi
    sleep 0.5
  done
  echo "run.sh: the app did not become healthy within 60 s (its log: $log)" >&2
  return 1
}

# Stops the app in $pid_file. The app stops its sidecars; any it could not (one killed and restarting) go too.
stop_app() {
  local pid children
  pid="$(cat "$pid_file" 2>/dev/null || true)"
  if [[ -z "$pid" ]]; then return 0; fi
  children="$(pgrep -P "$pid" || true)"
  kill "$pid" 2>/dev/null || true
  # wait reaps an app this shell started; one that another run.sh started is not its child, so it is waited out.
  wait "$pid" 2>/dev/null || true
  while kill -0 "$pid" 2>/dev/null; do sleep 0.1; done
  if [[ -n "$children" ]]; then kill $children 2>/dev/null || true; fi
  rm -f "$pid_file"
}

if [[ "$relaunch" == 1 ]]; then
  # The second restart under load: bench.mjs runs this, with the run's --port and --sidecars and the first app's CPUs,
  # once the first restart is measured. It replaces the app with one whose client-rendered fallback is off and returns
  # once that one is healthy. The run's own run.sh stops it at the end.
  stop_app
  log="$here/.out/app-without-fallback.log"
  start_app Netjes__ClientRenderFallback=false
  exit 0
fi

# The idle check takes the load average from here: building the app lifts the one at the start of the run.
load_before_build=()
if [[ -r /proc/loadavg ]]; then load_before_build=(--load-before-build "$(cut -d ' ' -f 1 /proc/loadavg)"); fi

if [[ ! -d "$root/node_modules/netjes" ]]; then
  echo "run.sh: run 'npm ci' at the repository root first" >&2
  exit 1
fi
# The app imports netjes from its build, packages/netjes/dist, so the run measures the package as it is checked out.
echo "== build the netjes package" >&2
(cd "$root" && npm run build --workspace=packages/netjes --silent) >&2
if curl -s -o /dev/null --max-time 2 "$url/"; then
  echo "run.sh: something is already listening on port $port (use --port)" >&2
  exit 1
fi

mkdir -p "$here/.out"
echo "== generate the app's types (Release build)" >&2
# Command mode: the app writes generated/netjes.ts and exits without serving.
(
  cd "$here/app"
  ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="$url" NETJES_COMMAND=generate \
    dotnet run -c Release --no-launch-profile --nologo -v quiet >"$here/.out/generate.log" 2>&1
) || { cat "$here/.out/generate.log" >&2; exit 1; }

echo "== publish the benchmark app (Release)" >&2
rm -rf "$publish"
dotnet publish "$here/app/NetjesBench.csproj" -c Release -o "$publish" --nologo -v quiet >&2

echo "== start it in Production on $url with $sidecars sidecar(s)" >&2
trap stop_app EXIT
start_app || { cat "$log" >&2; exit 1; }
app_pid="$(cat "$pid_file")"

meta="$here/.out/meta.json"
node "$here/meta.mjs" --root "$root" --app "$here/app" --publish "$publish" --dotnet-sdk "$(cd "$root" && dotnet --version)" \
  --setting "sidecars=$sidecars" --setting "serverCpus=$server_cpus" --setting "clientCpus=$client_cpus" >"$meta"

# The restart kills the oldest sidecar of the app in the pid file only; the relaunch replaces that app.
kill_cmd="kill -KILL \$(pgrep -o -P \$(cat $(printf %q "$pid_file")) -f netjes-sidecar)"
# bench.mjs runs the relaunch, so with --client-cpus alone the second app would inherit the load generator's CPUs: it
# gets the ones this script, and so the first app, runs on.
relaunch_cpus="$server_cpus"
if [[ -z "$relaunch_cpus" && -n "$client_cpus" ]]; then
  relaunch_cpus="$(sed -n 's/^Cpus_allowed_list:[[:space:]]*//p' "/proc/$$/status")"
fi
relaunch_cmd="$(printf '%q ' "$here/run.sh" --port "$port" --sidecars "$sidecars" --server-cpus "$relaunch_cpus" \
  --relaunch-app-without-fallback)"
relaunch_cmd="${relaunch_cmd% }"
"${client[@]}" "$here/bench.mjs" --target "$url" --routes "$routes" --out "$out" --meta "$meta" \
  --pid "$app_pid" --kill "$kill_cmd" --relaunch-without-fallback "$relaunch_cmd" \
  ${load_before_build[@]+"${load_before_build[@]}"} ${args[@]+"${args[@]}"}
