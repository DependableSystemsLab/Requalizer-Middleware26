// Stand-in Notifier: dispatches an emergency notification for each detected fall (counted, not sent).
let seen = 0, notified = 0;
process.stdin.json.on('data', r => {
    seen++;
    if (r.vitals && r.vitals.fall) notified++;
});
process.stdin.json.on('end', () => { console.error(`notifier: ${notified} notifications from ${seen} records`); process.exit(0); });
