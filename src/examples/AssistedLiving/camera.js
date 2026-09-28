// Stand-in VideoCamera: emits frames at `fps` for `seconds`. A frame in which the resident is recognised
// (fraction `identified`) carries their id in `person` (the paper's frame.id), which makes it `patient`.
// `key=value;key=value` config (inlined: oneos.js runs an instrumented copy of this file elsewhere).
function config(text, defaults) {
    const c = Object.assign({}, defaults);
    for (const part of String(text || '').split(';')) {
        const [k, v] = part.split('=');
        if (k && v !== undefined && k.trim() in c) c[k.trim()] = typeof c[k.trim()] === 'number' ? Number(v) : v.trim();
    }
    return c;
}
const c = config(process.argv[2], { fps: 15, seconds: 30, identified: 0.5, bytes: 4096 });
const payload = Buffer.alloc(c.bytes, 7).toString('base64');
const total = Math.round(c.fps * c.seconds);
let sent = 0;
const timer = setInterval(() => {
    process.stdout.json.write({ ts: Date.now(), person: Math.random() < c.identified ? 'resident-1' : '', data: payload });
    if (++sent >= total) { clearInterval(timer); setTimeout(() => process.exit(0), 1000); }
}, 1000 / c.fps);
