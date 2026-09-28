// Stand-in Wearable: vital-sign readings (PPG, accelerometer, ECG summaries) at `hz` for `seconds`.
// `key=value;key=value` config (inlined: oneos.js runs an instrumented copy of this file elsewhere).
function config(text, defaults) {
    const c = Object.assign({}, defaults);
    for (const part of String(text || '').split(';')) {
        const [k, v] = part.split('=');
        if (k && v !== undefined && k.trim() in c) c[k.trim()] = typeof c[k.trim()] === 'number' ? Number(v) : v.trim();
    }
    return c;
}
const c = config(process.argv[2], { hz: 2, seconds: 30 });
const total = Math.round(c.hz * c.seconds);
let sent = 0;
const timer = setInterval(() => {
    process.stdout.json.write({ patient_id: 'resident-1', ts: Date.now(),
        vitals: { kind: 'wearable', heartRate: 60 + Math.round(Math.random() * 40), spo2: 94 + Math.round(Math.random() * 5), accel: Math.random() } });
    if (++sent >= total) { clearInterval(timer); setTimeout(() => process.exit(0), 1000); }
}, 1000 / c.hz);
