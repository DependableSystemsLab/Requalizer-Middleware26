// Stand-in WebServer: keeps the latest diagnosis per patient for user sessions (counted; no HTTP listener,
// so the demo needs no ports).
const latest = new Map();
let served = 0;
process.stdin.json.on('data', r => { latest.set(r.patient_id, r); served++; });
process.stdin.json.on('end', () => { console.error(`web-server: ${served} records for ${latest.size} patients`); process.exit(0); });
