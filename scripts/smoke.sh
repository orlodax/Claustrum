#!/usr/bin/env bash
# End-to-end smoke check for Claustrum (docs/PLAN.md "Verification" item 3) on a throwaway git repo:
# `backends doctor` for every registered backend, `claustrum init` plus an idempotent rerun, a real
# `run builder` per installed backend the builder role supports, two concurrent max_parallel
# builders followed by `jobs clean`, and a spawned-architect `coordinate`. A backend that is not
# installed SKIPs instead of failing, so one script works on any subset; only a FAIL row exits nonzero.
# Usage: scripts/smoke.sh [path-to-claustrum-binary]   (falls back to $CLAUSTRUM, then a Release publish)
# Env: CLAUSTRUM_SMOKE_MODEL_<NAME> (e.g. …CURSOR=auto) gives that backend's paid run its model and
# CLAUSTRUM_SMOKE_COORDINATE_MODEL the coordinate architect; unset SKIPs the row (claude: sonnet).
set -uo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"

host_rid() {
    local os arch
    case "$(uname -s)" in
        Linux) os=linux ;;
        Darwin) os=osx ;;
        *) echo "no host RID for $(uname -s): pass the binary as an argument or set \$CLAUSTRUM" >&2; return 1 ;;
    esac
    case "$(uname -m)" in
        x86_64 | amd64) arch=x64 ;;
        aarch64 | arm64) arch=arm64 ;;
        *) echo "no host RID for $(uname -m): pass the binary as an argument or set \$CLAUSTRUM" >&2; return 1 ;;
    esac
    printf '%s-%s' "$os" "$arch"
}

# Publish, not build: PackAsTool=true suppresses the apphost, so `dotnet build` leaves a
# claustrum.dll and no executable at all (issue #22, NOTES.md "Releasing: five RIDs, one tool
# package"). A failed AOT publish is reported, never worked around — a framework-dependent binary
# would exercise a different startup path than the one release.yml ships.
resolve_binary() {
    if [ -n "${1:-}" ]; then
        echo "$1"
        return
    fi
    if [ -n "${CLAUSTRUM:-}" ]; then
        echo "$CLAUSTRUM"
        return
    fi

    local rid candidate
    rid="$(host_rid)" || return 1

    echo "Publishing claustrum (Release, AOT, $rid)..." >&2
    if ! dotnet publish "$repo_root/src/Claustrum/Claustrum.csproj" -c Release -r "$rid" -p:PublishAot=true >&2; then
        echo "AOT publish failed. Linux needs clang or gcc plus the zlib development headers" >&2
        echo "(zlib1g-dev / zlib-devel); Windows needs the VS 'Desktop development with C++'" >&2
        echo "workload. See AGENTS.md 'Build and gate'." >&2
        return 1
    fi

    for candidate in "$repo_root/src/Claustrum/bin/Release"/*/"$rid"/publish/claustrum; do
        if [ -x "$candidate" ]; then
            echo "$candidate"
            return
        fi
    done
    echo "publish reported success but left no claustrum under bin/Release/*/$rid/publish" >&2
    return 1
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

# The parallel runs' stdout and the cast answers file live beside the repo, not in it: an untracked
# file inside the repo turns up in the next run's changed_files.
work="$(mktemp -d)"
tmp="$work/repo"
outputs="$work/out"
cleanup() { rm -rf "$work"; }
trap cleanup EXIT
mkdir -p "$tmp" "$outputs"

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
    local name="$1" status="$2" detail="$3"
    printf "%-60s %-4s %s\n" "$name" "$status" "$detail"
    if [ "$status" = "FAIL" ]; then
        checks_failed=$((checks_failed + 1))
    fi
}

declare -A found_backends=()
backend_names=()
parallel_branches=()
smoke_cast_created=0

# Check 4's row names, shared between the checks themselves and the SKIP branches that stand in for
# them, so a renamed row cannot drift between the two.
cast_row="cast create smoke: builder claude:sonnet, max_parallel 2"
parallel_row="2 parallel builders: distinct worktrees + branches"
clean_row="jobs clean: worktrees removed, branches kept"
coordinate_row="coordinate: spawned architect, builder writes hello.txt"

