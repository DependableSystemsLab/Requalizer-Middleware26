#!/usr/bin/env bash
# Runs one demo under one system.
#
#   scripts/experiment/run-one.sh MODE APP [OUTPUT_DIR] [extra DemoRunner options...]
#
#   MODE        baseline | ldift | requalizer (or aware)
#   APP         AAL | FD | SPG
#   OUTPUT_DIR  where the profiles, plan.json, summary.json and summary.txt go
#               (default ./demo-results-<time>/<app>-<mode>)
#
# Extra options go to the DemoRunner, e.g. `--duration 60` or `--log Information`; the output directory must
# then be given too. The run's console output goes to the terminal; the summary is printed at the end.
set -uo pipefail

usage() {
    echo "usage: $0 baseline|ldift|requalizer AAL|FD|SPG [OUTPUT_DIR] [DemoRunner options...]" >&2
    exit 2
}

[ $# -ge 2 ] || usage
mode="$(echo "$1" | tr '[:upper:]' '[:lower:]')"
app="$(echo "$2" | tr '[:lower:]' '[:upper:]')"
case "$mode" in baseline|ldift|requalizer|aware) ;; *) echo "unknown mode '$1'" >&2; usage ;; esac
case "$app" in AAL|FD|SPG) ;; *) echo "unknown app '$2'" >&2; usage ;; esac
shift 2

here="$(realpath "$REQUALIZER_ROOT")/src/DemoRunner"
out="${1:-demo-results-$(date +%Y%m%d-%H%M%S)/$app-$mode}"
shift $(( $# > 0 ? 1 : 0 ))

dotnet run --project "$here" -- --app "$app" --mode "$mode" --output "$out" "$@"
