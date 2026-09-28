#!/usr/bin/env bash
# Runs every demo (AAL, FD, SPG) under every system (baseline, ldift, requalizer), one after another.
#
#   scripts/experiment/run-all.sh [OUTPUT_DIR] [extra DemoRunner options...]
#
# Each run writes to OUTPUT_DIR/<app>-<mode>/ (profiles, plan.json, summary.json, summary.txt), and its
# console output to OUTPUT_DIR/<app>-<mode>.log. OUTPUT_DIR defaults to $REQUALIZER_ROOT/data/demo-results-<time>.
# Extra options go to every run, e.g. `--duration 60` or `--log Information`. A failed run doesn't stop the
# others; the exit status is 1 if any failed.
set -uo pipefail

root="$(realpath "$REQUALIZER_ROOT")"
here="$root/src/DemoRunner"
out="${1:-$root/data/demo-results-$(date +%Y%m%d-%H%M%S)}"
shift $(( $# > 0 ? 1 : 0 ))
mkdir -p "$out"
out="$(cd "$out" && pwd)"

echo "Building DemoRunner..."
if ! dotnet build "$here" -v q -nologo; then
    echo "Build failed." >&2
    exit 1
fi

failed=()
for app in AAL FD SPG; do
    for mode in baseline ldift requalizer; do
        name="$app-$mode"
        echo
        echo "=== $name ($(date +%H:%M:%S))"
        if dotnet run --project "$here" --no-build -- --app "$app" --mode "$mode" --output "$out/$name" "$@" > "$out/$name.log" 2>&1; then
            cat "$out/$name/summary.txt"
        else
            echo "FAILED (exit $?); see $out/$name.log"
            failed+=("$name")
        fi
    done
done

echo
if [ ${#failed[@]} -eq 0 ]; then
    echo "All 9 runs finished. Results in $out"
else
    echo "${#failed[@]} of 9 runs failed: ${failed[*]}. Results in $out"
    exit 1
fi
