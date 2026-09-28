if (process.argv.length < 3) {
    console.log("Provide file path. E.g., node TransactionParser.js $filePath [records]");
    process.exit();
}

var filePath = process.argv[2];
// Output format: CSV lines as segments (default; frauddetection.osh), or with `records`, transaction records
// as newline-delimited JSON (fraud_demo.osh): { ts, sensitivity, entityId, transactionId, state }, where
// `ts` is the ingestion time (ms) and `sensitivity` is drawn uniformly from 0, 1, 2 (the demo's labels,
// as in the Requalizer paper's experiment).
var records = process.argv[3] === 'records';

var fs = require('fs');

function parse(line) {
    if (!records) {
        process.stdout.segment.write(Buffer.from(line));
        return;
    }
    const fields = line.split(',');
    if (fields.length < 3) return;
    process.stdout.json.write({
        ts: Date.now(),
        sensitivity: Math.floor(Math.random() * 3),
        entityId: fields[0],
        transactionId: fields[1],
        state: fields[2]
    });
}

var stream = fs.createReadStream(filePath);
//stream.pipe(process.stdout);
var buf = '';
stream.on('data', chunk => {
    const text = chunk.toString('utf8');
    for (let i = 0; i < text.length; i++) {
        if (text[i] === '\n') {
            parse(buf);
            buf = '';
        }
        else {
            buf += text[i];
        }
    }
});
stream.on('end', () => {
    if (buf) {
        parse(buf);
        buf = '';
    };

    setTimeout(() => {
        process.exit();
    }, 1000);
});