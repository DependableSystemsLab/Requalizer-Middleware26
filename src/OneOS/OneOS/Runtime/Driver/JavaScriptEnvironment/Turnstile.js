const esprima = require('./esprima.js');
const escodegen = require('escodegen');
const PrivacyTracker = require('./PrivacyTracker.js');
const CodeAnalyzer = require('./CodeAnalyzer.js');
const expressPlugin = require('./plugin-express.js');
const nodeRedPlugin = require('./plugin-node-red.js');

const INSTRUMENT_ALL = (process.env.INSTRUMENT_ALL === 'true') || false;	// flag to indicate whether to selectively instrument privacy-sensitive paths or instrument all expressions.

// We use these greek letter identifiers/prefixes to avoic potential naming conflicts.
// This is purely based on the assumption that there is a very low probability of these
// characters being used by the application developer.
const TRACKER_NAME = 'Σ.tracker';			// identifier for the tracker object
const ANONYMOUS_PREFIX = 'α';		// prefix for anonymous functions

const injectionsMap = new Map();
const flowMap = new Map();

const tainter = (entity, flow) => {
	if (!flowMap.has(entity.node)){
		flowMap.set(entity.node, new Set());
	}

	const flowsOverNode = flowMap.get(entity.node);
	flowsOverNode.add(flow.id);

	// TODO: revise whether tainting the derivedFrom AST node as below is necessary
	if (entity.derivedFrom){
		if (!flowMap.has(entity.derivedFrom)){
			flowMap.set(entity.derivedFrom, new Set());
		}
		flowMap.get(entity.derivedFrom).add(flow.id);
	}
};

function getScopeInjections(scope){
	let scopeInjections = injectionsMap.get(scope);
	if (!scopeInjections){
		scopeInjections = [];
		injectionsMap.set(scope, scopeInjections);
	}
	return scopeInjections;
}

function registerInjection(scope, injectionType, targetRefName, handlerName){
	const scopeInjections = getScopeInjections(scope);

	const entity = scope.getEntity(targetRefName);

	if (scope.params.includes(targetRefName)
		|| scope.hoisted.includes(targetRefName)){
		scopeInjections.push({
			type: injectionType,
			target: targetRefName,
			handler: handlerName,
			leading: true
		});
	}
	else if (entity.scope.isAncestorOf(scope)){
		scopeInjections.push({
			type: injectionType,
			target: targetRefName,
			handler: handlerName,
			leading: true,
			outerScope: true
		});
	}
	else {
		scopeInjections.push({
			type: injectionType,
			target: targetRefName,
			handler: handlerName,
			leading: false
		});
	}

	scope.$root.tracer.traceUpstream(entity, tainter);
	scope.$root.tracer.traceDownstream(entity, tainter);
}

// returns true if the comment was a tracker annotation.
// If true, it will be removed from the comments list
// While processing, it will register the injection in the scope
function processComment(comment, scope){
	const regex = /\s*\$(\w+)\s+(\s*\w+\s*(?::\s*\w+\s*)?(?:,\s*\w+\s*(?::\s*\w+\s*)?)*)/;

	if (comment.type === 'Line'){
		const match = comment.value.match(regex);
		if (match){
			const keyword = match[1];
			const operands = match[2].split(',').map(item => item.split(':').map(token => token.trim()));

			if (keyword === 'label'){
				operands.forEach(item => {
					registerInjection(scope, 'label', item[0], item[1] || item[0]);
				});
			}
			else if (keyword === 'issuer'){
				operands.forEach(item => {
					registerInjection(scope, 'issuer', item[0], item[1] || item[0]);
				});
			}
			else if (keyword === 'serialized'){
				// this is a hacky keyword to be used in our experiments only
				operands.forEach(item => {
					registerInjection(scope, 'serialized', item[0], item[1] || item[0]);
				});
			}

			return true;
		}
	}

	return false;
}

