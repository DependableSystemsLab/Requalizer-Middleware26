"""
Converts DemoRunner output (the 9 <APP>-<mode> folders that scripts/experiment/run-all.sh writes) into the
per-message data-<system>-<app>.csv files that compile-raw-files.js reads.

    python compile-demo-results.py RESULTS_DIR [OUTPUT_DIR]

RESULTS_DIR holds one folder per run, each with run.json and profiles/*/messages.jsonl. Each run is identified
by its run.json (app, mode), not by its folder name. OUTPUT_DIR defaults to $REQUALIZER_OUTPUT_ROOT (the current
directory if it's not set).

Each output row is one message that reached a sink on the app's measured paths (PATHS; SPG's two sinks
together), ordered by when it left its source, with no header:

    send time (Unix ms), arrival time (Unix ms), latency (ms), throughput (bytes/s), throughput (msg/s),
    sequence number (1..N), message size (bytes)

Throughput at a message is what arrived at the sinks in the 1 second before that message arrived.
"""
import bisect
import glob
import json
import os
import sys

SYSTEMS = {'baseline': 'baseline', 'ldift': 'ldift', 'requalizer': 'codift'}
APPS = {'AAL': 'aal', 'FD': 'fd', 'SPG': 'sg'}
# The source -> sink paths measured per app. AAL's doctor -> webServer is left out: under requalizer its last
# edge is a bypass pipe, whose messages skip the sidecar and aren't logged, so only camera -> notifier is
# measured in every system.
PATHS = {
    'AAL': {('camera', 'notifier')},
    'FD': {('spout', 'sink')},
    'SPG': {('spout', 'oSink'), ('spout', 'pSink')},
}
WINDOW_MS = 1000.0


def load_runs(results_dir):
    runs = {}
    for run_file in sorted(glob.glob(os.path.join(results_dir, '*', 'run.json'))):
        run_dir = os.path.dirname(run_file)
        with open(run_file) as f:
            run = json.load(f)
        key = (run['app'], run['mode'])
        if key in runs:
            sys.exit(f"Error: {runs[key]} and {run_dir} are both {key[0]} / {key[1]} runs")
        runs[key] = run_dir
    return runs


def read_messages(run_dir, paths):
    # The graph instance of the run, so that nothing from another instance is mixed in.
    instance = None
    summary_file = os.path.join(run_dir, 'summary.json')
    if os.path.exists(summary_file):
        with open(summary_file) as f:
            instance = json.load(f).get('instance')

    files = glob.glob(os.path.join(run_dir, 'profiles', '*', 'messages.jsonl'))
    if not files:
        return None
    messages = []
    for path in files:
        with open(path) as f:
            for line in f:
                if not line.strip():
                    continue
                m = json.loads(line)
                if (instance is None or m['graph'] == instance) and (m['from'], m['to']) in paths:
                    messages.append(m)
    return messages


def to_rows(messages):
    # Throughput from the arrival order: bytes and messages that arrived in the preceding WINDOW_MS.
    arrivals = sorted(messages, key=lambda m: m['arrivedMs'])
    times = [m['arrivedMs'] for m in arrivals]
    cumulative_bytes = [0]
    for m in arrivals:
        cumulative_bytes.append(cumulative_bytes[-1] + m['bytes'])
    for i, m in enumerate(arrivals):
        start = bisect.bisect_right(times, m['arrivedMs'] - WINDOW_MS)
        m['_bytesPerSec'] = (cumulative_bytes[i + 1] - cumulative_bytes[start]) * 1000.0 / WINDOW_MS
        m['_msgPerSec'] = (i + 1 - start) * 1000.0 / WINDOW_MS

    rows = []
    for seq, m in enumerate(sorted(messages, key=lambda m: m['originMs']), start=1):
        rows.append([
            f"{m['originMs']:.3f}",
            f"{m['arrivedMs']:.3f}",
            f"{m['latencyUs'] / 1000.0:.3f}",
            f"{m['_bytesPerSec']:.1f}",
            f"{m['_msgPerSec']:.1f}",
            str(seq),
            str(m['bytes']),
        ])
    return rows


def main():
    if len(sys.argv) < 2:
        print("Usage: python compile-demo-results.py RESULTS_DIR [OUTPUT_DIR]")
        sys.exit(1)

    results_dir = sys.argv[1]
    output_dir = sys.argv[2] if len(sys.argv) > 2 else os.environ.get("REQUALIZER_OUTPUT_ROOT", ".")
    os.makedirs(output_dir, exist_ok=True)

    runs = load_runs(results_dir)
    missing = []
    for app, app_name in APPS.items():
        for mode, system_name in SYSTEMS.items():
            run_dir = runs.get((app, mode))
            if run_dir is None:
                missing.append(f"{app} / {mode}")
                continue
            messages = read_messages(run_dir, PATHS[app])
            if messages is None:
                print(f"Skipping {run_dir}: no profiles/*/messages.jsonl (made by a DemoRunner without the per-message log?)")
                missing.append(f"{app} / {mode}")
                continue
            if not messages:
                print(f"Skipping {run_dir}: no messages reached a sink")
                missing.append(f"{app} / {mode}")
                continue

            out_file = os.path.join(output_dir, f"data-{system_name}-{app_name}.csv")
            with open(out_file, "w") as f:
                f.write("\n".join(",".join(row) for row in to_rows(messages)))
            print(f"{app} / {mode}: {len(messages)} messages -> {out_file}")

    if missing:
        print(f"\nNot produced ({len(missing)}): {', '.join(missing)}")
        sys.exit(1)


if __name__ == "__main__":
    main()
