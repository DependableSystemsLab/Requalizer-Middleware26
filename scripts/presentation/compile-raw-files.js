if (process.argv.length < 4){
	console.log('Provide 2 paths, corresponding to input directory and output directory');
	process.exit();
}

const path = require('path');
const fs = require('fs');

const inputDir = path.resolve(process.argv[2] || '.');
const outputDir = path.resolve(process.argv[3] || '.');

const OUTPUT_LAT = path.join(outputDir, 'plot-latency.csv');
const OUTPUT_THR = path.join(outputDir, 'plot-throughput.csv');

const SYSTEM_NAMES = {
	'baseline': 'Baseline',
	'ldift': 'L-DIFT',
	'codift': 'Requalizer'
};
const APP_NAMES = {
	'aal': 'AAL',
	'fd': 'FD',
	'sg': 'SG'
};

const csvLatency = [[ 'Application', 'Configuration', 'Latency (ms)' ]];
const csvThroughput = [[ 'Application', 'Configuration', 'Time (sec)', 'Throughput (msg/s)' ]];

for (let sysname in SYSTEM_NAMES){
	for (let appname in APP_NAMES){
		const dataPath = path.join(inputDir, `data-${sysname}-${appname}.csv`);
		const rows = fs.readFileSync(dataPath, 'utf8').split('\n').map(line => line.split(',').map(item => parseInt(item)));

		const t0 = rows[0][0];
		let nextTputSample = 0;

		rows.forEach(row => {
			// pre-process the data by normalizing the timestamp
			row[0] = row[0] - t0;
			row[1] = row[1] - t0;

			csvLatency.push([ APP_NAMES[appname], SYSTEM_NAMES[sysname], row[2] ]);
			if (nextTputSample <= row[0]){
				csvThroughput.push([ APP_NAMES[appname], SYSTEM_NAMES[sysname], row[1], row[3] ]);
				nextTputSample += 200;
			}
		});
	}
}

fs.writeFileSync(OUTPUT_LAT, csvLatency.map(line => line.join(',')).join('\n'));
fs.writeFileSync(OUTPUT_THR, csvThroughput.map(line => line.join(',')).join('\n'));