# Multi-line output squeezed onto the table's third column (the api backend's `version:` alone is
# four lines of curl banner).
join_lines() {
    tr '\n' ';' | sed 's/;\{1,\}$//'
}

# 1. `backends doctor <name>` for every name `backends list` prints. found: False is a SKIP, not a
# failure — the whole point of the loop is that one script runs on any subset of the five.
check_doctor() {
    local name="$1" output exit_code found path problems
    output="$("$claustrum_bin" backends doctor "$name" 2>&1)"
    exit_code=$?
    found="$(printf '%s\n' "$output" | sed -n 's/^  found: *//p' | head -n1)"
    path="$(printf '%s\n' "$output" | sed -n 's/^  path: *//p' | head -n1)"
    problems="$(printf '%s\n' "$output" | sed -n 's/^  problem: *//p' | join_lines)"

    local detail="path=$path"
    if [ -n "$problems" ]; then
        detail="$detail problems=$problems"
    fi

    if [ "$exit_code" -ne 0 ]; then
        report_check "backends doctor $name finds it" FAIL "exit=$exit_code $problems"
    elif [ "$found" = "True" ]; then
        found_backends["$name"]=1
        report_check "backends doctor $name finds it" OK "$detail"
    else
        report_check "backends doctor $name finds it" SKIP "not installed: $problems"
    fi
}

check_all_doctors() {
    mapfile -t backend_names < <("$claustrum_bin" backends list)
    if [ "${#backend_names[@]}" -eq 0 ]; then
        report_check "backends list yields the registered backends" FAIL "no names on stdout"
        return
    fi

    report_check "backends list yields the registered backends" OK "${backend_names[*]}"
    local name
    for name in "${backend_names[@]}"; do
        check_doctor "$name"
    done
}

# 2. `claustrum init` has no --cwd: it scaffolds Environment.CurrentDirectory (Cli/InitCommand.cs).
check_init() {
    local output exit_code entry
    local missing=()
    output="$(cd "$tmp" && "$claustrum_bin" init 2>&1)"
    exit_code=$?

    [ -f "$tmp/claustrum.json" ] || missing+=("claustrum.json")
    [ -d "$tmp/.claustrum/casts" ] || missing+=(".claustrum/casts/")
    [ -f "$tmp/.claude/agents/builder.md" ] || missing+=(".claude/agents/builder.md")
    for entry in ".claustrum/worktrees/" ".claustrum/locks/" ".claustrum/briefs/"; do
        grep -qF "$entry" "$tmp/.gitignore" 2>/dev/null || missing+=(".gitignore lacks $entry")
    done

    if [ "$exit_code" -eq 0 ] && [ "${#missing[@]}" -eq 0 ]; then
        report_check "claustrum init scaffolds a fresh repo" OK "$(printf '%s\n' "$output" | join_lines)"
    else
        report_check "claustrum init scaffolds a fresh repo" FAIL "exit=$exit_code missing: ${missing[*]:-none}"
    fi
}

snapshot_repo() {
    (cd "$tmp" && find . -path ./.git -prune -o -type f -print0 | sort -z | xargs -0 -r sha256sum)
}

check_init_rerun() {
    local before after output exit_code unchanged=no up_to_date=no snapshot=identical
    before="$(snapshot_repo)"
    output="$(cd "$tmp" && "$claustrum_bin" init 2>&1)"
    exit_code=$?
    after="$(snapshot_repo)"

    printf '%s\n' "$output" | grep -q "already present, left unchanged" && unchanged=yes
    printf '%s\n' "$output" | grep -q "already up to date" && up_to_date=yes
    [ "$before" = "$after" ] || snapshot=differs

    if [ "$exit_code" -eq 0 ] && [ "$unchanged" = "yes" ] && [ "$up_to_date" = "yes" ] && [ "$snapshot" = "identical" ]; then
        report_check "claustrum init is idempotent on rerun" OK "left_unchanged=$unchanged up_to_date=$up_to_date snapshot=$snapshot"
    else
        report_check "claustrum init is idempotent on rerun" FAIL \
            "exit=$exit_code left_unchanged=$unchanged up_to_date=$up_to_date snapshot=$snapshot"
    fi
}

