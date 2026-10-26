#!/usr/bin/env sh
# Prints the section of docs/UPGRADING.md written for one release: the "## <from> → <version>" heading
# and everything up to the next "## " heading. A prerelease tag (0.2.0-rc.1) gets its release's notes.
# Prints nothing, and still succeeds, when no section names the version: the release then carries only
# GitHub's generated notes. Any heading still saying Unreleased fails it: the tagged commit holds every
# change those notes describe, so they would not ship. Usage: scripts/release-notes.sh <version> [file]
set -eu

version="${1:?usage: release-notes.sh <version> [file]}"
file="${2:-docs/UPGRADING.md}"
base="${version%%-*}"

awk -v base="$base" -v file="$file" '
    /^#+[[:space:]]/ && tolower($0) ~ /unreleased/ {
        unreleased = $0
        sub(/[[:space:]]+$/, "", unreleased)
    }
    /^## / {
        heading = $0
        sub(/[[:space:]]+$/, "", heading)
        suffix = "→ " base
        printing = !found && substr(heading, length(heading) - length(suffix) + 1) == suffix
        if (printing)
            found = 1
    }
    printing { print }
    END {
        if (unreleased != "") {
            print "release-notes.sh: \"" unreleased "\" is still in " file ": fold its notes into the section headed \"… → " base "\" (rename it to that if there is none)" > "/dev/stderr"
            exit 1
        }
    }
' "$file"
