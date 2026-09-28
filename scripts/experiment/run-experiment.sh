#!/usr/bin/env bash
# Runs the whole experiment and prepares its data for the figures:
#   1. every demo (AAL, FD, SPG) under every system (baseline, ldift, requalizer), with run-all.sh, into OUTPUT_DIR/raw;
#   2. the runs converted to per-message data-<system>-<app>.csv files (compile-demo-results.py), into OUTPUT_DIR;
#   3. those compiled into plot-latency.csv and plot-throughput.csv (compile-raw-files.js), into OUTPUT_DIR.
#
#   scripts/experiment/run-experiment.sh [OUTPUT_DIR] [extra DemoRunner options...]
#
# OUTPUT_DIR defaults to $REQUALIZER_OUTPUT_ROOT/experiment-<time> ($REQUALIZER_ROOT/data/experiment-<time> if
# REQUALIZER_OUTPUT_ROOT isn't set). Extra options go to every run, e.g. `--duration 60`; the output directory
# must then be given too.
set -uo pipefail

root="$(realpath "$REQUALIZER_ROOT")"
experiment="$root/scripts/experiment"
presentation="$root/scripts/presentation"
out="${1:-${REQUALIZER_OUTPUT_ROOT:-$root/data}/experiment-$(date +%Y%m%d-%H%M%S)}"
shift $(( $# > 0 ? 1 : 0 ))
mkdir -p "$out"
out="$(cd "$out" && pwd)"

echo "=== 1/3: running every demo under every system, into $out/raw"
if ! "$experiment/run-all.sh" "$out/raw" "$@"; then
    echo "Some runs failed; stopping. Their logs are in $out/raw." >&2
    exit 1
fi

echo
echo "=== 2/3: converting the runs into per-message data files"
if ! python3 "$presentation/compile-demo-results.py" "$out/raw" "$out"; then
    echo "Converting the runs failed." >&2
    exit 1
fi

echo
echo "=== 3/3: compiling the data files into the figures' data"
if ! node "$presentation/compile-raw-files.js" "$out" "$out"; then
    echo "Compiling the data files failed." >&2
    exit 1
fi

echo
echo "Done. The figures' data:"
echo "  $out/plot-latency.csv"
echo "  $out/plot-throughput.csv"
echo "To render them, in $presentation with its .venv activated:"
echo "  python plot-latency-histogram.py $out/plot-latency.csv"
echo "  python plot-throughput-snapshot.py $out/plot-throughput.csv"