# 3. One paid `run builder` per installed backend. The row name keeps M1's wording for claude, whose
# flags are also unchanged, so a regression there reads the same as it always did.
run_row() {
    if [ "$1" = "claude" ]; then
        echo "run builder --json: success + hello.txt + report"
    else
        echo "run builder --backend $1: success + hello.txt + report"
    fi
}

# A backend that is merely installed has no model the smoke run can guess: claude's default id
# ("opus") is refused by cursor and by opencode, so the row would FAIL where it must SKIP. claude
# keeps sonnet, M1's original flag, when its variable is unset.
smoke_model_var() {
    printf 'CLAUSTRUM_SMOKE_MODEL_%s' "$(printf '%s' "$1" | tr '[:lower:]' '[:upper:]')"
}

smoke_model() {
    local var value
    var="$(smoke_model_var "$1")"
    value="${!var:-}"
    if [ -z "$value" ] && [ "$1" = "claude" ]; then
        value="sonnet"
    fi
    printf '%s' "$value"
}

assert_run() {
    local row="$1" expected_file="$2"
    shift 2
    local output status has_file report_present
    # An earlier backend's hello.txt would let a backend that did nothing still pass this row.
    rm -f "$tmp/$expected_file"

    output="$("$claustrum_bin" run builder "$@" --json --cwd "$tmp" 2>/dev/null)"
    status="$(printf '%s' "$output" | jq -r '.status // empty' 2>/dev/null)"
    has_file="$(printf '%s' "$output" | jq -r --arg f "$expected_file" '([.changed_files[]?.path] | index($f)) != null' 2>/dev/null)"
    report_present="$(printf '%s' "$output" | jq -r '.report != null' 2>/dev/null)"

    local detail="status=$status $expected_file=$has_file report=$report_present"
    if [ "$status" = "success" ] && [ "$has_file" = "true" ] && [ "$report_present" = "true" ]; then
        report_check "$row" OK "$detail"
    else
        report_check "$row" FAIL "$detail"
    fi
}

check_all_runs() {
    # The builder role's own harness list (roles/builder/role.json) decides who gets a paid run:
    # `api` is registered and its curl is "found", but it has no tools, is not a builder harness, and
    # `--backend api` without an `api:<provider>:<model>` model spec cannot even resolve a model
    # (measured 2026-09-21: "api backend model must be 'openrouter:<model>'... got 'opus'").
    local harnesses name model
    harnesses="$("$claustrum_bin" roles show builder | sed -n 's/^harnesses: *//p' | tr -d ',')"
    if [ -z "$harnesses" ]; then
        # Without the list every backend would SKIP, quietly dropping every paid row.
        report_check "roles show builder lists its harnesses" FAIL "no harnesses line"
        return
    fi
    harnesses=" $harnesses "

    for name in "${backend_names[@]}"; do
        if [ -z "${found_backends[$name]:-}" ]; then
            report_check "$(run_row "$name")" SKIP "not installed"
            continue
        fi
        if [[ "$harnesses" != *" $name "* ]]; then
            report_check "$(run_row "$name")" SKIP "not a builder harness (roles show builder)"
            continue
        fi

        model="$(smoke_model "$name")"
        if [ -z "$model" ]; then
            report_check "$(run_row "$name")" SKIP "set $(smoke_model_var "$name")=<model> to run"
            continue
        fi

        if [ "$name" = "claude" ]; then
            assert_run "$(run_row "$name")" hello.txt \
                --brief "create hello.txt containing hi" --budget 0.5 --model "$model"
        else
            assert_run "$(run_row "$name")" hello.txt \
                --backend "$name" --brief "create hello.txt containing hi" --budget 0.5 --model "$model"
        fi
    done
}