function injectLeadingLabellers(scope, node){
	const injections = getScopeInjections(scope).filter(item => item.leading)
		.map(item => {
			const entity = scope.getEntity(item.target);
			if (!entity){
				throw new Error(`[Instrumentor]\tAttempting to add a ${item.type} handler for "${item.target}", but there is no entity associated with "${item.target}"`);
			}

			if (!flowMap.has(entity.node)){
				console.log(`[Instrumentor]\tAdded a ${item.type} handler for "${item.target}" (in line ${entity.node.loc.start.line}), but "${item.target}" is not part of any security-sensitive dataflow`);

				// scope.$root.tracer.traceUpstream(entity, (node, flow) => {
				// 	console.log(node.node);
				// }, (node, flow) => (node.node.type === 'FunctionEnter'));
				// scope.$root.tracer.traceDownstream(entity, (node, flow) => {
				// 	console.log(node.node);
				// }, (node, flow) => (node.node.type === 'FunctionReturn'));
			}

			// return the instrumented code string
			const assignment = item.outerScope ? '' : `${item.target} = `;
			return `${assignment}${TRACKER_NAME}.${item.type}(${item.target}, '${item.handler}')`;
		});

	const injected = esprima.parse(injections.join(';')).body;
	node.body.unshift.apply(node.body, injected);
}

// node is a VariableDeclarator
function tryInjectWrappedLabeller(scope, node){
	const scopeInjections = getScopeInjections(scope);
	const index = scopeInjections.findIndex(item => item.target === node.id.name && !item.leading);

	if (index > -1){
		const replace = esprima.parse(`${TRACKER_NAME}.${scopeInjections[index].type}('${scopeInjections[index].handler}')`).body[0].expression;
		replace.arguments.unshift(node.init);
		node.init = replace;

		scopeInjections.splice(index, 1);
	}
}

// helper function to get the left-most object in a MemberExpression
function getFirstMember(expr){
	if (expr.type === 'MemberExpression'){
		return getFirstMember(expr.object);
	}
	else return expr;
}

// `Σ.tracker.<method>(...args)` as an AST node.
function trackerCall(method, args){
	const call = esprima.parse(`${TRACKER_NAME}.${method}()`).body[0].expression;
	call.arguments = args;
	return call;
}

function stringLiteral(value){
	return esprima.parse(JSON.stringify(value)).body[0].expression;
}

// Whether an expression contains a node of the given type (AwaitExpression, YieldExpression) outside any
// function nested in it.
function contains(node, type, visited = new Set()){
	if (!node || typeof node !== 'object' || visited.has(node)) return false;
	visited.add(node);
	if (node.type === type) return true;
	if (node.type === 'FunctionExpression' || node.type === 'ArrowFunctionExpression' || node.type === 'FunctionDeclaration') return false;
	return Object.keys(node).some(key => {
		if (key === 'scope' || key === 'loc' || key === 'range' || key.endsWith('Comments')) return false;
		const child = node[key];
		if (Array.isArray(child)) return child.some(item => item && typeof item.type === 'string' && contains(item, type, visited));
		return child && typeof child.type === 'string' && contains(child, type, visited);
	});
}

const EQUALITY_OPERATORS = [ '===', '!==', '==', '!=' ];

