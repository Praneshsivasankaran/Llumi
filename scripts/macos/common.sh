#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
export PYTHONPATH="$ROOT/scripts/macos${PYTHONPATH:+:$PYTHONPATH}"
OUT="$ROOT/dist/macos"
fail() { printf '%s\n' "$*" >&2; exit 1; }
app_check() {
  [[ -d "$1/Contents/MacOS" ]] || fail 'Expected an application bundle.'
  python3 "$ROOT/scripts/macos/validate-bundle.py" "$1"
  python3 "$ROOT/macos/Scripts/privacy-check.py" "$1"
}
identity_check() {
  [[ "${DEVELOPER_ID_APPLICATION:-}" == 'Developer ID Application: Pranesh S (K38622WCYD)' ]] || fail 'Set DEVELOPER_ID_APPLICATION to the approved Pranesh S Developer ID Application identity only after signing authorization.'
}
plan() { printf 'PLAN:'; printf ' %q' "$@"; printf '\n'; }
