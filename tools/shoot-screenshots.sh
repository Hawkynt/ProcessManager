#!/usr/bin/env bash
# Regenerates docs/screenshots. Run from the repository root; needs a build and Xvfb.
#
# The machine is given some real work first. A screenshot of a monitor on an idle machine shows a
# table of zeroes and empty plots, which is honest and tells a reader nothing about what the program
# does — so a small workload runs for the length of the capture, and what the picture shows is a real
# measurement of it.
#
# The capture runs inside a private PID namespace, so the process table holds only the workload
# started below and the program itself. Everything else stays real: the processor meters, the memory
# figures, the adapters and the disks are read from files the namespace does not touch, so the
# picture is a true reading of this machine doing this work.
#
# That is a deliberate trade. These images are committed to a public repository, and a screenshot of
# a process monitor is a photograph of whatever its author happened to be running — every program
# name, every command line, every path under their home directory. Publishing that is nobody's
# intention when they regenerate a screenshot, and it is not something a reader of the README needs
# in order to see what the program does.
set -euo pipefail

BIN="${1:-ProcessManager.App/bin/Release/net10.0/procman}"
OUT="docs/screenshots"
mkdir -p "$OUT"

# Unprivileged user namespaces are how this is possible without root. Where they are switched off,
# the capture still runs — a build server has nothing personal on it to leak — but it says so, so
# that a picture taken on somebody's desktop is never quietly a picture of their desktop.
ISOLATE=(unshare --user --map-current-user --fork --pid --mount-proc)
if ! "${ISOLATE[@]}" true 2>/dev/null; then
  echo "!! no private PID namespace here: the capture will show every process on this machine."
  echo "!! check the images before committing them if this is not a build server."
  ISOLATE=(env)
fi

# Real work, from tools every machine has: something spinning, something moving bytes, something
# holding memory, something asleep. Named after what they are, because that is what they are.
WORKLOAD='
  for _ in 1 2 3; do ( timeout "$HOLD" bash -c "while :; do :; done" >/dev/null 2>&1 & ); done
  ( timeout "$HOLD" dd if=/dev/zero of=/dev/null bs=1M >/dev/null 2>&1 & )
  ( timeout "$HOLD" gzip -c /dev/zero >/dev/null 2>&1 & )
  ( timeout "$HOLD" sort -R /dev/urandom >/dev/null 2>&1 & )
  ( timeout "$HOLD" sleep "$HOLD" >/dev/null 2>&1 & )
  sleep 1
'

echo "== terminal =="
HOLD=20 "${ISOLATE[@]}" bash -c "$WORKLOAD"'
  "$0" --tui --capture-samples 18 --interval 0.4 \
    --capture-svg "$1/tui.svg" --capture-frame "$1/tui.txt"
' "$BIN" "$OUT"

echo "== desktop =="
HOLD=30 "${ISOLATE[@]}" bash -c "$WORKLOAD"'
  xvfb-run -a --server-args="-screen 0 1400x900x24" \
    "$0" --flat --shoot "$1" --shoot-hold 16
' "$BIN" "$OUT"

echo "== wrote =="
ls -la "$OUT"
