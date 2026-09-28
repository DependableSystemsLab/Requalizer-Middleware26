if (process.argv.length < 6) {
    console.log('provide input file, output file, agent URI, virtual CWD');
    process.exit(1);
}

const inputPath = process.argv[2];
const outputPath = process.argv[3];
const agentUri = process.argv[4];
const agentCwd = process.argv[5];
const idmEnabled = process.argv[6] === 'true';
const idmPolicyPath = process.argv[7];

const fs = require('fs');
const path = require('path');
const Code = require('./Code.js');
const Turnstile = require('./Turnstile.js');

let source, idmPolicy = null;

// if idmEnabled, instrument with Turnstile first
if (idmEnabled){
    idmPolicy = require(path.resolve(idmPolicyPath));
    source = Turnstile.instrument(inputPath, idmPolicy);
}
else {
    source = fs.readFileSync(inputPath, 'utf8');
}

let instrumented = Code.instrument(source, {
    uri: agentUri,
    filename: path.basename(outputPath),
    cwd: agentCwd,
    idmEnabled: idmEnabled,
    idmPolicy: idmPolicy
});

fs.writeFileSync(outputPath, instrumented, 'utf8');