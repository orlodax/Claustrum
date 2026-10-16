#!/usr/bin/env sh
# Prints the section of docs/UPGRADING.md written for one release: the "## <from> → <version>" heading
# and everything up to the next "## " heading. A prerelease tag (0.2.0-rc.1) gets its release's notes.
# Prints nothing, and still succeeds, when no section names the version: the release then carries only
# GitHub's generated notes. Usage: scripts/release-notes.sh <version> [file]
set -eu

version="${1:?usage: release-notes.sh <version> [file]}"
file="${2:-docs/UPGRADING.md}"
base="${version%%-*}"

awk -v base="$base" '
    /^## / {
        if (printing) exit
        heading = $0
        sub(/[[:space:]]+$/, "", heading)
        suffix = "→ " base
        if (substr(heading, length(heading) - length(suffix) + 1) == suffix) printing = 1
    }
    printing { print }
' "$file"