// We assume that the AST node passed to this function
// has been processed by the CodeAnalyzer, thus carrying additional scope information
function instrumentNode(node, scope) {
    if (instrumentNode.Handlers[node.type]) {
    	
    	// If any comments are found, process them if they contain tracker annotations
    	if (node.leadingComments){
    		node.leadingComments = node.leadingComments.filter((comment) => !processComment(comment, scope));
    	}

        return instrumentNode.Handlers[node.type](node, scope);
    }
    else {
    	console.log(`[Instrumentor]\tWARN: Unsupported expression "${node.type}" in line ${node.loc.start.line}:${node.loc.start.column}`);
    }
    return node;
}
instrumentNode.Handlers = {
	Program: (node) => {
		node.body.forEach((child, index, list) => {
            list[index] = instrumentNode(child, node.scope);
        });

		// we inject leading labellers after going through the body,
		// because we don't want the injected nodes to be instrumented again
        injectLeadingLabellers(node.scope, node);

        return node;
	},
	Literal: (node, scope, context) => node,
	TemplateLiteral: (node, scope, context) => {
		if (flowMap.has(node) || INSTRUMENT_ALL){
			const replace = esprima.parse(`${TRACKER_NAME}.interpolate(args => 'placeholder', [])`).body[0].expression;
	        replace.arguments[1].elements = node.expressions;

	        node.expressions = node.expressions.map((item, index) => {
	            return esprima.parse(`args[${index}]`).body[0].expression;
	        });

	        replace.arguments[0].body = node;

	        return replace;
		}

		return node;
    },
	Identifier: (node, scope, context) => node,
	ExpressionStatement: (node, scope, context) => {
        node.expression = instrumentNode(node.expression, scope, context);

        return node;
    },
	SequenceExpression: (node, scope, context) => {
		node.expressions.forEach((child, index, list) => {
			list[index] = instrumentNode(child, scope, context);
		});
        return node;
	},
	VariableDeclaration: (node, scope, context) => {
		node.declarations.forEach((child, index, list) => {
            list[index] = instrumentNode(child, scope, context);
        });
        return node;
	},
	VariableDeclarator: (node, scope, context) => {
        if (node.init){
        	node.init = instrumentNode(node.init, scope, context);

        	tryInjectWrappedLabeller(scope, node);
        }

        return node;
    },
	UpdateExpression: (node, scope, context) => node,
	UnaryExpression: (node, scope, context) => {
		// `delete` works on a reference, not a value
		if (node.operator === 'delete') return node;

		node.argument = instrumentNode(node.argument, scope, context);
		if (node.operator === 'void') return node;

		if (node.operator === 'typeof'){
			// The type of a wrapped primitive is its value's. `typeof x` must not throw for an undeclared x,
			// so x is read only once it is known to be declared (and an object).
			if (node.argument.type === 'Identifier'){
				const name = node.argument.name;
				return esprima.parse(`(typeof ${name} === 'object' ? ${TRACKER_NAME}.typeOf(${name}) : typeof ${name})`).body[0].expression;
			}
			return trackerCall('typeOf', [ node.argument ]);
		}

		if (flowMap.has(node) || INSTRUMENT_ALL){
			// part of the security-sensitive dataflow: the result carries the operand's label
			return trackerCall('unaryOp', [ stringLiteral(node.operator), node.argument ]);
		}

		// `!x` must see the value, not the wrapper (an object is always truthy); `-x`, `+x` and `~x` read the
		// value through valueOf
		if (node.operator === '!') return trackerCall('not', [ node.argument ]);

		return node;
	},
	BinaryExpression: (node, scope, context) => {
		node.left = instrumentNode(node.left, scope, context);
    	node.right = instrumentNode(node.right, scope, context);

		if (flowMap.has(node) || INSTRUMENT_ALL){
			// this node is part of the security-sensitive dataflow
			const replace = esprima.parse(`${TRACKER_NAME}.binaryOp('${node.operator}')`).body[0].expression;
    		replace.arguments.push(node.left);
    		replace.arguments.push(node.right);

    		return replace;
		}

		// Equality compares objects by identity, and a wrapped primitive is an object: compare the values.
		// Other operators read a wrapped primitive's value through valueOf.
		if (EQUALITY_OPERATORS.includes(node.operator)){
			return trackerCall('equals', [ stringLiteral(node.operator), node.left, node.right ]);
		}

		return node;
	},
	LogicalExpression: (node, scope, context) => {
		node.left = instrumentNode(node.left, scope, context);
		node.right = instrumentNode(node.right, scope, context);

		// `a && b`, `a || b` and `a ?? b` decide on the value of `a`, not its wrapper. `b` becomes a function so
		// it is still evaluated only when picked: an async one if it awaits. A `yield` can't move into a
		// function, so such an expression is left as it is.
		if (contains(node.right, 'YieldExpression')) return node;

		const awaits = contains(node.right, 'AwaitExpression');
		const thunk = esprima.parse(`(${awaits ? 'async ' : ''}() => null)`).body[0].expression;
		thunk.body = node.right;

		const replace = trackerCall(awaits ? 'logicalAsync' : 'logical', [ stringLiteral(node.operator), node.left, thunk ]);
		return awaits ? { type: 'AwaitExpression', argument: replace } : replace;
	},
	ConditionalExpression: (node, scope, context) => {
		node.test = instrumentNode(node.test, scope);
        node.consequent = instrumentNode(node.consequent, scope);
        node.alternate = instrumentNode(node.alternate, scope);

        const replace = esprima.parse(`${TRACKER_NAME}.bool(null)`).body[0].expression;
		replace.arguments[0] = node.test;
		node.test = replace;

		return node;
	},
	AssignmentExpression: (node, scope, context) => {
        node.right = instrumentNode(node.right, scope, context);

        if (flowMap.has(node.left) || INSTRUMENT_ALL){
        	if (node.left.type === 'Identifier'){
        		const replace = esprima.parse(`${TRACKER_NAME}.check(null, ${node.left.name})`).body[0].expression;
                replace.arguments[0] = node.right;
                node.right = replace;
        	}
        	else if (node.left.type === 'MemberExpression'){
        		const replace = esprima.parse(`${TRACKER_NAME}.check(null, ${escodegen.generate(node.left.object)})`).body[0].expression;
                replace.arguments[0] = node.right;
                node.right = replace;
        	}
        }

        return node;
    },
	FunctionDeclaration: (node, scope, context) => {
		if (node.body.type == 'BlockStatement') {
            node.body.body.forEach((child, index, list) => {
                list[index] = instrumentNode(child, node.scope, context);
            });

            // we inject leading labellers after going through the body,
			// because we don't want the injected nodes to be instrumented again
            injectLeadingLabellers(node.scope, node.body);
        }
        // if arrow function
        else {
            node.body = instrumentNode(node.body, node.scope, context);
        }

        return node;
	},
	FunctionExpression: (node, scope, context) => {
        // If function is anonymous, assign some name. We need the name for migration
        instrumentNode.Handlers.FunctionDeclaration(node, scope, context);

        return node;
    },
	ArrowFunctionExpression: (node, scope, context) => {
        // If function is anonymous, assign some name. We need the name for migration
        instrumentNode.Handlers.FunctionDeclaration(node, scope, context);

        return node;
    },
	ReturnStatement: (node, scope, context) => {
		if (node.argument){
			node.argument = instrumentNode(node.argument, scope, context);
		}
		return node;
	},
	CallExpression: (node, scope, context) => {
		node.callee = instrumentNode(node.callee, scope, context);

		node.arguments.forEach((child, index, list) => {
            list[index] = instrumentNode(child, scope, context);
        });

        if (node.callee.type === 'Identifier' && node.callee.name === 'require'){
        	const replace = esprima.parse(`${TRACKER_NAME}.require`).body[0].expression;
        	node.callee = replace;

        	return node;
        }

        if (flowMap.has(node) || INSTRUMENT_ALL){
        	if (node.callee.type === 'MemberExpression'
        		&& node.callee.object.name === 'console'){
        		return node;
        	}
        	// console.log(`CallExpression <${escodegen.generate(node)}> at line ${node.loc.start.line} is part of the flow`);

        	const replace = esprima.parse(`${TRACKER_NAME}.invoke(null, null, [])`).body[0].expression;
        	if (node.callee.type === 'MemberExpression'){
        		replace.arguments[0] = node.callee.object;
        		if (node.callee.computed){
        			replace.arguments[1] = node.callee.property;
        		}
        		else {
        			replace.arguments[1] = esprima.parse(`"${node.callee.property.name}"`).body[0].expression;
        		}
        	}
        	else if (node.callee.type === 'Identifier' || node.callee.type === 'CallExpression'){
        		replace.arguments[1] = node.callee;
        	}
        	replace.arguments[2].elements = node.arguments;
        	return replace;
        }

        return node;
	},
	ObjectExpression: (node, scope, context) => {
        node.properties.forEach((child, index, list) => {
            list[index] = instrumentNode(child, scope, context);
        });

        return node;
    },
	Property: (node, scope, context) => {
		node.value = instrumentNode(node.value, scope, context);
		return node;
	},
	ArrayExpression: (node, scope, context) => {
        node.elements.forEach((child, index, list) => {
            list[index] = instrumentNode(child, scope, context);
        });

        return node;
    },
	MemberExpression: (node, scope, context) => {

    	node.object = instrumentNode(node.object, scope, context);
    	node.property = instrumentNode(node.property, scope, context);

    	return node;
    },
	NewExpression: (node, scope, context) => {
		node.callee = instrumentNode(node.callee, scope, context);

		node.arguments.forEach((child, index, list) => {
            list[index] = instrumentNode(child, scope, context);
        });

        return node;
	},
	ThisExpression: (node, scope, context) => node,
	Super: (node, scope, context) => node,
	ClassDeclaration: (node, scope, context) => node,
	ClassExpression: (node, scope, context) => node,
	MethodDefinition: (node, scope, context) => node,
	IfStatement: (node, scope, context) => {
		const replace = esprima.parse(`${TRACKER_NAME}.bool(null)`).body[0].expression;
		replace.arguments[0] = instrumentNode(node.test, scope, context);
		node.test = replace;

        // if body is not a block, force it to be a block
        // so that we can inject code into the body
        if (node.consequent.type !== 'BlockStatement') {
            var block = esprima.parse('{}').body[0];
            block.body.push(node.consequent);
            node.consequent = block;
        }

        node.consequent.body.forEach((child, index, list) => {
            list[index] = instrumentNode(child, scope, context);
        });

        if (node.alternate) {
            if (node.alternate.type == 'IfStatement') {
                node.alternate = instrumentNode(node.alternate, scope, context);
            }
            else {
                // if body is not a block, force it to be a block
                // so that we can inject code into the body
                if (node.alternate.type !== 'BlockStatement') {
                    var block = esprima.parse('{}').body[0];
                    block.body.push(node.alternate);
                    node.alternate = block;
                }

                node.alternate.body.forEach((child, index, list) => {
                    list[index] = instrumentNode(child, scope, context);
                });
            }
        }

        return node;
    },
	SwitchStatement: (node, scope, context) => {

        // cases match with ===, which must see values, not wrappers
        node.discriminant = trackerCall('value', [ instrumentNode(node.discriminant, scope, context) ]);

        node.cases.forEach((child, index, list) => {
            list[index] = instrumentNode(child, scope, context);
        });

        return node;
    },
	SwitchCase: (node, scope, context) => {
		if (node.test){
			node.test = trackerCall('value', [ instrumentNode(node.test, scope, context) ]);
		}
        node.consequent.forEach((child, index, list) => {
            list[index] = instrumentNode(child, scope, context);
        });
        return node;
    },
	BlockStatement: (node, scope, context) => {
		node.body.forEach((child, index, list) => {
            list[index] = instrumentNode(child, scope, context);
        });
        return node;
	},
	ForStatement: (node, scope, context) => {
		if (node.init){
			node.init = instrumentNode(node.init, scope, context);
		}
		if (node.test){
			node.test = trackerCall('bool', [ instrumentNode(node.test, scope, context) ]);
		}
		if (node.update){
			node.update = instrumentNode(node.update, scope, context);
		}

        // if body is not a block, force it to be a block
        // so that we can inject code into the body
        if (node.body.type !== 'BlockStatement') {
            var block = esprima.parse('{}').body[0];
            block.body.push(node.body);
            node.body = block;
        }

        node.body.body.forEach((child, index, list) => {
            list[index] = instrumentNode(child, scope, context);
        });

        return node;
    },
	ForOfStatement: (node, scope, context) => {
		node.right = instrumentNode(node.right, scope, context);

        // if body is not a block, force it to be a block
        // so that we can inject code into the body
        if (node.body.type !== 'BlockStatement') {
            var block = esprima.parse('{}').body[0];
            block.body.push(node.body);
            node.body = block;
        }

        node.body.body.forEach((child, index, list) => {
            list[index] = instrumentNode(child, scope, context);
        });

        return node;
    },
	ForInStatement: (node, scope, context) => {
		node.right = instrumentNode(node.right, scope, context);

        // if body is not a block, force it to be a block
        // so that we can inject code into the body
        if (node.body.type !== 'BlockStatement') {
            var block = esprima.parse('{}').body[0];
            block.body.push(node.body);
            node.body = block;
        }

        node.body.body.forEach((child, index, list) => {
            list[index] = instrumentNode(child, scope, context);
        });

        return node;
    },
	WhileStatement: (node, scope, context) => {
		const replace = esprima.parse(`${TRACKER_NAME}.bool(null)`).body[0].expression;
		replace.arguments[0] = instrumentNode(node.test, scope, context);
		node.test = replace;

        // if body is not a block, force it to be a block
        // so that we can inject code into the body
        if (node.body.type !== 'BlockStatement') {
            var block = esprima.parse('{}').body[0];
            block.body.push(node.body);
            node.body = block;
        }

        node.body.body.forEach((child, index, list) => {
            list[index] = instrumentNode(child, scope, context);
        });

        return node;
    },
    DoWhileStatement: (node, scope, context) => instrumentNode.Handlers.WhileStatement(node, scope, context),
	SpreadElement: (node, scope, context) => node,
	ThrowStatement: (node, scope, context) => node,
	AwaitExpression: (node, scope, context) => {
		node.argument = instrumentNode(node.argument, scope, context);
        return node;
	},
	TryStatement: (node, scope, context) => {
        node.block = instrumentNode(node.block, scope, context);
        if (node.handler){
            node.handler = instrumentNode(node.handler, scope, context);
        }
        if (node.finalizer){
            node.finalizer = instrumentNode(node.finalizer, scope, context);
        }
        return node;
    },
	CatchClause: (node, scope, context) => {
		node.body = instrumentNode(node.body, scope, context);
		return node;
	},
	EmptyStatement: (node, scope, context) => node,
    BreakStatement: (node, scope, context) => node,
    ContinueStatement: (node, scope, context) => node
}


