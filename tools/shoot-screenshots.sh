#!/usr/bin/env bash
# Regenerates docs/screenshots. Run from the repository root; needs a build and Xvfb.
#
# The machine is given some real work first. A screenshot of a monitor on an idle machine shows a
# table of zeroes and empty plots, which is honest and tells a reader nothing about what the program
# does — so a few busy loops run for the length of the capture, and what the picture shows is a real
# measurement of them.
set -euo pipefail

BIN="${1:-ProcessManager.App/bin/Release/net10.0/procman}"
OUT="docs/screenshots"
mkdir -p "$OUT"

busy() {
  for _ in $(seq 1 "$1"); do
    ( timeout "$2" bash -c 'while :; do :; done' >/dev/null 2>&1 & )
  done
}

echo "== terminal =="
busy 3 20
"$BIN" --tui --capture-samples 18 --interval 0.4 \
  --capture-svg "$OUT/tui.svg" --capture-frame "$OUT/tui.txt"

echo "== desktop =="
busy 3 30
xvfb-run -a --server-args="-screen 0 1400x900x24" \
  "$BIN" --flat --shoot "$OUT" --shoot-hold 16

echo "== wrote =="
ls -la "$OUT"
