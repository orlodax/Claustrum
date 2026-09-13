#!/usr/bin/env bash
# End-to-end smoke check for Claustrum (docs/PLAN.md "Verification" item 3): a fresh git repo,
# `backends doctor claude`, then a real `run builder` against the `claude` backend.
# Usage: scripts/smoke.sh [path-to-claustrum-binary]   (falls back to $CLAUSTRUM, then a Release build)
set -uo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"

resolve_binary() {
    if [ -n "${1:-}" ]; then
        echo "$1"
        return
    fi
    if [ -n "${CLAUSTRUM:-}" ]; then
        echo "$CLAUSTRUM"
        return
    fi

    echo "Building claustrum (Release)..." >&2
    dotnet build "$repo_root/src/Claustrum/Claustrum.csproj" -c Release >&2 || return 1
    find "$repo_root/src/Claustrum/bin/Release" -type f -name "claustrum" | head -n1
}

claustrum_bin="$(resolve_binary "${1:-}")"
if [ -z "$claustrum_bin" ] || [ ! -x "$claustrum_bin" ]; then
    echo "could not resolve a claustrum binary" >&2
    exit 1
fi
echo "Using binary: $claustrum_bin"

if ! command -v jq >/dev/null 2>&1; then
    echo "smoke.sh requires 'jq' to parse JSON output" >&2
    exit 1
fi

tmp="$(mktemp -d)"
cleanup() { rm -rf "$tmp"; }
trap cleanup EXIT

(
    cd "$tmp"
    git init -q
    git config user.email "smoke@claustrum.local"
    git config user.name "claustrum-smoke"
    echo "smoke" > README.md
    git add -A
    git commit -q -m init
)

checks_failed=0
report_check() {
    local name="$1" ok="$2" detail="$3"
    printf "%-55s %-4s %s\n" "$name" "$([ "$ok" = "1" ] && echo OK || echo FAIL)" "$detail"
    [ "$ok" = "1" ] || checks_failed=$((checks_failed + 1))
}

doctor_output="$("$claustrum_bin" backends doctor claude 2>&1)"
if echo "$doctor_output" | grep -A1 "^claude:" | grep -q "found:.*True"; then
    doctor_ok=1
else
    doctor_ok=0
fi
report_check "backends doctor claude finds it" "$doctor_ok" "$(echo "$doctor_output" | tr '\n' ' ')"

run_output="$("$claustrum_bin" run builder --brief "create hello.txt containing hi" --json --cwd "$tmp" --budget 0.5 --model sonnet 2>/dev/null)"
status="$(echo "$run_output" | jq -r '.status // empty' 2>/dev/null)"
has_hello="$(echo "$run_output" | jq -r '([.changed_files[]?.path] | index("hello.txt")) != null' 2>/dev/null)"
report_present="$(echo "$run_output" | jq -r '.report != null' 2>/dev/null)"

run_ok=0
[ "$status" = "success" ] && [ "$has_hello" = "true" ] && [ "$report_present" = "true" ] && run_ok=1
report_check "run builder --json: success + hello.txt + report" "$run_ok" "status=$status hello.txt=$has_hello report=$report_present"

if [ "$checks_failed" -gt 0 ]; then
    echo "$checks_failed check(s) failed" >&2
    exit 1
fi

echo "All smoke checks passed."
exit 0
