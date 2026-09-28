// Stand-in FallDetector: one record per frame, keeping the frame's capture time (the fast flow's clock);
// flags a fall every `fall_every` frames (no pose model).
const fallEvery = Number(process.argv[2] || 45);
let frames = 0;
process.stdin.json.on('data', frame => {
    frames++;
    const fall = frames % fallEvery === 0;
    process.stdout.json.write({ patient_id: frame.person || 'unknown', ts: frame.ts,
        vitals: { kind: 'fall-detection', fall: fall, confidence: fall ? 0.9 : 0.05 } });
});
process.stdin.json.on('end', () => process.exit(0));
