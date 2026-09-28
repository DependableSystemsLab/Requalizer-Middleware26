const path = require('path');

module.exports = {
	name: 'node-red',
	package: (packageJson) => {
		if ('node-red' in packageJson){
			return {
				name: 'node-red',
				main: packageJson.main,
				files: Object.values(packageJson['node-red'].nodes)
			};
		}
	},
	hooks: {
		'scope.create': (node, scope, context) => {
			// check if this is a node-red component file
			const packageFiles = scope.$root.packageInfo.files ? scope.$root.packageInfo.files.map(file => path.join(scope.$root.packageInfo.mountPath, file)) : [];
			const isMain = scope.$root.packageInfo.main && path.join(scope.$root.packageInfo.mountPath, scope.$root.packageInfo.main) === scope.$root.absSourcePath;
			const hasNodeRedFlag = scope.$root.options && scope.$root.options.isNodeRed;
			if (packageFiles.includes(scope.$root.absSourcePath) || isMain || hasNodeRedFlag){
				
				// check if this is root scope
				if (scope.$root === scope){

				}
				else if (scope.$root === scope.parent){
					// check if this is the declaration of a Node RED component function
					if (context.node.type === 'AssignmentExpression'
						&& context.node.left.type === 'MemberExpression'
						&& context.node.left.object.type === 'Identifier'
						&& context.node.left.object.name === 'module'
						&& context.node.left.property.type === 'Identifier'
						&& context.node.left.property.name === 'exports'){

						scope.isNodeRedBody = true;

						// If it is a node-red body, we need to overwrite the entity
						// for the first parameter

						const par = node.params[0];

						const oldEntity = scope.entity_states.get(par.name);
		                scope.dependency_graph.delete(oldEntity);

		                const entity = scope.createExternalDefinedEntity('node-red.NodeREDRuntimeAPI', par.name, par.loc);

		                // attach the entity to the node so that it can be looked up later
		                // e.g., when generating HTML nodes
		                par.entity = entity;

					}
				}
				else if (scope.parent.isNodeRedBody){
					// check if this function has the call
					// RED.nodes.createNode(this, config)
					const signatures = node.body.body.map(item => item.type === 'ExpressionStatement' ? item.expression : item)
					.filter(item => {
						if (item.type === 'CallExpression'
							&& item.callee.type === 'MemberExpression'
							&& item.callee.object.type === 'MemberExpression'
							&& item.callee.object.object.type === 'Identifier'){

							const entity = scope.getEntity(item.callee.object.object.name);
							return (entity.node.type === 'External:NodeREDRuntimeAPI'
								&& item.callee.object.property.type === 'Identifier'
								&& item.callee.object.property.name === 'nodes'
								&& item.callee.property.type === 'Identifier'
								&& item.callee.property.name === 'createNode');
						}
						return false;
					});

					if (signatures.length > 0){
						// if the signature was found, then this is the
						// body of the Node RED node.
						// We need to set "this" to the NodeRED Node entity
						const entity = scope.createExternalDefinedEntity('node-red.NodeREDNode', 'this', node.loc);
						// console.log(entity);
					}
					
				}
			}
		}
	},
	definitions: [{
		type: 'NodeREDRuntimeAPI',
		properties: {
			'nodes': {
				type: 'Object',
				properties: {
					'createNode': {
						type: 'Function',
						params: ['NodeREDNode', 'Object']
					}
				}
			}
		}
	}, {
		type: 'NodeREDNode',
		properties: {
			'on': {
				type: 'Function',
				params: ['String', 'MessageHandler']
			},
			'send': 'IO:Writer'
		}
	}, {
		type: 'MessageHandler',
		inherits: 'Function',
		params: [
			[],
			['IO:Source'],
			['IO:Source', 'Function'],
			['IO:Source', 'Function', 'Function']
		]
	}]
}