// helper function to stringify functions into the output code
function stringify(obj){
	if (typeof obj === 'function'){
		return obj.toString();
	}
	else if (obj instanceof Array){
		return `[ ${obj.map(stringify).join(', ')} ]`;
	}
	else if (obj === null){
		return 'null';
	}
	else if (typeof obj === 'object'){
		return `{ ${Object.keys(obj).map(key => `"${key}": ${stringify(obj[key])}`).join(', ')} }`;
	}
	else return JSON.stringify(obj);
}

function instrument(sourcePath, policy){
	// process policy to validate it
	const tracker = new PrivacyTracker();
	tracker.configure(policy);

	// get dataflow analysis results
	const analysis = CodeAnalyzer.analyze(sourcePath, {
		strict: false,
		deep: false,
		packageInfo: { name: 'node-red' },	// this option is needed to explicitly indicate the code as node-red code
		tree: { isNodeRed: true }			// this option is needed to explicitly indicate the code as node-red code
	}, [ expressPlugin, nodeRedPlugin ]);

	// taint the flows before instrumenting, so that we know which dataflows to instrument
	const tracer = CodeAnalyzer.FlowTracer(analysis.graph);
	
	analysis.graph.sinks.forEach(sink => tracer.traceUpstream(sink, tainter));
	analysis.graph.sources.forEach(source => tracer.traceDownstream(source, tainter));

	analysis.graph.nodes
	.filter(node => node instanceof CodeAnalyzer.EntityTypes.ExternalEntity)
	.forEach(node => {
		tracer.traceUpstream(node, tainter);
		tracer.traceDownstream(node, tainter);
	});

	// attach comments to the tree so that we can identify the user-annotated injection points
	const tree = escodegen.attachComments(analysis.tree, analysis.tree.comments, analysis.tree.tokens);

	tree.scope.tracer = tracer;	// attach tracer so that it can be used during instrumentation pass

	instrumentNode(tree);

	const instrumented = escodegen.generate(tree, { comment: true });

    return instrumented;

// 	const trackerHeader = `const ${TRACKER_NAME} = new (require('turnstile').PrivacyTracker)(__filename, module);
// ${TRACKER_NAME}.configure({
// 	labellers: ${stringify(policy.labellers)},
// 	issuers: ${stringify(policy.issuers)},
// 	rules: ${JSON.stringify(policy.rules)}
// })`;

//     const result = `/* Instrumented by PrivateEdge to protect privacy */
// ${trackerHeader}

// ${instrumented}`;

//     return result;
}

module.exports = {
	instrument,
    stringify
}