# 4. max_parallel > 1 (docs/PLAN.md §D4) end to end: a cast with max_parallel 2, two concurrent
# builders that must land on their own worktree and branch, then `jobs clean`.
check_cast_create() {
    local answers="$outputs/smoke-answers.json" output exit_code

    # Only the questions this cast needs: CastBuilder reads an unanswered role as "not needed"
    # (Casts/CastBuilder.cs), so this file stays correct when the role library gains a role.
    cat > "$answers" <<'JSON'
{
  "architect": "host",
  "builder": "claude:sonnet",
  "builder_max_parallel": "2",
  "budget": "2"
}
JSON

    output="$(cd "$tmp" && "$claustrum_bin" cast create --answers "$answers" --name smoke 2>&1)"
    exit_code=$?
    if [ "$exit_code" -eq 0 ] && [ -f "$tmp/.claustrum/casts/smoke.json" ]; then
        smoke_cast_created=1
        report_check "$cast_row" OK "$output"
    else
        report_check "$cast_row" FAIL "exit=$exit_code $output"
    fi
}

check_parallel_runs() {
    local n
    local statuses=() worktrees=() has_file=()

    for n in 1 2; do
        "$claustrum_bin" run builder --cast smoke --brief "create parallel-$n.txt containing $n" \
            --json --cwd "$tmp" --budget 0.5 > "$outputs/parallel-$n.json" 2>/dev/null &
    done
    wait

    for n in 1 2; do
        statuses[$n]="$(jq -r '.status // empty' "$outputs/parallel-$n.json" 2>/dev/null)"
        worktrees[$n]="$(jq -r '.worktree // empty' "$outputs/parallel-$n.json" 2>/dev/null)"
        parallel_branches[$n]="$(jq -r '.branch // empty' "$outputs/parallel-$n.json" 2>/dev/null)"
        has_file[$n]="$(jq -r --arg f "parallel-$n.txt" '([.changed_files[]?.path] | index($f)) != null' \
            "$outputs/parallel-$n.json" 2>/dev/null)"
    done

    local detail="status=${statuses[1]},${statuses[2]} branch=${parallel_branches[1]},${parallel_branches[2]}"
    detail="$detail files=${has_file[1]},${has_file[2]}"
    if [ "${statuses[1]}" = "success" ] && [ "${statuses[2]}" = "success" ] \
        && [ -n "${worktrees[1]}" ] && [ -n "${worktrees[2]}" ] && [ "${worktrees[1]}" != "${worktrees[2]}" ] \
        && [ -n "${parallel_branches[1]}" ] && [ -n "${parallel_branches[2]}" ] \
        && [ "${parallel_branches[1]}" != "${parallel_branches[2]}" ] \
        && [ "${has_file[1]}" = "true" ] && [ "${has_file[2]}" = "true" ]; then
        report_check "$parallel_row" OK "$detail"
    else
        report_check "$parallel_row" FAIL "$detail"
    fi
}

check_jobs_clean() {
    local output exit_code n
    local left=0 branches_kept=yes
    output="$("$claustrum_bin" jobs clean --cwd "$tmp" 2>&1)"
    exit_code=$?

    if [ -d "$tmp/.claustrum/worktrees" ]; then
        left="$(find "$tmp/.claustrum/worktrees" -mindepth 1 -maxdepth 1 | wc -l)"
    fi

    # `jobs clean` removes the working directory only — the branch survives for the architect to
    # rebase from (Cli/JobsCommands.cs, NOTES.md "Worktree isolation for max_parallel builders").
    local branch_list
    branch_list="$(git -C "$tmp" branch --list 'claustrum/*')"
    for n in 1 2; do
        if [ -z "${parallel_branches[$n]:-}" ] || ! printf '%s\n' "$branch_list" | grep -qF "${parallel_branches[$n]}"; then
            branches_kept=no
        fi
    done

    local detail="exit=$exit_code worktrees_left=$left branches_kept=$branches_kept ($output)"
    if [ "$exit_code" -eq 0 ] && [ "$left" -eq 0 ] && [ "$branches_kept" = "yes" ]; then
        report_check "$clean_row" OK "$detail"
    else
        report_check "$clean_row" FAIL "$detail"
    fi
}

