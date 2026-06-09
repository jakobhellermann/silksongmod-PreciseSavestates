#!/bin/sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"
cd "$ROOT"

jj edit -r 'description(regex:"Rename [pP]roject")'
jj restore
./scripts/rename.sh
