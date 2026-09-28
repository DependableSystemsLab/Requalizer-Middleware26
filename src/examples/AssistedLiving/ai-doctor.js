// Stand-in AIDoctor: a diagnosis per health record (a fixed rule instead of the model in argv[2]).
process.stdin.json.on('data', r => {
    const v = r.vitals || {};
    const risk = v.fall ? 'high' : (v.heartRate > 95 || v.spo2 < 95 ? 'elevated' : 'normal');
    process.stdout.json.write({ patient_id: r.patient_id, ts: r.ts, vitals: { kind: 'diagnosis', risk: risk, basis: v.kind } });
});
process.stdin.json.on('end', () => process.exit(0));
