#!/bin/sh

set -euo pipefail

jj git fetch
jj rebase -o base
