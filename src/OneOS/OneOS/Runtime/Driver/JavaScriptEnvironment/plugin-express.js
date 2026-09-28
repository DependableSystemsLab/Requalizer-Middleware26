module.exports = {
	name: 'express',
	require: {
		type: 'Function',
		returns: 'Application',
		definitions: [{
			type: 'Application',
			properties: {
				'use': {
					type: 'Function',
					params: ['...MiddlewareFunction'],
					returns: 'Application'
				},
				'get': {
					type: 'Function',
					params: ['Literal', '...MiddlewareFunction'],
					returns: 'Application'
				},
				'post': {
					type: 'Function',
					params: ['Literal', '...MiddlewareFunction'],
					returns: 'Application'
				}
			}
		}, {
			type: 'MiddlewareFunction',
			inherits: 'Function',
			params: [
				['IncomingMessage', 'ServerResponse'],
				['IncomingMessage', 'ServerResponse', 'Function'],
				['Error', 'IncomingMessage', 'ServerResponse', 'Function']
			]
		}, {
			type: 'IncomingMessage',
			inherits: 'IO:Source'
		}, {
			type: 'ServerResponse',
			properties: {
				'send': 'IO:Writer',
				'json': 'IO:Writer'
			}
		}]
	}
}