check_max_parallel() {
    if [ -z "${found_backends[claude]:-}" ]; then
        report_check "$cast_row" SKIP "claude not installed"
        report_check "$parallel_row" SKIP "claude not installed"
        report_check "$clean_row" SKIP "claude not installed"
        return
    fi

    check_cast_create
    if [ "$smoke_cast_created" -ne 1 ]; then
        report_check "$parallel_row" SKIP "cast smoke was not created"
        report_check "$clean_row" SKIP "cast smoke was not created"
        return
    fi

    check_parallel_runs
    check_jobs_clean
}

# 5. docs/PLAN.md §D3 spawned architect: `coordinate` runs the architect role headlessly and the
# architect delegates the file to a builder, so this row pays for two nested agents — its own
# opt-in variable, separate from the per-backend ones, and a top-level check rather than a row
# inside check 4, whose early returns would otherwise swallow it.
check_coordinate() {
    local model="${CLAUSTRUM_SMOKE_COORDINATE_MODEL:-}"
    if [ -z "$model" ]; then
        report_check "$coordinate_row" SKIP "set CLAUSTRUM_SMOKE_COORDINATE_MODEL=<spec> to run"
        return
    fi
    if [ -z "${found_backends[claude]:-}" ]; then
        report_check "$coordinate_row" SKIP "claude not installed"
        return
    fi

    # `cast create`'s questionnaire cannot express a spawned architect, so this cast is written by
    # hand; its `library` is copied from the one cast create did write, to stay right when the role
    # library's version moves.
    local library builder_model
    library="$(jq -r '.library // empty' "$tmp/.claustrum/casts/smoke.json" 2>/dev/null)"
    [ -n "$library" ] || library="1.0.0"
    builder_model="$(smoke_model claude)"
    [[ "$builder_model" == *:* ]] || builder_model="claude:$builder_model"

    mkdir -p "$tmp/.claustrum/casts"
    cat > "$tmp/.claustrum/casts/coordinate.json" <<JSON
{
  "name": "coordinate",
  "library": "$library",
  "architect": { "mode": "spawned", "model": "$model", "tier": null },
  "roles": {
    "builder": { "model": "$builder_model", "backend": null, "tier": null, "max_parallel": null },
    "code-reviewer": null,
    "ui-reviewer": null,
    "tester": null
  },
  "budget_usd": 2
}
JSON

    # check 3's claude row left a hello.txt behind; an architect that delegated nothing would still
    # pass the "in repo" half of the assertion below.
    rm -f "$tmp/hello.txt"

    local output status job_id in_repo=no on_branch=no budget=""
    output="$("$claustrum_bin" coordinate --cast coordinate \
        --brief "create hello.txt containing hi, delegate the file creation to the builder role, do not run a reviewer or tester" \
        --json --cwd "$tmp" --timeout 900 2>/dev/null)"
    status="$(printf '%s' "$output" | jq -r '.status // empty' 2>/dev/null)"
    job_id="$(printf '%s' "$output" | jq -r '.job_id // empty' 2>/dev/null)"

    # The builder may have run in its own worktree, in which case hello.txt is only ever a commit on
    # a claustrum/* branch — the architect is expected to leave it there for a rebase, not to merge.
    [ -f "$tmp/hello.txt" ] && in_repo=yes
    if [ -n "$(git -C "$tmp" branch --list 'claustrum/*' 2>/dev/null)" ] \
        && [ -n "$(git -C "$tmp" log --all --oneline -- hello.txt 2>/dev/null)" ]; then
        on_branch=yes
    fi
    if [ -n "$job_id" ]; then
        budget="$("$claustrum_bin" jobs budget "$job_id" 2>&1 | join_lines)"
    fi

    local detail="status=$status job=$job_id hello_in_repo=$in_repo hello_on_branch=$on_branch budget=$budget"
    if [ "$status" = "success" ] && { [ "$in_repo" = "yes" ] || [ "$on_branch" = "yes" ]; }; then
        report_check "$coordinate_row" OK "$detail"
    else
        report_check "$coordinate_row" FAIL "$detail"
    fi
}

check_all_doctors
check_init
check_init_rerun
check_all_runs
check_max_parallel
check_coordinate

if [ "$checks_failed" -gt 0 ]; then
    echo "$checks_failed check(s) failed" >&2
    exit 1
fi

echo "All smoke checks passed."
exit 0
