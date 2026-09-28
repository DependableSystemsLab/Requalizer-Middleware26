// Stand-in RecordManager: merges detector events and wearable readings (one input port, both edges) into
// health records for the AI doctor.
const history = new Map();
process.stdin.json.on('data', r => {
    const h = history.get(r.patient_id) || { events: 0, readings: 0 };
    if (r.vitals && r.vitals.kind === 'wearable') h.readings++; else h.events++;
    history.set(r.patient_id, h);
    process.stdout.json.write({ patient_id: r.patient_id, ts: r.ts, vitals: Object.assign({ history: h }, r.vitals) });
});
process.stdin.json.on('end', () => process.exit(0));
