#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"
cd "$ROOT"

FROM="ExampleMod"
FROM_LOWER="examplemod"
TO="${1:-$(basename "$ROOT")}"
TO_LOWER="$(echo "$TO" | tr '[:upper:]' '[:lower:]')"

EXCLUDE=(-E obj -E bin -E .git -E .jj -E 'thunderstore/temp' -E 'thunderstore/dist' -E scripts)

echo "Renaming '$FROM' → '$TO' in $ROOT"

# Replace in file contents
fd --hidden --type f "${EXCLUDE[@]}" . -x perl -i -pe "s/\Q$FROM\E/$TO/g; s/\Q$FROM_LOWER\E/$TO_LOWER/g"

# Rename files and directories (deepest first so parent renames don't break paths)
fd --hidden -0 "${EXCLUDE[@]}" "$FROM" . | sort -rz | while IFS= read -rd '' path; do
    new_path="${path//$FROM/$TO}"
    [ "$path" != "$new_path" ] && mv "$path" "$new_path"
done
fd --hidden -0 "${EXCLUDE[@]}" "$FROM_LOWER" . | sort -rz | while IFS= read -rd '' path; do
    new_path="${path//$FROM_LOWER/$TO_LOWER}"
    [ "$path" != "$new_path" ] && mv "$path" "$new_path"
done

# Replace github username placeholder (case-insensitive)
fd --hidden --type f "${EXCLUDE[@]}" . -x perl -i -pe 's/YourGitHubUsername/jakobhellermann/g; s/yourgithubusername/jakobhellermann/g'
