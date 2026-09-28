const path = require('path');
const fs = require('fs');
const crypto = require('crypto');
const stream = require('stream');
const readline = require('readline');
const esprima = require('./esprima.js');    // we modified esprima because the public version (4.0.1) does not support some of the newer ECMAScript syntax
const escodegen = require('escodegen');

const DEBUG = false;

// if STRICT_MODE=true, analysis will stop when it encounters
// variables that cannot be found in the scope (e.g., undeclared names).
// if STRICT_MODE=false, analyzer will create an UnknownObject entity
// for the variable that cannot be found in the scope, and continue running
// the analysis.
let STRICT_MODE = true;

// if DEEP_MODE=true, third-party modules will be fully analyzed.
// if DEEP_MODE=false, only the exported objects of the third-party modules
// will be observed on-demand
let DEEP_MODE = false;

class LexicalScope {
    constructor(func_name, parent = null) {
        this.func_name = func_name;
        this.parent = parent;

        this.entity_states = new Map();
        this.dependency_graph = new Map();

        this.params = [];   // track param names, since instrumentor needs them for leading injections
        this.hoisted = [];  // track hoisted functions names, since instrumentor needs them for leading injections

        // this.hoisted = [];
        this.resolve_on_exit = [];
    }

    id() {
        return this.parent ? `${this.parent.id()}_${this.func_name}` : `$global`;
    }

    get $root(){
        if (this.parent === null) return this;
        return this.parent.$root;
    }

    __initParam(astNode, item){
        let par = item;
        if (item.type === 'AssignmentPattern'){
            par = item.left;
        }
        else if (item.type === 'RestElement'){
            par = item.argument;
        }

        const entity = new SemanticEntity({
            type: 'UnknownObject',
            name: escodegen.generate(par),
            loc: par.loc,
            scope: this
        }, this);
        this.entity_states.set(par.name, entity);
        // this.dependency_graph.set(entity, new Set([ this.invocationEntity ]));
        this.dependency_graph.set(entity, new Set());

        this.params.push(par.name);

        item.entity = entity;
    }

    initEntities(astNode) {
        // initialize entities that are present before we enter the body of the scope.
        // namely, any function parameters or hoisted function names must be initialized

        // first, we create a special SemanticEntity, which we use for representing
        // the dynamic invocation of the function. This special entity is used later
        // to link an invocation to the body of the function, so that we can continue
        // to trace the dataflow across function calls.
        this.invocationEntity = new SemanticEntity({
            type: "FunctionEnter", // this is a special semantic entity
            name: this.func_name,
            loc: astNode.loc
        }, this);
        this.dependency_graph.set(this.invocationEntity, new Set());

        let body;
        if (astNode.type == 'Program') {
            body = astNode.body;
        }
        else if (astNode.type == 'FunctionDeclaration' || astNode.type == 'FunctionExpression') {
            body = astNode.body.body;

            astNode.params.forEach((item) => this.__initParam(astNode, item));
        }
        else if (astNode.type == 'ArrowFunctionExpression') {
            body = astNode.body.type == 'BlockStatement' ? astNode.body.body : [ astNode.body ];

            astNode.params.forEach((item) => this.__initParam(astNode, item));
        }
        else return;

        // find hoisted functions first
        body.filter(node => node.type === 'FunctionDeclaration')
        .forEach(node => {
            const entity = new SemanticEntity(node, this);
            this.entity_states.set(node.id.name, entity);
            this.dependency_graph.set(entity, new Set());

            this.hoisted.push(node.id.name);
        });

        // find variable declarations
        body.filter(node => node.type === 'VariableDeclaration')
        .forEach(node => {
            node.declarations.forEach(item => {
                if (item.init !== null || !this.entity_states.has(item.id.name)){
                    const entity = new SemanticEntity(item, this);
                    this.entity_states.set(item.id.name, entity);
                    this.dependency_graph.set(entity, new Set());
                }
            });
        });

        // create an unknown entity for 'this'
        const thisEntity = new SemanticEntity({
            type: 'UnknownObject',
            name: this.func_name + '.this',
            loc: astNode.loc,
            scope: this
        }, this);
        this.entity_states.set('this', thisEntity);
        this.dependency_graph.set(thisEntity, new Set());

        // TODO: also handle variable declarations inside If/For/While statements
    }

    getEntity(name){
        if (this.entity_states.has(name)) return this.entity_states.get(name);
        else if (this.parent) return this.parent.getEntity(name);

        // special case when we reach the root scope
        if (!this.parent && this.globalEntity){
            return this.globalEntity.entity_states.get(name);
        }
    }

    createExternalDefinedEntity(entityType, name, loc){
        const definition = findDefinition(entityType, name, loc);

        if (!definition){
            throw new AnalyzerException(`Could not find a definition for the external object ${name}`);
        }

        let entity;
        if (definition.type === 'BuiltIn'){
            entity = definition.init(name, loc, this);
        }
        else {
            entity = ExternalEntity.createFromDefinition(entityType + '.' + crypto.randomBytes(4).toString('hex'), definition, this, loc);
        }

        this.entity_states.set(name, entity);
        this.dependency_graph.set(entity, new Set());

        return entity;
    }

    isAncestorOf(descendant){
        let parent = descendant.parent;
        while (parent !== null){
            if (parent === this) return true;
            parent = parent.parent;
        }
        return false;
    }
}

class SemanticEntity {
    constructor(node, scope){
        this.id = 'se' + crypto.randomBytes(9).toString('hex');
        this.node = node;
        this.scope = scope;
        
        // back-reference
        node.entity = this;

        // object entities have child entities whose state needs to be tracked
        this.entity_states = new Map();
    }

    toString(){
        return `${this.node.type} at line ${this.node.loc.start.line}, col ${this.node.loc.start.column} of ${this.scope.$root.absSourcePath}`;
    }

    toJSON(){
        let type = this.node.type;
        if (this.node.type === 'VariableDeclarator'){
            type = `Dynamic value of ${this.node.id.name}`;
        }

        let name = this.node.name;
        if (this.node.type === 'FunctionDeclaration' || this.node.type === 'FunctionExpression' || this.node.type === 'ClassDeclaration'){
            name = this.node.id.name;
        }
        else if (this.node.type === 'Property'){
            name = this.node.key.name;
        }
        else if (this.node.type === 'NewExpression' || this.node.type === 'MemberExpression'){
            name = escodegen.generate(this.node);
        }

        return {
            id: this.id,
            type: type,
            name: name,
            loc: this.node.loc,
            file: this.scope.$root.absSourcePath
        };
    }

    getNestedProp(tokens){
        if (!(tokens instanceof Array)){
            tokens = tokens.split('.');
        }
        if (tokens.length < 1) return;
        const prop = this.entity_states.get(tokens[0]);
        if (tokens.length === 1) return prop;
        if (!prop) throw new Error(`Entity ${this.id} does not have the property "${tokens.join('.')}"`);
        return prop.getNestedProp(tokens.slice(1));
    }

    getSourceEntity(){
        if (['Identifier', 'UnknownObject', 'ThisExpression', 'MemberExpression'].includes(this.node.type)){
            let dependencies = this.scope.dependency_graph.get(this);
            if (!dependencies && DEBUG) {
                console.log(`WARN: The following '${this.node.type}' has no dependencies:\n\t${this.node.name} (in line ${this.node.loc.start.line} in ${this.scope.$root.absSourcePath})`);
                // console.log(this.toJSON());
            }
            if (!dependencies || dependencies.size === 0) return this;
            else return dependencies.values().next().value.getSourceEntity();
        }
        return this;
    }

    // FunctionInvocation is a special case where a pair of entities
    // are created to represent a function invocation, one for when
    // the function is called (pushing the stack frame into the call stack)
    // and another for when the function returns (popping the stack frame)
    // This is used for interprocedural dataflow tracking
    static createFunctionInvocation(calleeEntity, scope, callExpr){
        // this represents execution context being pushed into the call stack
        const enter = new SemanticEntity({
            type: calleeEntity.node.type === 'IO:Writer' ? 'SinkFunctionEnter' : 'FunctionEnter',
            name: escodegen.generate(callExpr),
            loc: callExpr.loc,
            scope: scope
        }, scope);

        // save a reference to the AST that is related to this semantic entity, 
        // which can be useful during dataflow tracing (e.g., during instrumentation,
        // we need to know the actual AST associated with the semantic entity)
        enter.derivedFrom = callExpr;

        const exit = new SemanticEntity({
            type: 'FunctionReturn',
            name: escodegen.generate(callExpr),
            loc: callExpr.loc,
            scope: scope
        }, scope);

        // save a reference to the AST that is related to this semantic entity, 
        // which can be useful during dataflow tracing (e.g., during instrumentation,
        // we need to know the actual AST associated with the semantic entity)
        exit.derivedFrom = callExpr;

        return {
            enter: enter,
            exit: exit
        }
    }
}

class ConditionalEntity extends SemanticEntity {
    constructor(node, scope){
        super(node, scope);

        this.condition = null;
        this.outcomes = [];
    }

    toJSON(){
        const json = super.toJSON();
        json.condition = this.condition.toJSON();
        json.outcomes = this.outcomes.map(item => item.toJSON());

        return json;
    }
}

const IO_SOURCE_ENTITY_TYPES = [
    'ProcessArgument',
    'ProcessEnvironmentVariable',
    'IO:ProcessStdin',
    'IO:FileContent',
    'IO:ProcessOutput',
    'IO:SocketContent',
    'IO:HttpIncomingMessage',
    'IO:Source'
];
const IO_SINK_ENTITY_TYPES = [
    'SinkFunctionEnter',
    // 'IO:Writer'
];
// const IO_DUPLEX_ENTITY_TYPES = [
//     'IO:Socket'
// ];

const SPECIAL_BUILTIN_HANDLERS = {
    'global.process.argv': {
        propGetter: function(name, scope){
            return new SemanticEntity({
                type: 'ProcessArgument',
                name: name,
                loc: { start: { line: '_', column: '_' } },
                scope: scope.$root
            }, scope.$root);
        }
    },
    'global.process.env': {
        propGetter: function(name, scope){
            return new SemanticEntity({
                type: 'ProcessEnvironmentVariable',
                name: name,
                loc: { start: { line: '_', column: '_' } },
                scope: scope.$root
            }, scope.$root);
        }
    },
    'global.process.stdout.write': {
        get: function(name, scope){
            const entity = new SemanticEntity({
                type: 'IO:Writer',
                name: 'process.stdout.write',
                loc: { start: { line: '_', column: '_' } },
                scope: scope.$root
            }, scope.$root);
            scope.$root.dependency_graph.set(entity, new Set());
            return entity;
        }
    },
    'fs.writeFileSync': {
        get: function(name, scope){
            const entity = new SemanticEntity({
                type: 'IO:Writer',
                name: 'fs.writeFileSync',
                loc: { start: { line: '_', column: '_' } },
                scope: scope.$root
            }, scope.$root);
            scope.$root.dependency_graph.set(entity, new Set());
            return entity;
        }
    },
    'fs.writeFile': {
        get: function(name, scope){
            const entity = new SemanticEntity({
                type: 'IO:Writer',
                name: 'fs.writeFile',
                loc: { start: { line: '_', column: '_' } },
                scope: scope.$root
            }, scope.$root);
            scope.$root.dependency_graph.set(entity, new Set());
            return entity;
        }
    },
    'fs.appendFileSync': {
        get: function(name, scope){
            const entity = new SemanticEntity({
                type: 'IO:Writer',
                name: 'fs.appendFileSync',
                loc: { start: { line: '_', column: '_' } },
                scope: scope.$root
            }, scope.$root);
            scope.$root.dependency_graph.set(entity, new Set());
            return entity;
        }
    },
    'fs.appendFile': {
        get: function(name, scope){
            const entity = new SemanticEntity({
                type: 'IO:Writer',
                name: 'fs.appendFile',
                loc: { start: { line: '_', column: '_' } },
                scope: scope.$root
            }, scope.$root);
            scope.$root.dependency_graph.set(entity, new Set());
            return entity;
        }
    },
    'child_process.exec': {
        get: function(name, scope){
            const entity = new SemanticEntity({
                type: 'IO:Writer',
                name: 'child_process.exec',
                loc: { start: { line: '_', column: '_' } },
                scope: scope.$root
            }, scope.$root);
            scope.$root.dependency_graph.set(entity, new Set());
            return entity;
        }
    }
}

const SPECIAL_BUILTIN_HOOKS = {
    'FunctionDeclaration in CallExpression': (node, scope, context) => {
        const callee = context.parent.node.callee;
        const invocation = context.parent.invocation;
        // console.log(callee, callee.object.entity);
        // console.log(invocation);

        if (escodegen.generate(callee) === 'process.stdin.on'
            && context.parent.node.arguments.length > 1
            && context.parent.node.arguments[0].type === 'Literal'
            && context.parent.node.arguments[0].value === 'data'){
            // overwrite the function params
            node.params.forEach((item) => {
                const oldEntity = scope.entity_states.get(item.name);
                scope.dependency_graph.delete(oldEntity);

                const entity = new SemanticEntity({
                    type: 'IO:ProcessStdin',
                    name: escodegen.generate(item),
                    loc: item.loc,
                    scope: scope
                }, scope);
                scope.entity_states.set(item.name, entity);
                scope.dependency_graph.set(entity, new Set([]));

                // attach the entity to the node so that it can be looked up later
                // e.g., when generating HTML nodes
                item.entity = entity;
            });
        }
        else if (callee.type === 'MemberExpression'
            && callee.object.entity 
            && callee.object.entity.node.type === 'IO:FileReadStream'
            && callee.property.name === 'on'
            && context.parent.node.arguments[0].type === 'Literal'
            && context.parent.node.arguments[0].value === 'data'){
            
            // overwrite the function params
            node.params.forEach((item) => {
                const oldEntity = scope.entity_states.get(item.name);
                scope.dependency_graph.delete(oldEntity);

                const entity = new SemanticEntity({
                    type: 'IO:FileContent',
                    name: escodegen.generate(item),
                    loc: item.loc,
                    scope: scope
                }, scope);
                scope.entity_states.set(item.name, entity);
                if (invocation && invocation.enter){
                    scope.dependency_graph.set(entity, new Set([ invocation.enter ]));
                }
                else {
                    scope.dependency_graph.set(entity, new Set([]));    
                }

                // attach the entity to the node so that it can be looked up later
                // e.g., when generating HTML nodes
                item.entity = entity;
            });
        }
        else if (callee.type === 'MemberExpression'
            && callee.object.entity
            && callee.object.entity.id === 'fs'
            && callee.property.name === 'readFile'){
            
            // overwrite the function params
            const par = node.params[1];

            const oldEntity = scope.entity_states.get(par.name);
            scope.dependency_graph.delete(oldEntity);

            const entity = new SemanticEntity({
                type: 'IO:FileContent',
                name: escodegen.generate(par),
                loc: par.loc,
                scope: scope
            }, scope);
            scope.entity_states.set(par.name, entity);
            if (invocation && invocation.enter){
                scope.dependency_graph.set(entity, new Set([ invocation.enter ]));
            }
            else {
                scope.dependency_graph.set(entity, new Set([]));    
            }

            // attach the entity to the node so that it can be looked up later
            // e.g., when generating HTML nodes
            par.entity = entity;
        }
        else if (callee.type === 'MemberExpression'
            && callee.object.entity
            && callee.object.entity.id === 'child_process'
            && callee.property.name === 'exec'){
            
            // overwrite the function params
            node.params.slice(1).forEach((item) => {
                const oldEntity = scope.entity_states.get(item.name);
                scope.dependency_graph.delete(oldEntity);

                const entity = new SemanticEntity({
                    type: 'IO:ProcessOutput',
                    name: escodegen.generate(item),
                    loc: item.loc,
                    scope: scope
                }, scope);
                scope.entity_states.set(item.name, entity);
                if (invocation && invocation.enter){
                    scope.dependency_graph.set(entity, new Set([ invocation.enter ]));
                }
                else {
                    scope.dependency_graph.set(entity, new Set([]));    
                }

                // attach the entity to the node so that it can be looked up later
                // e.g., when generating HTML nodes
                item.entity = entity;
            });
        }
        else if (callee.type === 'MemberExpression'
            && callee.object.entity 
            && callee.object.entity.node.type === 'IO:ProcessReadStream'
            && callee.property.name === 'on'
            && context.parent.node.arguments[0].type === 'Literal'
            && context.parent.node.arguments[0].value === 'data'){
            
            // overwrite the function params
            node.params.forEach((item) => {
                const oldEntity = scope.entity_states.get(item.name);
                scope.dependency_graph.delete(oldEntity);

                const entity = new SemanticEntity({
                    type: 'IO:ProcessOutput',
                    name: escodegen.generate(item),
                    loc: item.loc,
                    scope: scope
                }, scope);
                scope.entity_states.set(item.name, entity);
                if (invocation && invocation.enter){
                    scope.dependency_graph.set(entity, new Set([ invocation.enter ]));
                }
                else {
                    scope.dependency_graph.set(entity, new Set([]));    
                }

                // attach the entity to the node so that it can be looked up later
                // e.g., when generating HTML nodes
                item.entity = entity;
            });
        }
        else if (callee.type === 'MemberExpression'
            && callee.object.entity
            && ((callee.object.entity.id === 'net'
                && callee.property.name === 'createServer')
            || (callee.object.entity.node.type === 'IO:Server'
                && callee.property.name === 'on'
                && context.parent.node.arguments[0].type === 'Literal'
                && context.parent.node.arguments[0].value === 'connection'))){
            
            // overwrite the function params
            const par = node.params[0];

            const oldEntity = scope.entity_states.get(par.name);
            scope.dependency_graph.delete(oldEntity);

            const entity = new SemanticEntity({
                type: 'IO:Socket',
                name: escodegen.generate(par),
                loc: par.loc,
                scope: scope
            }, scope);
            scope.entity_states.set(par.name, entity);

            const writer = new SemanticEntity({
                type: 'IO:Writer',
                name: escodegen.generate(par) + '.write',
                loc: { start: { line: '_', column: '_' } },
                scope: scope
            }, scope);

            entity.entity_states.set('write', writer);

            if (invocation && invocation.enter){
                scope.dependency_graph.set(entity, new Set([ invocation.enter ]));
            }
            else {
                scope.dependency_graph.set(entity, new Set([]));    
            }

            // attach the entity to the node so that it can be looked up later
            // e.g., when generating HTML nodes
            par.entity = entity;
        }
        else if (callee.type === 'MemberExpression'
            && callee.object.entity 
            && (callee.object.entity.node.type === 'IO:Socket' || callee.object.entity.node.type === 'IO:HttpIncomingMessage')
            && callee.property.name === 'on'
            && context.parent.node.arguments[0].type === 'Literal'
            && context.parent.node.arguments[0].value === 'data'){
            
            // overwrite the function params
            const par = node.params[0];
            const oldEntity = scope.entity_states.get(par.name);
            scope.dependency_graph.delete(oldEntity);

            const entity = new SemanticEntity({
                type: 'IO:SocketContent',
                name: escodegen.generate(par),
                loc: par.loc,
                scope: scope
            }, scope);
            scope.entity_states.set(par.name, entity);
            if (invocation && invocation.enter){
                scope.dependency_graph.set(entity, new Set([ invocation.enter ]));
            }
            else {
                scope.dependency_graph.set(entity, new Set([]));    
            }

            // attach the entity to the node so that it can be looked up later
            // e.g., when generating HTML nodes
            par.entity = entity;
        }
        else if (callee.type === 'MemberExpression'
            && callee.object.entity
            && (((callee.object.entity.id === 'http' || callee.object.entity.id === 'https')
                && callee.property.name === 'createServer')
            || (callee.object.entity.node.type === 'IO:HttpServer'
                && callee.property.name === 'on'
                && context.parent.node.arguments[0].type === 'Literal'
                && context.parent.node.arguments[0].value === 'request'))){
            
            // overwrite the function params
            const par0 = node.params[0];

            const oldEntity0 = scope.entity_states.get(par0.name);
            scope.dependency_graph.delete(oldEntity0);

            const entity0 = new SemanticEntity({
                type: 'IO:HttpIncomingMessage',
                name: escodegen.generate(par0),
                loc: par0.loc,
                scope: scope
            }, scope);
            scope.entity_states.set(par0.name, entity0);

            scope.dependency_graph.set(entity0, new Set([]));

            const par1 = node.params[1];

            const oldEntity1 = scope.entity_states.get(par1.name);
            scope.dependency_graph.delete(oldEntity1);

            const entity1 = new SemanticEntity({
                type: 'IO:HttpServerResponse',
                name: escodegen.generate(par1),
                loc: par1.loc,
                scope: scope
            }, scope);
            scope.entity_states.set(par1.name, entity1);

            scope.dependency_graph.set(entity1, new Set([]));

            const writers = ['write', 'writeHead', 'setHeader', 'end'];
            writers.forEach(method => {
                const writer = new SemanticEntity({
                    type: 'IO:Writer',
                    name: escodegen.generate(par1) + '.' + method,
                    loc: { start: { line: '_', column: '_' } },
                    scope: scope
                }, scope);

                entity1.entity_states.set(method, writer);
            });

            // const writer = new SemanticEntity({
            //     type: 'IO:Writer',
            //     name: escodegen.generate(par0) + '.write',
            //     loc: { start: { line: '_', column: '_' } },
            //     scope: scope
            // }, scope);

            // entity.entity_states.set('write', writer);

            // if (invocation && invocation.enter){
            //     scope.dependency_graph.set(entity, new Set([ invocation.enter ]));
            // }
            // else {
            //     scope.dependency_graph.set(entity, new Set([]));
            // }

            // attach the entity to the node so that it can be looked up later
            // e.g., when generating HTML nodes
            par0.entity = entity0;
            par1.entity = entity1;
        }
        else if (callee.entity && callee.entity instanceof ExternalEntity && callee.entity.params){
            // console.log(callee);

            const argIndex = context.parent.node.arguments.indexOf(node);
            const calleeParams = callee.entity.params;
            let param;
            if (argIndex < calleeParams.length){
                param = calleeParams[argIndex];
                if (param.indexOf('...') === 0){
                    param = param.substr(3);
                }
            }
            else if (calleeParams[calleeParams.length - 1].indexOf('...') === 0){
                param = calleeParams[calleeParams.length - 1].substr(3);
            }
            else {
                throw new AnalyzerException(`Function at Line ${node.loc.start.line} was passed as an argument to ${escodegen.generate(node)}, but the external function ${escodegen.generate(callee)} does not expect a function as an argument at index ${argIndex}`);
            }

            const namespace = callee.entity.id.split('.')[0];
            const paramDefinition = findDefinition(namespace + '.' + param);

            if (!paramDefinition){
                throw new AnalyzerException(`Could not find a definition for the external function ${param} at line ${node.loc.start.line}`);
            }
            else if (paramDefinition.type !== 'Function' && paramDefinition.inherits !== 'Function'){
                throw new AnalyzerException(`Definition for the external function ${param} was found, but it was defined as type ${paramDefinition.type}, not a function`);
            }

            let funcParams = paramDefinition.params;
            if (funcParams[0] instanceof Array){
                funcParams = funcParams.find(item => item.length === node.params.length);
            }

            if (!funcParams){
                throw new AnalyzerException(`Definition for the external function ${param} was found, but the function parameters were not provided (processing Line ${node.loc.start.line} in ${scope.$root.absSourcePath})\n${JSON.stringify(paramDefinition.params)}`);
            }

            // overwrite the function params
            node.params.forEach((item, index) => {
                const oldEntity = scope.entity_states.get(item.name);
                scope.dependency_graph.delete(oldEntity);

                const defName = funcParams[index];

                if (!defName){
                    throw new AnalyzerException(`Definition for the parameter at index ${index} of the external function ${param} was not found (calleeEntity = ${callee.entity.id}), trying to process ${escodegen.generate(node)} in Line ${node.loc.start.line}`);
                }

                const definition = findDefinition(namespace + '.' + funcParams[index]);

                let entity;
                if (definition.type === 'BuiltIn'){
                    entity = definition.init(item.name, item.loc, scope);
                }
                else {
                    entity = ExternalEntity.createFromDefinition(callee.entity.id + '.' + crypto.randomBytes(4).toString('hex') + '.' + item.name, definition, scope, item.loc);
                }

                // save a reference to the AST that is related to this semantic entity, 
                // which can be useful during dataflow tracing (e.g., during instrumentation,
                // we need to know the actual AST associated with the semantic entity)
                entity.derivedFrom = item;
                // console.log(entity, item);

                scope.entity_states.set(item.name, entity);
                
                // if (invocation && invocation.enter){
                //     scope.dependency_graph.set(entity, new Set([ invocation.enter ]));
                // }
                // else {
                //     scope.dependency_graph.set(entity, new Set([]));    
                // }
                scope.dependency_graph.set(entity, new Set());

                // attach the entity to the node so that it can be looked up later
                // e.g., when generating HTML nodes
                item.entity = entity;
            });
        }
    },
    'CallExpression': (node, scope, calleeEntity) => {
        if (escodegen.generate(node.callee) === 'process.stdout.write'){
            // attach the entity to the node so that it can be looked up later
            // e.g., when generating HTML nodes
            node.callee.entity = scope.$root.globalEntity.getNestedProp('process.stdout.write');
        }
        else if (calleeEntity.node.type === 'IO:Writer'){
            node.callee.entity = calleeEntity;
        }
    },
    'KnownReturnTypes in CallExpression': (node, scope, calleeEntity, invocation) => {
        // console.log('Known Return Type');
        // console.log(calleeEntity);
        if (calleeEntity.node.type === 'BuiltIn:Function'){
            if (calleeEntity.id === 'fs.createReadStream'){
                const exit = new SemanticEntity({
                    type: 'IO:FileReadStream',
                    name: escodegen.generate(node),
                    loc: node.loc,
                    scope: scope
                }, scope);

                invocation.exit = exit;
            }
            else if (calleeEntity.id === 'fs.createWriteStream'){
                const exit = new SemanticEntity({
                    type: 'IO:FileWriteStream',
                    name: escodegen.generate(node),
                    loc: node.loc,
                    scope: scope
                }, scope);

                const writer = new SemanticEntity({
                    type: 'IO:Writer',
                    name: escodegen.generate(node) + '.write',
                    loc: { start: { line: '_', column: '_' } },
                    scope: scope
                }, scope);

                exit.entity_states.set('write', writer);

                invocation.exit = exit;
            }
            else if (calleeEntity.id === 'fs.readFileSync'){
                const exit = new SemanticEntity({
                    type: 'IO:FileContent',
                    name: escodegen.generate(node),
                    loc: node.loc,
                    scope: scope
                }, scope);

                invocation.exit = exit;
            }
            else if (calleeEntity.id === 'child_process.spawn' || calleeEntity.id === 'child_process.fork'){
                const exit = new SemanticEntity({
                    type: 'ChildProcess',
                    name: escodegen.generate(node),
                    loc: node.loc,
                    scope: scope
                }, scope);

                const stdin = new SemanticEntity({
                    type: 'IO:ProcessWriteStream',
                    name: escodegen.generate(node) + '.stdin',
                    loc: { start: { line: '_', column: '_' } },
                    scope: scope
                }, scope);

                const stdinWrite = new SemanticEntity({
                    type: 'IO:Writer',
                    name: escodegen.generate(node) + '.stdin.write',
                    loc: { start: { line: '_', column: '_' } },
                    scope: scope
                }, scope);

                stdin.entity_states.set('write', stdinWrite);

                const stdout = new SemanticEntity({
                    type: 'IO:ProcessReadStream',
                    name: escodegen.generate(node) + '.stdout',
                    loc: { start: { line: '_', column: '_' } },
                    scope: scope
                }, scope);

                const stderr = new SemanticEntity({
                    type: 'IO:ProcessReadStream',
                    name: escodegen.generate(node) + '.stderr',
                    loc: { start: { line: '_', column: '_' } },
                    scope: scope
                }, scope);

                exit.entity_states.set('stdin', stdin);
                exit.entity_states.set('stdout', stdout);
                exit.entity_states.set('stderr', stderr);

                invocation.exit = exit;
            }
            else if (calleeEntity.id === 'net.createServer'){
                const exit = new SemanticEntity({
                    type: 'IO:Server',
                    name: escodegen.generate(node),
                    loc: node.loc,
                    scope: scope
                }, scope);

                invocation.exit = exit;
            }
            else if (calleeEntity.id === 'net.createConnection' || calleeEntity.id === 'net.connect'){
                const exit = new SemanticEntity({
                    type: 'IO:Socket',
                    name: escodegen.generate(node),
                    loc: node.loc,
                    scope: scope
                }, scope);

                const writer = new SemanticEntity({
                    type: 'IO:Writer',
                    name: escodegen.generate(node) + '.write',
                    loc: { start: { line: '_', column: '_' } },
                    scope: scope
                }, scope);

                exit.entity_states.set('write', writer);

                invocation.exit = exit;
            }
            else if (calleeEntity.id === 'http.createServer' || calleeEntity.id === 'https.createServer'){
                const exit = new SemanticEntity({
                    type: 'IO:HttpServer',
                    name: escodegen.generate(node),
                    loc: node.loc,
                    scope: scope
                }, scope);

                invocation.exit = exit;
            }
        }
    },
    'KnownReturnTypes in NewExpression': (node, scope, calleeEntity, invocation) => {
        if (calleeEntity.node.type === 'BuiltIn:Function'){
            if (calleeEntity.id === 'net.Socket'){
                const exit = new SemanticEntity({
                    type: 'IO:Socket',
                    name: escodegen.generate(node),
                    loc: node.loc,
                    scope: scope
                }, scope);

                const writer = new SemanticEntity({
                    type: 'IO:Writer',
                    name: escodegen.generate(node) + '.write',
                    loc: { start: { line: '_', column: '_' } },
                    scope: scope
                }, scope);

                exit.entity_states.set('write', writer);

                invocation.exit = exit;
            }
            else if (calleeEntity.id === 'net.Server'){
                const exit = new SemanticEntity({
                    type: 'IO:Server',
                    name: escodegen.generate(node),
                    loc: node.loc,
                    scope: scope
                }, scope);

                invocation.exit = exit;
            }
            else if (calleeEntity.id === 'http.Server' || calleeEntity.id === 'https.Server'){
                const exit = new SemanticEntity({
                    type: 'IO:HttpServer',
                    name: escodegen.generate(node),
                    loc: node.loc,
                    scope: scope
                }, scope);

                invocation.exit = exit;
            }
        }
    }
}

class BuiltInEntity extends SemanticEntity {
    constructor(name, obj, scope){
        super({
            type: Object.getPrototypeOf(obj) === null ? 'BuiltIn:NullPrototype' : (obj === Object.prototype ? 'BuiltIn:ObjectPrototype' : 'BuiltIn:' + Object.getPrototypeOf(obj).constructor.name),
            name: name,
            loc: { start: { line: '_', column: '_' } },
            scope: scope
        }, scope);
        
        this.id = name; // override random ID
        let thisId = name;

        // make a lazy version of entity_states as we don't
        // want to enumerate all builtins from the beginning
        function lazyMapGetter(name){
            if (this.has(name)) return Map.prototype.get.call(this, name);
            const propName = thisId + '.' + name;
            // console.log(propName);

            if (SPECIAL_BUILTIN_HANDLERS[thisId] && SPECIAL_BUILTIN_HANDLERS[thisId].propGetter){
                const childEntity = SPECIAL_BUILTIN_HANDLERS[thisId].propGetter.call(this, name, scope);
                this.set(name, childEntity);
                return childEntity;
            }
            else if (SPECIAL_BUILTIN_HANDLERS[propName] && SPECIAL_BUILTIN_HANDLERS[propName].get){
                const childEntity = SPECIAL_BUILTIN_HANDLERS[propName].get.call(this, name, scope);
                this.set(name, childEntity);
                return childEntity;
            }

            if (Object.getOwnPropertyNames(obj).includes(name)){
                const childEntity = new BuiltInEntity(thisId + '.' + name, obj[name], scope);
                this.set(name, childEntity);
                return childEntity;
            }
        }
        this.entity_states.get = lazyMapGetter;
    }
}

class ExternalEntity extends SemanticEntity {
    constructor(name, obj, scope){
        super({
            type: 'External:' + Object.getPrototypeOf(obj).constructor.name,
            name: name,
            loc: { start: { line: '_', column: '_' } },
            scope: scope
        }, scope);

        scope.dependency_graph.set(this, new Set());
        
        this.id = name; // override random ID
        let thisId = name;

        // make a lazy version of entity_states as we don't
        // want to enumerate all builtins from the beginning
        function lazyMapGetter(name){
            if (this.has(name)) return Map.prototype.get.call(this, name);

            if (Object.getOwnPropertyNames(obj).includes(name)){
                const childEntity = new ExternalEntity(thisId + '.' + name, obj[name], scope);
                this.set(name, childEntity);
                return childEntity;
            }
        }
        this.entity_states.get = lazyMapGetter;
    }

    static createFromDefinition(name, definition, scope, loc){
        const entity = new ExternalEntity(name, {}, scope);
        entity.node.type = 'External:' + definition.type;
        if (loc) entity.node.loc = loc; // provide loc, if known, for viewing in the html
        
        entity.params = definition.params;
        entity.returns = definition.returns;
        entity.inherits = definition.inherits;

        if (definition.definitions){
            definition.definitions.forEach(item => {
                EXTERNAL_DEFINITIONS[entity.id + '.' + item.type] = item;
            });
        }

        const namespace = entity.id.split('.')[0];

        if (definition.properties){
            Object.keys(definition.properties)
            .forEach(key => {
                let childDefinition = definition.properties[key];
                if (typeof childDefinition === 'string'){
                    // lookup defintiions
                    childDefinition = findDefinition(namespace + '.' + childDefinition);
                }

                let childEntity;
                if (childDefinition.type === 'BuiltIn'){
                    childEntity = childDefinition.init(entity.id + '.' + key, { start: { line: '_', column: '_'}}, scope);
                }
                else {
                    childEntity = ExternalEntity.createFromDefinition(entity.id + '.' + key, childDefinition, scope);
                }

                entity.entity_states.set(key, childEntity);
            });
        }

        return entity;
    }
}

class LiteralEntity extends SemanticEntity {
    constructor (value, scope, loc){
        super({
            type: 'Literal:' + (value === null ? 'Null' : (value === undefined ? 'Undefined' : Object.getPrototypeOf(value).constructor.name)),
            name: value,
            loc: loc,
            scope: scope
        }, scope);

        if (value !== null & value !== undefined){
            this.proto = Object.getPrototypeOf(value);
        }
        else {
            this.proto = {};
        }

        const proto = this.proto;
        const thisId = this.id;

        // make a lazy version of entity_states as we don't
        // want to enumerate all builtins from the beginning
        function lazyMapGetter(name){
            if (this.has(name)) return Map.prototype.get.call(this, name);

            if (Object.getOwnPropertyNames(proto).includes(name)){
                const childValue = value[name];
                let childEntity;
                if (childValue === null || childValue === undefined || (typeof childValue !== 'object' && typeof childValue !== 'function')){
                    childEntity = new LiteralEntity(childValue, scope, { start: { line: '_', column: '_' } });
                }
                else {
                    childEntity = new BuiltInEntity(thisId + '.' + name, childValue, scope);
                }

                this.set(name, childEntity);
                return childEntity;
            }
        }
        this.entity_states.get = lazyMapGetter;
    }
}

class ParsingContext {
    constructor (node, parent = null){
        this.parent = parent;
        this.node = node;

        this.entities = new Set();
    }
}

const BUILTIN_LIBS = [
    'fs',
    'net',
    'child_process',
    'http',
    'https',
    'http2',
    'buffer',
    'events',
    'stream',
    'path',
    'url',
    'util',
    'os',
    'crypto',
    'tty',
    'zlib',
    'repl',
    'readline',
    'cluster',
    'vm',
    'string_decoder',
    'async_hooks',
    'querystring'
];

const EXTERNAL_LIBS = {};
const EXTERNAL_DEFINITIONS = {};
const EXTERNAL_HOOKS = {};
const BUILTIN_DEFINITIONS = {
    'Function': {
        type: 'BuiltIn',
        init: (name, loc, scope) => new SemanticEntity({
            type: 'External:Function',
            name: name,
            loc: loc,
            scope: scope
        }, scope)
    },
    'Object': {
        type: 'BuiltIn',
        init: (name, loc, scope) => new SemanticEntity({
            type: 'External:Object',
            name: name,
            loc: loc,
            scope: scope
        }, scope)
    },
    'Error': {
        type: 'BuiltIn',
        init: (name, loc, scope) => new SemanticEntity({
            type: 'External:Error',
            name: name,
            loc: loc,
            scope: scope
        }, scope)
    },
    'IO:Source': {
        type: 'BuiltIn',
        init: (name, loc, scope) => new SemanticEntity({
            type: 'IO:Source',
            name: name,
            loc: loc,
            scope: scope
        }, scope)
    },
    'IO:Writer': {
        type: 'BuiltIn',
        init: (name, loc, scope) => new SemanticEntity({
            type: 'IO:Writer',
            name: name,
            loc: loc,
            scope: scope
        }, scope)
    }
}

const findDefinition = name => {
    const tokens = name.split('.');
    const namespace = tokens[0];
    const defName = tokens.slice(1).join('.');

    if (defName in BUILTIN_DEFINITIONS){
        return BUILTIN_DEFINITIONS[defName];
    }
    else return EXTERNAL_DEFINITIONS[name];
}

const requiredBuiltIns = {};
const requiredExternals = {};
const requiredModules = {};

const generateNodeModulePaths = absPath => {
    if (absPath === path.resolve('/')) return [];
    const dirname = path.dirname(absPath);
    return [ path.join(dirname, 'node_modules') ].concat(generateNodeModulePaths(dirname));
}

class AnalyzerException extends Error {
    constructor(message){
        super(message);
    }
}

function analyzeNode(node, scope, context){
    try {
        if (node === null || node.skip) return node;

        // console.log(`(${node.type}) at Line ${node.loc.start.line}: ${escodegen.generate(node)}`);

        if (analyzeNode.Handlers[node.type]) {
            return analyzeNode.Handlers[node.type](node, scope, context);

            // try {
            //     return analyzeNode.Handlers[node.type](node, scope, context);
            // }
            // catch (err){
            //     if (err instanceof AnalyzerException){
            //         const wrapped = new Error(`Exception while handling ${node.type} in Line ${node.loc.start.line}, Column ${node.loc.start.column} of ${scope.$root.absSourcePath}\nException: ${err.message}\nCode: ${escodegen.generate(node)}`, { cause: err });
            //         throw wrapped;
            //     }
            //     else {
            //         throw err;
            //     }
            // }
        }
        else {
            console.error(`WARN: Unsupported expression "${node.type}" in line ${node.loc.start.line}:${node.loc.start.column}`);
        }
        return node;
    }
    catch (err){
        if (err.isInnerMostException || err instanceof AnalyzerException){
            throw err;
        }
        else {
            const message = node ? `Unexpected error while handling ${node.type} in Line ${node.loc.start.line}, Column ${node.loc.start.column}${(scope ? ' in ' + scope.$root.absSourcePath : '')}\n\nCode: ${escodegen.generate(node)}\n\nError Stack: ${err.message}` : '';
            const wrapped = new Error(message, { cause: err });
            wrapped.isInnerMostException = true;
            throw wrapped;
        }
    }
}

analyzeNode.Handlers = {
    Program: (node) => {
        const context = new ParsingContext(node, null);

        const scope = new LexicalScope(null, null);
        scope.absSourcePath = node.absSourcePath;   // TODO: Revise this 
        scope.sourceDir = node.sourceDir;           // TODO: Revise this 
        scope.packageInfo = node.packageInfo;       // TODO: Revise this
        scope.options = node.options;
        scope.loadedModules = [];

        if (EXTERNAL_HOOKS['scope.create'] && scope.packageInfo && EXTERNAL_HOOKS['scope.create'][scope.packageInfo.name]){
            const hook = EXTERNAL_HOOKS['scope.create'][scope.packageInfo.name];
            hook(node, scope, context);
        }

        scope.globalEntity = new BuiltInEntity('global', global, scope);
        scope.entity_states.set('require', new BuiltInEntity('require', require, scope));   // require is special and needs to be explicitly injected. See: (https://stackoverflow.com/questions/34566343/why-does-global-require-return-undefined-when-executed-as-nodejs-script)
        scope.entity_states.set('module', new BuiltInEntity('module', module, scope));      // module is also a special object
        scope.entity_states.set('__filename', new BuiltInEntity('__filename', scope.absSourcePath, scope));
        scope.entity_states.set('__dirname', new BuiltInEntity('__dirname', path.dirname(scope.absSourcePath), scope));

        scope.initEntities(node);

        // scope.hoisted.forEach((child, index, list) => {
        //     list[index] = analyzeNode(child, scope, context);
        //     child.skip = true;
        // });

        node.body.forEach((child, index, list) => {
            list[index] = analyzeNode(child, scope, context);
        });

        scope.resolve_on_exit.forEach(callback => callback());

        node.scope = scope;

        return node;
    },
    Literal: (node, scope, context) => {
        const entity = new LiteralEntity(node.value, scope, node.loc);

        scope.dependency_graph.set(entity, new Set());

        context.entities.add(entity);

        return node;
    },
    TemplateLiteral: (node, scope, context) => {
        const entity = new SemanticEntity(node, scope);

        context.entities.add(entity);

        const objContext = new ParsingContext(node, context);
        node.expressions.forEach((child, index, list) => {
            list[index] = analyzeNode(child, scope, objContext);
        });

        scope.dependency_graph.set(entity, objContext.entities);

        return node;
    },
    Identifier: (node, scope, context) => {
        // undefined is a special identifier and must be handled separately
        if (node.name === 'undefined'){
            const entity = new LiteralEntity(undefined, scope, node.loc);
            scope.dependency_graph.set(entity, new Set());

            context.entities.add(entity);
            return node;
        }

        // look up entity
        let entity = scope.getEntity(node.name);

        if (!entity && !STRICT_MODE){
            entity = new SemanticEntity({
                type: 'UnknownObject',
                name: escodegen.generate(node),
                loc: node.loc,
                scope: scope
            }, scope);
            scope.entity_states.set(node.name, entity);
            scope.dependency_graph.set(entity, new Set());
        }

        // if (context.node.type === 'VariableDeclarator' || context.node.type === 'AssignmentExpression'){
            if (!entity){
                throw new AnalyzerException(`Identifier "${node.name}" does not map to any SemanticEntity (while handling ${escodegen.generate(context.node)})`);
            }

            context.entities.add(entity);
        // }

        node.entity = entity;

        return node;
    },
    ExpressionStatement: (node, scope, context) => {
        analyzeNode(node.expression, scope, context);

        return node;
    },
    SequenceExpression: (node, scope, context) => {
        node.expressions.forEach((child, index, list) => analyzeNode(child, scope, context));
        return node;
    },
    VariableDeclaration: (node, scope, context) => {
        // console.log(node.kind);
        node.declarations.forEach((child, index, list) => {
            list[index] = analyzeNode(child, scope, context);
        });

        return node;
    },
    VariableDeclarator: (node, scope, context) => {
        // console.log(`(${node.type}) at Line ${node.loc.start.line}: ${escodegen.generate(node)}`);
        const declContext = new ParsingContext(node, context);
        if (node.init){
            node.init = analyzeNode(node.init, scope, declContext);
        }
        else {
            declContext.entities.add(new SemanticEntity({
                type: 'UnknownObject',
                name: escodegen.generate(node.id),
                loc: node.id.loc,
                scope: scope
            }, scope))
        }

        // create new semantic entity
        if (node.id.type === 'Identifier'){
            const entity = new SemanticEntity(node.id, scope);
            scope.entity_states.set(node.id.name, entity);

            // if node.init contains any entities, declContext.entities will have them
            scope.dependency_graph.set(entity, declContext.entities);

            context.entities.add(entity);
        }
        else if (node.id.type === 'ArrayPattern'){
            // console.log(node.loc.start.line + ': ' + escodegen.generate(node));
            node.id.elements.forEach(elem => {
                if (elem){
                    const entity = new SemanticEntity(elem, scope);
                    scope.entity_states.set(elem.name, entity);    

                    // if node.init contains any entities, declContext.entities will have them
                    scope.dependency_graph.set(entity, declContext.entities);

                    context.entities.add(entity);
                }
            });
        }
        else if (node.id.type === 'ObjectPattern'){
            node.id.properties.forEach(elem => {
                const entity = new SemanticEntity(elem, scope);
                scope.entity_states.set(elem.key.name, entity);

                // if node.init contains any entities, declContext.entities will have them
                const initEntity = declContext.entities.values().next().value;

                // try to read the property from initEntity
                let propEntity = initEntity.entity_states.get(elem.key.name);
                if (propEntity){
                    scope.dependency_graph.set(entity, new Set([ propEntity ]));
                }
                else {
                    scope.dependency_graph.set(entity, declContext.entities);
                }

                context.entities.add(entity);
            });
        }
        else {
            throw new AnalyzerException(`Unexpected VariableDeclarator ID type ${node.id.type} (in "${escodegen.generate(node)}")`);
        }

        return node;
    },
    UpdateExpression: (node, scope, context) => {
        analyzeNode(node.argument, scope, context);
        return node;
    },
    UnaryExpression: (node, scope, context) => {
        analyzeNode(node.argument, scope, context);
        return node;
    },
    BinaryExpression: (node, scope, context) => {

        const entity = new SemanticEntity(node, scope);
        const binaryContext = new ParsingContext(node, context);
        
        analyzeNode(node.left, scope, binaryContext);
        analyzeNode(node.right, scope, binaryContext);

        scope.dependency_graph.set(entity, binaryContext.entities);
        context.entities.add(entity)

        node.entity = entity;

        return node;
    },
    LogicalExpression: (node, scope, context) => {
        
        const entity = new SemanticEntity(node, scope);
        const binaryContext = new ParsingContext(node, context);
        
        analyzeNode(node.left, scope, binaryContext);
        analyzeNode(node.right, scope, binaryContext);

        scope.dependency_graph.set(entity, binaryContext.entities);
        context.entities.add(entity)

        node.entity = entity;

        return node;
    },
    ConditionalExpression: (node, scope, context) => {
        // dependency_graph only captures dataflow dependency
        // control flow dependency is captured by the ConditionalEntity construct
        const entity = new ConditionalEntity(node, scope);

        const condContext = new ParsingContext(node, context);
        const condition = new SemanticEntity(node.test, scope);
        analyzeNode(node.test, scope, condContext);
        scope.dependency_graph.set(condition, condContext.entities);

        const cnsqContext = new ParsingContext(node, context);
        const consequent = new SemanticEntity(node.consequent, scope);
        analyzeNode(node.consequent, scope, cnsqContext);
        scope.dependency_graph.set(consequent, cnsqContext.entities);

        const altContext = new ParsingContext(node, context);
        const alternate = new SemanticEntity(node.alternate, scope);
        analyzeNode(node.alternate, scope, altContext);
        scope.dependency_graph.set(alternate, altContext.entities);

        entity.condition = condition;
        entity.outcomes.push(consequent);
        entity.outcomes.push(alternate);

        context.entities.add(entity);
        return node;
    },
    AssignmentExpression: (node, scope, context) => {
        const exprContext = new ParsingContext(node, context);
        analyzeNode(node.right, scope, exprContext);

        // create new semantic entity
        const entity = new SemanticEntity(node.left, scope);
        if (node.left.type === 'Identifier'){
            scope.entity_states.set(node.left.name, entity);    
        }
        else if (node.left.type === 'MemberExpression'){
            const leftContext = new ParsingContext(node.left, context);
            node.left.object = analyzeNode(node.left.object, scope, leftContext);

            let objectEntity = leftContext.entities.values().next().value;
            // console.log(escodegen.generate(node));
            // console.log(scope, context);

            // look up the dependency chain
            while (objectEntity.node.type === 'Identifier' || objectEntity.node.type === 'MemberExpression'){
                objectEntity = objectEntity.scope.dependency_graph.get(objectEntity).values().next().value;
                // console.log(node.object.name, objectEntity);
            }

            if (objectEntity.node.type === 'ThisExpression'){
                // try to resolve "this", in case the context is clear (e.g., "this" inside a method)

                const thisEntity = scope.getEntity('this');
                if (thisEntity && thisEntity.node.type === 'ClassDeclaration' && scope.func_name === 'constructor'){
                    objectEntity = thisEntity;
                }
            }

            // update the object entity
            objectEntity.entity_states.set(node.left.property.name, entity);
        }

        // if node.right contains any entities, exprContext.entities will have them
        scope.dependency_graph.set(entity, exprContext.entities);
        context.entities.add(entity);

        return node;
    },
    FunctionDeclaration: (node, scope, context) => {
        const funcContext = new ParsingContext(node, context);

        const funcScope = new LexicalScope(node.id.name, scope);
        funcScope.initEntities(node);

        // Execute special hooks for handling callback functions
        // passed to built-in functions (e.g., process.stdin.on(data => {  }));
        if (context.node.type === 'CallExpression'){
            SPECIAL_BUILTIN_HOOKS['FunctionDeclaration in CallExpression'](node, funcScope, funcContext);
        }

        if (EXTERNAL_HOOKS['scope.create'] && scope.$root.packageInfo && EXTERNAL_HOOKS['scope.create'][scope.$root.packageInfo.name]){
            const hook = EXTERNAL_HOOKS['scope.create'][scope.$root.packageInfo.name];
            hook(node, funcScope, context);
        }

        if (node.onScopeInitialized){
            node.onScopeInitialized.forEach(callback => callback(funcScope));
        }

        // funcScope.hoisted.forEach((child, index, list) => {
        //     list[index] = analyzeNode(child, funcScope, funcContext);
        //     child.skip = true;
        // });

        if (node.body.type === 'BlockStatement'){
            node.body.body.forEach((child, index, list) => {
                list[index] = analyzeNode(child, funcScope, funcContext);
            });
        }
        else {
            node.body = analyzeNode(node.body, funcScope, funcContext);
        }

        // The function could have no ReturnStatement.
        // However, we need to explicitly provide a return statement
        // so that function calls can be traced.
        if (!funcScope.entity_states.get('return')){
            const returnEntity = new SemanticEntity({
                type: 'ImplicitReturnStatement',
                name: 'return',
                loc: node.loc,
                scope: funcScope
            }, funcScope);
            funcScope.entity_states.set('return', returnEntity);

            // if node.argument contains any entities, exprContext.entities will have them
            funcScope.dependency_graph.set(returnEntity, new Set());
        }

        funcScope.resolve_on_exit.forEach(callback => callback());
        
        if (node.onScopeExit){
            node.onScopeExit.forEach(callback => callback(funcScope));
        }

        node.scope = funcScope;

        // merge dependency graphs to parent scope
        for (let entry of funcScope.dependency_graph.entries()){
            scope.dependency_graph.set(entry[0], entry[1]);
        }

        return node;
    },
    FunctionExpression: (node, scope, context) => {
        const entity = new SemanticEntity(node, scope);

        // assign the entity id if the function is anonymous
        if (node.id === null){
            node.id = { type: 'Identifier', name: entity.id, range: [ node.range[0], node.range[1] + 40 ] };
            // INFO: we need to add the "range" property, because escodegen looks for the range if we attach comments.
        }

        analyzeNode.Handlers.FunctionDeclaration(node, scope, context);

        scope.dependency_graph.set(entity, new Set());
        context.entities.add(entity)

        return node;
    },
    ArrowFunctionExpression: (node, scope, context) => {
        const entity = new SemanticEntity(node, scope);

        // assign the entity id if the function is anonymous
        if (node.id === null){
            node.id = { name: entity.id, range: [ node.range[0], node.range[1] + 40 ] };
            // INFO: we need to add the "range" property, because escodegen looks for the range if we attach comments.
        }

        analyzeNode.Handlers.FunctionDeclaration(node, scope, context);

        scope.dependency_graph.set(entity, new Set());
        context.entities.add(entity)

        return node;
    },
    ReturnStatement: (node, scope, context) => {
        const exprContext = new ParsingContext(node, context);
        if (node.argument !== null){
            analyzeNode(node.argument, scope, exprContext);    
        }

        // create new semantic entity
        const entity = new SemanticEntity(node, scope);
        scope.entity_states.set('return', entity);

        // if node.argument contains any entities, exprContext.entities will have them
        scope.dependency_graph.set(entity, exprContext.entities);

        return node;
    },
    CallExpression: (node, scope, context) => {
        // console.log(`(${node.type}) at Line ${node.loc.start.line}: ${escodegen.generate(node)}`);

        let calleeEntity;
        // if (node.callee.type === 'Identifier'){
        //     calleeEntity = scope.getEntity(node.callee.name);
        // }
        // else if (node.callee.type === 'MemberExpression') {
        //     // calleeEntity = scope.getEntity(node.callee.name);

        //     const callContext = new ParsingContext(node, context);
        //     node.callee = analyzeNode(node.callee, scope, callContext);

        //     calleeEntity = callContext.entities.values().next().value;
        // }

        const callContext = new ParsingContext(node, context);
        node.callee = analyzeNode(node.callee, scope, callContext);

        calleeEntity = callContext.entities.values().next().value;

        // console.log(node);
        // console.log(`(${node.type}) at Line ${node.loc.start.line} in ${scope.$root.absSourcePath}: ${escodegen.generate(node)}`);
        // console.log(calleeEntity);

        // require is a special case that must be handled separately
        if ((calleeEntity instanceof BuiltInEntity) && calleeEntity.id === 'require'){
            // console.log(escodegen.generate(node));

            // see if required module is a built-in lib
            if (node.arguments[0].type === 'Literal'){
                if (BUILTIN_LIBS.includes(node.arguments[0].value)){
                    let moduleEntity = requiredBuiltIns[node.arguments[0].value];

                    if (!moduleEntity){
                        const actualModule = require(node.arguments[0].value);
                        moduleEntity = new BuiltInEntity(node.arguments[0].value, actualModule, scope.$root);
                        requiredBuiltIns[node.arguments[0].value] = moduleEntity;
                    }

                    context.entities.add(moduleEntity);
                    return node;
                }
                // we assume that user-defined local module names start with "."
                else if (node.arguments[0].value[0] !== '.' && DEEP_MODE === false){
                    // TODO: perform the require while in the working directory of the source code
                    //       to ensure that the required package exists
                    let moduleEntity = requiredExternals[node.arguments[0].value];

                    if (!moduleEntity){

                        // check if a plugin was provided to handle the library
                        if (EXTERNAL_LIBS[node.arguments[0].value]){
                            moduleEntity = ExternalEntity.createFromDefinition(node.arguments[0].value, EXTERNAL_LIBS[node.arguments[0].value], scope.$root, node.loc);
                            requiredExternals[node.arguments[0].value] = moduleEntity;
                        }
                        else {
                            const modulePath = require.resolve(node.arguments[0].value, { paths: generateNodeModulePaths(scope.$root.absSourcePath) });
                            const actualModule = require(modulePath);
                            moduleEntity = new ExternalEntity(node.arguments[0].value, actualModule, scope.$root);
                            requiredExternals[node.arguments[0].value] = moduleEntity;
                        }
                    }

                    context.entities.add(moduleEntity);
                    return node;
                }
                else {

                    const modulePath = require.resolve(node.arguments[0].value, { paths: [ path.dirname(scope.$root.absSourcePath) ].concat(generateNodeModulePaths(scope.$root.absSourcePath)) });
                    // let modulePath;

                    // if (node.arguments[0].value[0] !== '.' && DEEP_MODE){
                    //     modulePath = require.resolve(node.arguments[0].value, { paths: generateNodeModulePaths(scope.$root.absSourcePath) });
                    // }
                    // else {
                    //     // resolve the required module path, which is relative to the source path
                    //     modulePath = path.resolve(scope.$root.sourceDir, node.arguments[0].value);
                    // }
                    
                    // console.log(modulePath);

                    let moduleExports = requiredModules[modulePath];

                    if (!moduleExports){
                        let moduleCode = fs.readFileSync(modulePath, 'utf8');
                        if (DEBUG){
                            console.log(`  required module ${modulePath}`);
                        }

                        // if the required file is a JSON file, we need a small hack
                        // because esprima will throw a syntax error parsing raw JSON files
                        if (path.extname(modulePath) === '.json'){
                            moduleCode = 'module.exports = ' + moduleCode;
                        }

                        const tree = esprima.parse(moduleCode, { range: true, loc: true, comment: true });
                        tree.absSourcePath = modulePath;                   // TODO: Revise this 
                        tree.sourceDir = path.dirname(modulePath);         // TODO: Revise this
                        tree.packageInfo = scope.$root.packageInfo;

                        // if the required file is a JSON file, we need a small hack
                        // because esprima will throw a syntax error parsing raw JSON files
                        // if (path.extname(modulePath) === '.json'){
                        //     tree.body[0] = tree.body[0].expression;
                        // }

                        // console.log(`\n---< Begin Analysis >--- \n`);
                        analyzeNode(tree);
                        // console.log(`\n---<  End Analysis  >---\n`);

                        const moduleEntity = tree.scope.entity_states.get('module');
                        moduleExports = moduleEntity.entity_states.get('exports');

                        // console.log(`  module ${modulePath} entity: `);
                        // console.log(moduleEntity.toString());

                        // some third-party libs will either dynamically populate `module.exports`
                        // or simply assign to `exports`
                        if (!moduleExports){
                            moduleExports = tree.scope.entity_states.get('exports');
                        }

                        // console.log(Array.from(moduleExports.entity_states.keys()));

                        // console.log(moduleExports);
                        while (moduleExports.node.type === 'MemberExpression'){
                            moduleExports = tree.scope.dependency_graph.get(moduleExports).values().next().value;
                        }

                        // console.log(moduleExports);

                        // merge dependency graphs to main root scope
                        for (let entry of tree.scope.dependency_graph.entries()){
                            scope.$root.dependency_graph.set(entry[0], entry[1]);
                        }

                        requiredModules[modulePath] = moduleExports;

                        // include module tree in the main tree
                        tree.scope.loadedModules.forEach(moduleTree => {
                            scope.$root.loadedModules.push(moduleTree);
                        });
                        scope.$root.loadedModules.push(tree);
                    }

                    // console.log(moduleExports);

                    context.entities.add(moduleExports);
                    return node;
                }
            }
            else {
                if (!STRICT_MODE){

                    const unknownModule = new SemanticEntity({
                        type: 'UnknownObject',
                        name: escodegen.generate(node),
                        loc: node.loc,
                        scope: scope
                    }, scope)

                    scope.$root.dependency_graph.set(unknownModule, new Set());

                    context.entities.add(unknownModule);

                    return node;
                }

                throw new AnalyzerException(`Cannot handle dynamic require (${escodegen.generate(node)}) -- to assign an unknown entity to a dynamically required module, run with STRICT_MODE=false`);
            }
        }

        // console.log('Line ' + node.loc.start.line, escodegen.generate(node), calleeEntity);

        // console.log(node.loc.start.line + ': ' + escodegen.generate(node));
        // console.log(calleeEntity);

        // calleeEntity could be an indirect reference, so resolve it first
        while (calleeEntity && !(['FunctionDeclaration', 'FunctionExpression', 'ArrowFunctionExpression', 'MethodDefinition', 'UnknownObject', 'BuiltIn:Function', 'External:Function', 'IO:Writer', 'ChildProcess'].includes(calleeEntity.node.type))){
            // console.log(calleeEntity.toString());
            const calleeDependencies = calleeEntity.scope.dependency_graph.get(calleeEntity);

            // In certain cases, calleeEntity will fail to resolve
            // because the callee function has not been processed yet
            // (e.g., when the callee function is a hoisted function defined at the bottom)
            // In such cases, it is fine to resolve to the FunctionReturn entity,
            // as the hoisted function will eventually be resolved through the resolve_on_exit callback
            if (!calleeDependencies){
                if (calleeEntity.node.type === 'FunctionReturn'){
                    break;
                }
                else if (calleeEntity.node.type === 'ThisExpression'){
                    break;
                }
                else {
                    throw new AnalyzerException(`Could not resolve the calleeEntity [${calleeEntity.toString()}] while processing CallExpression ${escodegen.generate(node)}`);
                }
            }

            // console.log('Line ' + node.loc.start.line, escodegen.generate(node), calleeEntity);

            calleeEntity = calleeDependencies.values().next().value;

            // console.log('Line ' + node.loc.start.line, escodegen.generate(node), calleeEntity);

            // if calleeEntity is a conditional entity, we create a new unknown object entity,
            // and add the conditional entity as a dependency
            if (calleeEntity instanceof ConditionalEntity){
                const undecided = new SemanticEntity({
                    type: 'UnknownObject',
                    name: escodegen.generate(node.callee),
                    loc: node.callee.loc,
                    scope: scope
                }, scope);
                scope.dependency_graph.set(undecided, new Set([ calleeEntity ]));
                calleeEntity = undecided;
                break;
            }
        }

        // console.log('Line ' + node.loc.start.line, escodegen.generate(node), calleeEntity);

        // It is possible that we cannot determine whether a given callee is a valid function or not.
        // (e.g., when the function being called is passed as an argument)
        // In that case, we will simply assume that the callee is a function.
        // (Since we assume that we would analyze functionally working code).
        // However, we'll indicate it as an unknown function
        if (!calleeEntity){
            calleeEntity = new SemanticEntity({
                type: 'UnknownObject',
                name: escodegen.generate(node.callee),
                loc: node.callee.loc,
                scope: scope
            }, scope);
        }

        // attach the entity to the callee node, so that
        // when processing the call arguments, we can look up
        // the callee entity if needed
        node.callee.entity = calleeEntity;

        // We create two semantic entities to represent a function invocation.
        // the FunctionReturn will be a direct dependency for any of the sinks
        // following this CallExpression.
        // The FunctionEnter is not added as a direct dependency to any entity,
        // but we'll be able to dynamically resolve the dependency while
        // traversing the dependency graph
        const invocation = SemanticEntity.createFunctionInvocation(calleeEntity, scope, node);

        // process arguments after processing the callee and invocation entities
        // because some arguments such as callback functions require information
        // about the callee and the invocation entities.
        const argContext = new ParsingContext(node, context);
        argContext.invocation = invocation;
        node.arguments.forEach((child, index, list) => {
            list[index] = analyzeNode(child, scope, argContext);
        });

        // add dependencies for the function call after processing the arguments
        scope.dependency_graph.set(invocation.enter, argContext.entities);

        // determine the entity to return
        if (!(calleeEntity instanceof BuiltInEntity || calleeEntity instanceof ExternalEntity)){
            // console.log(calleeEntity.node.type + ' is not a Built in Entity');
            // console.log(calleeEntity);
            
            if (calleeEntity.node.type === 'UnknownObject' || calleeEntity.node.type === 'IO:Writer'){
                scope.dependency_graph.set(invocation.exit, new Set([ invocation.enter ]));

                const unknownEntity = new SemanticEntity({
                    type: 'UnknownObject',
                    name: escodegen.generate(node),
                    loc: node.loc,
                    scope: scope
                }, scope);
                // scope.dependency_graph.set(invocation.exit, new Set([ unknownEntity ]));
                scope.dependency_graph.set(unknownEntity, new Set([ invocation.exit ]));
                invocation.exit = unknownEntity;

                // in case the callee and returned values are both unknown, we track the
                // dependency along the chain of function calls
                if (node.callee.type === 'MemberExpression'){
                    scope.dependency_graph.get(unknownEntity).add(node.callee.object.entity);
                    // scope.dependency_graph.set(unknownEntity, new Set([ node.callee.object.entity ]));
                }
            }
            else if (calleeEntity.node.type === 'MethodDefinition'){
                const calleeReturn = calleeEntity.node.value.scope.entity_states.get('return');
                scope.dependency_graph.set(invocation.exit, new Set([ calleeReturn ]));
            }
            else if (calleeEntity.node.type === 'FunctionReturn'){
                scope.dependency_graph.set(invocation.exit, new Set([ calleeEntity ]));
            }
            else if (calleeEntity.node.type === 'External:Function'){
                scope.dependency_graph.set(invocation.exit, new Set([ calleeEntity ]));
            }
            // if calleeEntity.node.scope is available, the callee function was already processed
            // and all its dependency information is available. We can proceed normally
            else if (calleeEntity.node.scope){
                const calleeReturn = calleeEntity.node.scope.entity_states.get('return');
                
                scope.dependency_graph.set(invocation.exit, new Set([ calleeReturn ]));

                // link the dynamic FunctionEnter invocationEntity to the concrete FunctionEnter entity
                const calleeScope = calleeEntity.node.scope;
                if (calleeScope.invocationEntity){
                    const invocationDependencies = calleeScope.dependency_graph.get(calleeScope.invocationEntity);
                    invocationDependencies.add(invocation.enter);

                    // link function parameters to call arguments
                    calleeEntity.node.params.forEach((item, index) => {
                        if (node.arguments[index] && node.arguments[index].entity){
                            const argEntity = node.arguments[index].entity;

                            const parDependencies = calleeScope.dependency_graph.get(item.entity);
                            parDependencies.add(argEntity);
                        }
                    });
                }
            }
            // if calleeEntity.node.scope is not available, that means that the callee function
            // is declared **after** the current code -- it is likely a hoisted function.
            // In that case, we push a callback function that will be called at the end of
            // processing the current scope.
            else {
                // scope.$root.resolve_on_exit.push(() => {
                //     const calleeReturn = calleeEntity.node.scope.entity_states.get('return');
                    
                //     scope.dependency_graph.set(invocation.exit, new Set([ calleeReturn ]));

                //     // link the dynamic FunctionEnter invocationEntity to the concrete FunctionEnter entity
                //     const calleeScope = calleeEntity.node.scope;
                //     if (calleeScope.invocationEntity){
                //         const invocationDependencies = calleeScope.dependency_graph.get(calleeScope.invocationEntity);
                //         invocationDependencies.add(invocation.enter);

                //         // link function parameters to call arguments
                //         calleeEntity.node.params.forEach((item, index) => {
                //             if (node.arguments[index] && node.arguments[index].entity){
                //                 const argEntity = node.arguments[index].entity;

                //                 const parDependencies = calleeScope.dependency_graph.get(item.entity);
                //                 parDependencies.add(argEntity);
                //             }
                //         });
                //     }
                // });

                if (!calleeEntity.node.onScopeInitialized){
                    calleeEntity.node.onScopeInitialized = [];
                }

                calleeEntity.node.onScopeInitialized.push((calleeScope) => {
                    // link the dynamic FunctionEnter invocationEntity to the concrete FunctionEnter entity
                    if (calleeScope.invocationEntity){
                        const invocationDependencies = calleeScope.dependency_graph.get(calleeScope.invocationEntity);
                        invocationDependencies.add(invocation.enter);

                        // link function parameters to call arguments
                        calleeEntity.node.params.forEach((item, index) => {
                            if (node.arguments[index] && node.arguments[index].entity){
                                const argEntity = node.arguments[index].entity;

                                const parDependencies = calleeScope.dependency_graph.get(item.entity);
                                parDependencies.add(argEntity);
                            }
                        });
                    }
                });

                if (!calleeEntity.node.onScopeExit){
                    calleeEntity.node.onScopeExit = [];
                }

                calleeEntity.node.onScopeExit.push((calleeScope) => {

                    const calleeReturn = calleeScope.entity_states.get('return');
                    
                    // we add the dependency in the calleeScope (not scope)
                    // because by the time this callback is invoked, we would have finished
                    // processing `scope`, so that the dependencies would not be merged
                    calleeScope.dependency_graph.set(invocation.exit, new Set([ calleeReturn ]));
                });
            }

            // context.entities.add(calleeReturn);
        }
        else if (calleeEntity instanceof BuiltInEntity) {
            // console.log(calleeEntity.node.type + ' is a Built in Entity');
            // context.entities.add(calleeEntity);
            SPECIAL_BUILTIN_HOOKS['KnownReturnTypes in CallExpression'](node, scope, calleeEntity, invocation);

            scope.dependency_graph.set(invocation.exit, new Set([ invocation.enter ]));

            // in case the callee is a property of some object,
            // we assume that the call depends on the object as well
            if (node.callee.type === 'MemberExpression'){
                scope.dependency_graph.get(invocation.exit).add(node.callee.object.entity);
                // scope.dependency_graph.set(unknownEntity, new Set([ node.callee.object.entity ]));
            }
        }
        else if (calleeEntity instanceof ExternalEntity) {

            // scope.dependency_graph.set(invocation.exit, new Set([ invocation.enter ]));
            scope.dependency_graph.set(invocation.exit, new Set([ invocation.enter, calleeEntity ]));

            if (calleeEntity.returns){
                const namespace = calleeEntity.id.split('.')[0];

                const definition = findDefinition(namespace + '.' + calleeEntity.returns);

                const externalEntity = ExternalEntity.createFromDefinition(calleeEntity.id + '.' + calleeEntity.returns, definition, scope, node.loc);

                scope.dependency_graph.set(externalEntity, new Set([ invocation.exit ]));
                invocation.exit = externalEntity;
            }
            else {
                const unknownEntity = new SemanticEntity({
                    type: 'UnknownObject',
                    name: escodegen.generate(node),
                    loc: node.loc,
                    scope: scope
                }, scope);

                scope.dependency_graph.set(unknownEntity, new Set([ invocation.exit ]));
                invocation.exit = unknownEntity;
            }
        }

        // add invocation to the current parsing context,
        // so that any sinks can add it as a dependency
        context.entities.add(invocation.exit);

        SPECIAL_BUILTIN_HOOKS['CallExpression'](node, scope, calleeEntity);

        return node;
    },
    ObjectExpression: (node, scope, context) => {
        const entity = new SemanticEntity(node, scope);

        context.entities.add(entity);

        const objContext = new ParsingContext(node, context);
        node.properties.forEach((child, index, list) => {
            list[index] = analyzeNode(child, scope, objContext);
        });

        scope.dependency_graph.set(entity, objContext.entities);

        return node;
    },
    Property: (node, scope, context) => {
        const entity = new SemanticEntity(node, scope);

        context.entities.add(entity);

        // update entity state in parent (object entity)
        context.node.entity.entity_states.set(node.key.name, entity);

        const propContext = new ParsingContext(node, context);
        node.value = analyzeNode(node.value, scope, propContext);

        scope.dependency_graph.set(entity, propContext.entities);

        return node;
    },
    ArrayExpression: (node, scope, context) => {
        const entity = new SemanticEntity(node, scope);

        context.entities.add(entity);

        const objContext = new ParsingContext(node, context);
        node.elements.forEach((child, index, list) => {
            list[index] = analyzeNode(child, scope, objContext);
        });

        scope.dependency_graph.set(entity, objContext.entities);

        return node;
    },
    MemberExpression: (node, scope, context) => {
        // console.log(`(${node.type}) at Line ${node.loc.start.line}: ${escodegen.generate(node)}\t${scope.$root.absSourcePath}`);
        let objectEntity;

        if (node.object.type === 'Identifier'){
            // fetch the entity bound to the object
            objectEntity = scope.getEntity(node.object.name);
            // console.log(node.object.name);

            // console.log(escodegen.generate(node.object) + ' at line ' + node.loc.start.line);
            // console.log(objectEntity.toJSON());

            if (!objectEntity){
                if (STRICT_MODE){
                    throw new AnalyzerException(`Identifier "${node.object.name}" does not map to any SemanticEntity (while handling ${escodegen.generate(context.node)}, Line ${node.object.loc.start.line}, Column ${node.object.loc.start.column} in ${scope.$root.absSourcePath})`);
                }

                objectEntity = new SemanticEntity({
                    type: 'UnknownObject',
                    name: escodegen.generate(node.object),
                    loc: node.object.loc,
                    scope: scope
                }, scope);
                scope.entity_states.set(node.object.name, objectEntity);
                scope.dependency_graph.set(objectEntity, new Set());
            }

            // look up the dependency chain
            while (objectEntity.node.type === 'Identifier'){
                const dependency = objectEntity.scope.dependency_graph.get(objectEntity).values().next().value;

                if (!dependency){
                    // Sometimes, the identifier could be declared, but not initialized or assigned a value yet.
                    // The assignment could happen in some other scope, e.g., conditional statements.
                    // In such cases, we just use an unknown object.
                    // WARN: this will actually break the dependency chain, so we will need to come up with
                    //       a better way to handle this case
                    objectEntity = new SemanticEntity({
                        type: 'UnknownObject',
                        name: escodegen.generate(objectEntity.node),
                        loc: objectEntity.node.loc,
                        scope: objectEntity.scope
                    }, objectEntity.scope);
                    scope.entity_states.set(objectEntity.node.name, objectEntity);
                    scope.dependency_graph.set(objectEntity, new Set());
                }
                else {
                    objectEntity = dependency;
                }
            }

            // if objectEntity is an UnknownObject, check if
            // it has dependencies, and assume a certain entity
            // if all its dependencies are of the same type
            // TODO: We need a better way to do type inference here
            if (objectEntity.node.type === 'UnknownObject'){
                const dependencies = objectEntity.scope.dependency_graph.get(objectEntity);
                if (!dependencies && DEBUG){
                    console.log(`WARN: The following 'UnknownObject' has no dependencies:\n\t${objectEntity.node.name} (in line ${objectEntity.node.loc.start.line} in ${objectEntity.scope.$root.absSourcePath})`);
                    // console.log(objectEntity.toJSON());
                }
                if (dependencies && dependencies.size > 0){
                    const resolvedDependencies = Array.from(dependencies).map(item => item.getSourceEntity());

                    // console.log(resolvedDependencies);

                    if (resolvedDependencies.length === 1){
                        objectEntity = resolvedDependencies[0];
                    }
                    else if (resolvedDependencies.reduce((acc, item) => acc && item === resolvedDependencies[0], true)){
                        objectEntity = resolvedDependencies[0];
                    }
                }
            }
        }
        else {
            // console.log('  in\t' + escodegen.generate(node.object));

            const exprContext = new ParsingContext(node, context);
            node.object = analyzeNode(node.object, scope, exprContext);

            objectEntity = exprContext.entities.values().next().value;

            // console.log(escodegen.generate(node.object) + ' at line ' + node.loc.start.line);
            // console.log(objectEntity.toJSON());

            // console.log(objectEntity);
            // console.log(objectEntity.node.type + ' ' + objectEntity.node.name + ' > next in\t' + escodegen.generate(node));

            // look up the dependency chain
            if (!(objectEntity instanceof BuiltInEntity || objectEntity instanceof ExternalEntity || objectEntity instanceof LiteralEntity || ['UnknownObject', 'ThisExpression', 'ObjectExpression', 'ArrayExpression', 'FunctionDeclaration', 'FunctionExpression', 'ConditionalExpression', 'IO:FileContent', 'IO:ProcessOutput', 'ChildProcess', 'IO:ProcessReadStream', 'IO:ProcessWriteStream', 'IO:SocketContent'].includes(objectEntity.node.type))){
                // console.log(objectEntity.toString());
                // console.log(objectEntity.node.type + ' ' + objectEntity.node.name + ' > next in\t' + escodegen.generate(node) + ' (Line '+ node.loc.start.line + ' in ' + scope.$root.absSourcePath +')');

                const objectDependencies = objectEntity.scope.dependency_graph.get(objectEntity);

                // console.log(objectDependencies);

                // In certain cases, objectEntity will fail to resolve
                // because the object might be a returned object from a function that has not been processed yet
                // (e.g., when the function is a hoisted function defined at the bottom)
                // In such cases, it is fine to resolve to the FunctionReturn entity,
                // as the hoisted function will eventually be resolved through the resolve_on_exit callback
                if (!objectDependencies){
                    if (objectEntity.node.type !== 'FunctionReturn'){
                        throw new AnalyzerException(`Could not resolve the objectEntity [${objectEntity.toString()}] while processing MemberExpression ${escodegen.generate(node)}`);
                    }
                }
                else if (objectDependencies.size === 1) {
                    objectEntity = objectDependencies.values().next().value;
                }
                // console.log(objectEntity);
                // console.log(objectEntity.node.type + ' ' + objectEntity.node.name + ' > next in\t' + escodegen.generate(node));
            }

            // if (objectEntity.node.type === 'ThisExpression'){
            //     // try to resolve "this", in case the context is clear (e.g., "this" inside a method)

            //     const thisEntity = scope.getEntity('this');
            //     if (thisEntity && thisEntity.node.type === 'ClassDeclaration'){
            //         objectEntity = thisEntity;
            //     }
            // }

            // console.log(objectEntity);

            // console.log('------\n');
        }

        if (objectEntity.node.type === 'ThisExpression'){
            // try to resolve "this", in case the context is clear (e.g., "this" inside a method)

            const thisEntity = objectEntity.scope.getEntity('this');
            // console.log(thisEntity.toJSON());
            // if (thisEntity && thisEntity.node.type === 'ClassDeclaration'){
            //     objectEntity = thisEntity;
            // }
            if (thisEntity){
                objectEntity = thisEntity;
            }
        }

        // attach the entity to the node, in case it needs to be looked up elsewhere
        // (e.g., when processing CallExpression)
        node.object.entity = objectEntity;

        // console.log(escodegen.generate(node));
        // console.log(objectEntity.toJSON());

        const entity = new SemanticEntity(node, scope);

        const dependencies = new Set();

        // fetch the entity bound to the property
        // propEntity must be added as the first dependency,
        // so that it is found when we're performing name resolution
        // (e.g., in a call expression to find the callee function)
        let propEntity = objectEntity.entity_states.get(node.property.name);

        // console.log(propEntity);

        if (propEntity){
            dependencies.add(propEntity);
            // const propDependencies = scope.dependency_graph.get(propEntity);
            // if (propDependencies){
            //     for (let item of propDependencies){
            //         dependencies.add(item);
            //     }
            // }
        }
        else {
            propEntity = new SemanticEntity({
                type: 'UnknownObject',
                name: escodegen.generate(node.property),
                loc: node.property.loc,
                scope: scope
            }, scope);
            scope.dependency_graph.set(propEntity, new Set([ objectEntity ]));
            dependencies.add(propEntity);
        }

        // Similar to UnknownObjects, we assume that a property references some object
        // even though we cannot know statically whether there is actually an object or not
        // if (!propEntity){
        //     propEntity = new SemanticEntity({
        //         type: 'UnknownObject',
        //         name: escodegen.generate(node),
        //         loc: node.loc,
        //         scope: scope
        //     }, scope);
        // }

        if (node.computed){
            const propContext = new ParsingContext(node, context);
            analyzeNode(node.property, scope, propContext);
            // scope.dependency_graph.set(propEntity, propContext.entities);
            for (let item of propContext.entities){
                dependencies.add(item);
            }

            dependencies.add(objectEntity);
        }

        // dependencies.add(objectEntity);

        scope.dependency_graph.set(entity, dependencies);

        // // merge the dependencies of the object and the property
        // const mergedDependencies = new Set();
        // const objDependencies = objectEntity.scope.dependency_graph.get(objectEntity);
        // const propDependencies = scope.dependency_graph.get(propEntity);
        // if (objDependencies){
        //     for (let item of objDependencies){
        //         mergedDependencies.add(item);
        //     }
        // }
        // if (propDependencies){
        //     for (let item of propDependencies){
        //         mergedDependencies.add(item);
        //     }
        // }
        // scope.dependency_graph.set(propEntity, mergedDependencies);

        

        // console.log(propEntity);
        
        // context.entities.add(propEntity);
        context.entities.add(entity);

        // console.log('<<--- returning');

        return node;
    },
    NewExpression: (node, scope, context) => {
        // new expression is similar to a function call.
        // The difference is that the returned object does not depend on the
        // return value of the constructor function.

        // console.log(`(${node.type}) at Line ${node.loc.start.line}: ${escodegen.generate(node)}`);

        let calleeEntity;

        const callContext = new ParsingContext(node, context);
        node.callee = analyzeNode(node.callee, scope, callContext);

        calleeEntity = callContext.entities.values().next().value;

        // console.log(node);
        // console.log(`(${node.type}) at Line ${node.loc.start.line} in ${scope.$root.absSourcePath}: ${escodegen.generate(node)}`);
        // console.log(calleeEntity);

        // console.log(escodegen.generate(node), calleeEntity);

        // calleeEntity could be an indirect reference, so resolve it first
        while (calleeEntity && !(['ClassDeclaration', 'FunctionDeclaration', 'FunctionExpression', 'ArrowFunctionExpression', 'MethodDefinition', 'UnknownObject', 'BuiltIn:Function', 'External:Function', 'IO:Writer', 'ChildProcess'].includes(calleeEntity.node.type))){
        // while (!(['FunctionDeclaration', 'FunctionExpression', 'ClassDeclaration', 'BuiltIn:Function', 'External:Function'].includes(calleeEntity.node.type))){
            // console.log(calleeEntity.toString() + `\tin ${escodegen.generate(node)} (Line ${node.loc.start.line} in ${scope.$root.absSourcePath})`);
            
            const calleeDependencies = calleeEntity.scope.dependency_graph.get(calleeEntity);

            // In certain cases, calleeEntity will fail to resolve
            // because the callee function has not been processed yet
            // (e.g., when the callee function is a hoisted function defined at the bottom)
            // In such cases, it is fine to resolve to the FunctionReturn entity,
            // as the hoisted function will eventually be resolved through the resolve_on_exit callback
                if (!calleeDependencies){
                if (calleeEntity.node.type === 'FunctionReturn'){
                    break;
                }
                else if (calleeEntity.node.type === 'ThisExpression'){
                    break;
                }
                else {
                    throw new AnalyzerException(`Could not resolve the calleeEntity [${calleeEntity.toString()}] while processing CallExpression ${escodegen.generate(node)}`);
                }
            }

            calleeEntity = calleeDependencies.values().next().value;

            // if calleeEntity is a conditional entity, we create a new unknown object entity,
            // and add the conditional entity as a dependency
            if (calleeEntity instanceof ConditionalEntity){
                const undecided = new SemanticEntity({
                    type: 'UnknownObject',
                    name: escodegen.generate(node.callee),
                    loc: node.callee.loc,
                    scope: scope
                }, scope);
                scope.dependency_graph.set(undecided, new Set([ calleeEntity ]));
                calleeEntity = undecided;
                break;
            }
        }

        // We create two semantic entities to represent a function invocation.
        // the FunctionReturn will be a direct dependency for any of the sinks
        // following this CallExpression.
        // The FunctionEnter is not added as a direct dependency to any entity,
        // but we'll be able to dynamically resolve the dependency while
        // traversing the dependency graph
        const invocation = SemanticEntity.createFunctionInvocation(calleeEntity, scope, node);

        // process arguments after processing the callee and invocation entities
        // because some arguments such as callback functions require information
        // about the callee and the invocation entities.
        const argContext = new ParsingContext(node, context);
        node.arguments.forEach((child, index, list) => {
            list[index] = analyzeNode(child, scope, argContext);
        });

        // add dependencies for the function call
        scope.dependency_graph.set(invocation.enter, argContext.entities);

        if (!(calleeEntity instanceof BuiltInEntity)){
            // console.log(calleeEntity);
            
            const entity = new SemanticEntity(node, scope);
            
            // Check if calleeEntity is a Class Declaration
            if (calleeEntity.node.type === 'ClassDeclaration'){
                // make class methods available in the returned entity
                for (let entry of calleeEntity.entity_states){
                    entity.entity_states.set(entry[0], entry[1]);
                }

                invocation.exit = entity;

                scope.dependency_graph.set(invocation.exit, new Set([ calleeEntity ]));
            }
            else if (calleeEntity.node.type === 'External:Function'){
                scope.dependency_graph.set(invocation.exit, new Set([ invocation.enter ]));
            }
            // if calleeEntity.node.scope is available, the callee function was already processed
            // and all its dependency information is available. We can proceed normally
            else if (calleeEntity.node.scope){
                // const calleeReturn = calleeEntity.node.scope.entity_states.get('return');
                // scope.dependency_graph.set(invocation.exit, new Set([ calleeReturn ]));
            }
            // if calleeEntity.node.scope is not available, that means that the callee function
            // is declared **after** the current code -- it is likely a hoisted function.
            // In that case, we push a callback function that will be called at the end of
            // processing the current scope.
            else {
                scope.resolve_on_exit.push(() => {
                    // const calleeReturn = calleeEntity.node.scope.entity_states.get('return');
                    // scope.dependency_graph.set(invocation.exit, new Set([ calleeReturn ]));
                });
            }

            // context.entities.add(calleeReturn);
        }
        else {
            // context.entities.add(calleeEntity);
            SPECIAL_BUILTIN_HOOKS['KnownReturnTypes in NewExpression'](node, scope, calleeEntity, invocation);

            scope.dependency_graph.set(invocation.exit, new Set([ invocation.enter ]));
        }

        // add invocation to the current parsing context,
        // so that any sinks can add it as a dependency
        context.entities.add(invocation.exit);

        return node;
    },
    ThisExpression: (node, scope, context) => {
        const entity = new SemanticEntity(node, scope);

        context.entities.add(entity);

        // see if 'this' exists in scope
        const existingThis = scope.getEntity('this');
        if (existingThis){
            scope.dependency_graph.set(entity, new Set([ existingThis ]));
        }

        return node;
    },
    Super: (node, scope, context) => {
        // look up entity
        let entity = scope.getEntity('super');

        if (!entity && !STRICT_MODE){
            entity = new SemanticEntity({
                type: 'UnknownObject',
                name: 'super',
                loc: node.loc,
                scope: scope
            }, scope);
            scope.entity_states.set('super', entity);
            scope.dependency_graph.set(entity, new Set());
        }

        // if (context.node.type === 'VariableDeclarator' || context.node.type === 'AssignmentExpression'){
        if (!entity){
            throw new AnalyzerException(`'super' does not map to any SemanticEntity (while handling ${escodegen.generate(context.node)})`);
        }

        context.entities.add(entity);
        // }

        node.entity = entity;

        return node;
    },
    ClassDeclaration: (node, scope, context) => {
        const entity = new SemanticEntity(node, scope);
        scope.entity_states.set(node.id.name, entity);
        scope.dependency_graph.set(entity, new Set());

        const clsContext = new ParsingContext(node, context);

        const clsScope = new LexicalScope(node.id.name, scope);
        clsScope.initEntities(node);

        if (node.superClass){
            const superContext = new ParsingContext(node, context);

            analyzeNode(node.superClass, scope, superContext);

            const superClass = superContext.entities.values().next().value;

            // set "super" to refer to the superclass entity
            clsScope.entity_states.set('super', superClass);
        }

        // set "this" to refer to the class entity
        clsScope.entity_states.set('this', entity);

        node.body.body.forEach((child, index, list) => {
            list[index] = analyzeNode(child, clsScope, clsContext);

            // add the child entity to the class entity, not the class' scope.
            // (because a method is referenced as "this.methodName", not "methodName")
            entity.entity_states.set(child.key.name, child.entity);
        });

        clsScope.resolve_on_exit.forEach(callback => callback());

        node.scope = clsScope;

        // merge dependency graphs to parent scope
        for (let entry of clsScope.dependency_graph.entries()){
            scope.dependency_graph.set(entry[0], entry[1]);
        }

        context.entities.add(entity);

        return node;
    },
    ClassExpression: (node, scope, context) => {
        const entity = new SemanticEntity(node, scope);

        // assign the entity id if the function is anonymous
        if (node.id === null){
            node.id = { type: 'Identifier', name: entity.id, range: [ node.range[0], node.range[1] + 40 ] };
            // INFO: we need to add the "range" property, because escodegen looks for the range if we attach comments.
        }

        analyzeNode.Handlers.ClassDeclaration(node, scope, context);

        scope.dependency_graph.set(entity, new Set());
        context.entities.add(entity)

        return node;
    },
    MethodDefinition: (node, scope, context) => {
        const entity = new SemanticEntity(node, scope);
        scope.dependency_graph.set(entity, new Set());

        // explicitly assign the function id
        node.value.id = node.key;

        analyzeNode.Handlers.FunctionDeclaration(node.value, scope, context);

        return node;
    },
    IfStatement: (node, scope, context) => {
        // dependency_graph only captures dataflow dependency
        // control flow dependency is captured by the ConditionalEntity construct
        const entity = new ConditionalEntity(node, scope);

        const condContext = new ParsingContext(node, context);
        const condition = new SemanticEntity(node.test, scope);
        analyzeNode(node.test, scope, condContext);
        scope.dependency_graph.set(condition, condContext.entities);

        entity.condition = condition;

        const cnsqContext = new ParsingContext(node, context);
        const consequent = new SemanticEntity(node.consequent, scope);
        analyzeNode(node.consequent, scope, cnsqContext);
        scope.dependency_graph.set(consequent, cnsqContext.entities);

        entity.outcomes.push(consequent);

        if (node.alternate){
            const altContext = new ParsingContext(node, context);
            const alternate = new SemanticEntity(node.alternate, scope);
            analyzeNode(node.alternate, scope, altContext);
            scope.dependency_graph.set(alternate, altContext.entities);

            entity.outcomes.push(alternate);
        }

        context.entities.add(entity);

        return node;
    },
    SwitchStatement: (node, scope, context) => {

        // dependency_graph only captures dataflow dependency
        // control flow dependency is captured by the ConditionalEntity construct
        const entity = new ConditionalEntity(node, scope);

        const condContext = new ParsingContext(node, context);
        const condition = new SemanticEntity(node.discriminant, scope);
        analyzeNode(node.discriminant, scope, condContext);
        scope.dependency_graph.set(condition, condContext.entities);

        entity.condition = condition;

        node.cases.forEach((child, index, list) => {
            const cnsqContext = new ParsingContext(node, context);
            const consequent = new SemanticEntity(child, scope);
            analyzeNode(child, scope, cnsqContext);
            scope.dependency_graph.set(consequent, cnsqContext.entities);

            entity.outcomes.push(consequent);
        });

        context.entities.add(entity);

        return node;
    },
    SwitchCase: (node, scope, context) => {
        node.consequent.forEach((child, index, list) => {
            list[index] = analyzeNode(child, scope, context);
        });
        return node;
    },
    BlockStatement: (node, scope, context) => {
        node.body.forEach((child, index, list) => {
            list[index] = analyzeNode(child, scope, context);
        });
        return node;
    },
    ForStatement: (node, scope, context) => {
        analyzeNode(node.init, scope, context);
        analyzeNode(node.test, scope, context);
        analyzeNode(node.update, scope, context);
        analyzeNode(node.body, scope, context);
        return node;
    },
    ForOfStatement: (node, scope, context) => {
        // leftContext will contain any new identifiers declared
        const leftContext = new ParsingContext(node, context);
        analyzeNode(node.left, scope, leftContext);

        // rightContext will contain the object to iterate through
        const rightContext = new ParsingContext(node, context);
        analyzeNode(node.right, scope, rightContext);

        for (let entity of leftContext.entities){
            for (let dependency of rightContext.entities){
                scope.dependency_graph.get(entity).add(dependency);
            }
        }

        analyzeNode(node.body, scope, context);
        return node;
    },
    ForInStatement: (node, scope, context) => {
        // leftContext will contain any new identifiers declared
        const leftContext = new ParsingContext(node, context);
        analyzeNode(node.left, scope, leftContext);

        // rightContext will contain the object to iterate through
        const rightContext = new ParsingContext(node, context);
        analyzeNode(node.right, scope, rightContext);

        for (let entity of leftContext.entities){
            for (let dependency of rightContext.entities){
                scope.dependency_graph.get(entity).add(dependency);
            }
        }

        analyzeNode(node.body, scope, context);
        return node;
    },
    WhileStatement: (node, scope, context) => {
        analyzeNode(node.test, scope, context);
        analyzeNode(node.body, scope, context);
        return node;
    },
    DoWhileStatement: (node, scope, context) => {
        analyzeNode(node.test, scope, context);
        analyzeNode(node.body, scope, context);
        return node;
    },
    SpreadElement: (node, scope, context) => {
        analyzeNode(node.argument, scope, context);
        return node;
    },
    ThrowStatement: (node, scope, context) => {
        analyzeNode(node.argument, scope, context);
        return node;
    },
    AwaitExpression: (node, scope, context) => {
        analyzeNode(node.argument, scope, context);
        return node;
    },
    TryStatement: (node, scope, context) => {
        analyzeNode(node.block, scope, context);
        if (node.handler){
            analyzeNode(node.handler, scope, context);
        }
        if (node.finalizer){
            analyzeNode(node.finalizer, scope, context);
        }
        return node;
    },
    CatchClause: (node, scope, context) => {
        const catchContext = new ParsingContext(node, context);
        const catchScope = new LexicalScope('catch-' + crypto.randomBytes(4).toString('hex'), scope);
        
        if (node.param){
            const errorEntity = new SemanticEntity({
                type: 'ErrorObject',
                name: node.param.name,
                loc: node.param.loc,
                scope: scope
            }, scope);
            catchScope.entity_states.set(node.param.name, errorEntity);
            catchScope.dependency_graph.set(errorEntity, new Set());
        }

        analyzeNode(node.body, catchScope, catchContext);

        // merge dependency graphs to parent scope
        for (let entry of catchScope.dependency_graph.entries()){
            scope.dependency_graph.set(entry[0], entry[1]);
        }
    },
    EmptyStatement: (node, scope, context) => node,
    BreakStatement: (node, scope, context) => node,
    ContinueStatement: (node, scope, context) => node
}

function analyze(sourcePath, options, plugins){
    if (options && 'strict' in options){
        STRICT_MODE = options['strict'];
    }
    if (options && 'deep' in options){
        DEEP_MODE = options['deep'];
    }

    // inject plugin objects
    if (plugins && plugins instanceof Array){
        plugins.forEach(plugin => {
            if (plugin.require){
                EXTERNAL_LIBS[plugin.name] = plugin.require;
            }

            if (plugin.hooks){
                for (let hookType in plugin.hooks){
                    if (!EXTERNAL_HOOKS[hookType]){
                        EXTERNAL_HOOKS[hookType] = {};
                    }

                    EXTERNAL_HOOKS[hookType][plugin.name] = plugin.hooks[hookType];
                }
            }

            if (plugin.definitions){
                plugin.definitions.forEach(item => {
                    EXTERNAL_DEFINITIONS[plugin.name + '.' + item.type] = item;
                });
            }
        })
    }

    const startedAt = Date.now();

    let packageJson = null;
    let absSourcePath = path.resolve(sourcePath);
    let sourceDir = path.dirname(absSourcePath);
    let packageInfo = null;
    let code;

    let stat = fs.statSync(absSourcePath);

    if (stat.isFile()){
        code = fs.readFileSync(absSourcePath, 'utf8');
        // console.log(`Read ${absSourcePath}`);

        if (options.packageInfo){
            packageInfo = options.packageInfo;
        }
    }
    else if (stat.isDirectory()) {
        // console.log(`${absSourcePath} is a directory... trying to read as an NPM package`);

        let packageStat = fs.statSync(path.join(absSourcePath, 'package.json'));

        if (packageStat.isFile()){
            packageJson = JSON.parse(fs.readFileSync(path.join(absSourcePath, 'package.json'), 'utf8'));
        }

        packageInfo = plugins.reduce((acc, plugin) => acc || (plugin.package ? plugin.package(packageJson) : null), null);

        if (packageInfo){
            // console.log(`Read package info via plugin`);
            if (packageInfo.main){
                absSourcePath = path.join(absSourcePath, packageInfo.main);
                sourceDir = path.dirname(absSourcePath);
                code = fs.readFileSync(absSourcePath, 'utf8');
                packageInfo.mountPath = sourceDir;
            }
            else {
                absSourcePath = path.join(absSourcePath, 'index.js');
                sourceDir = path.dirname(absSourcePath);
                code = packageInfo.files.map(file => `require('./${file}');`).join('\n');
                packageInfo.mountPath = sourceDir;
            }
        }
        else {
            const basename = './' + path.basename(absSourcePath);
            const dirname = path.dirname(absSourcePath);
            absSourcePath = require.resolve(basename, { paths: [ dirname ]});
            sourceDir = path.dirname(absSourcePath);
            code = fs.readFileSync(absSourcePath, 'utf8');
            // console.log(`Read ${absSourcePath}`);
        }
    }

	let tree = esprima.parse(code, { range: true, loc: true, comment: true, tokens: true });
    tree.absSourcePath = absSourcePath; // TODO: Revise this 
    tree.sourceDir = sourceDir;         // TODO: Revise this
    tree.packageInfo = packageInfo;     // TODO: Revise this
    tree.options = options.tree;        // this is used to pass additional options to plugins

    // console.log(`\n---< Begin Analysis >--- \n`);
    analyzeNode(tree);
    // console.log(`\n---<  End Analysis  >---\n`);

    // console.log(tree.scope.dependency_graph);

    const treeScannedAt = Date.now();

    // organize the nodes and edges
    const nodes = new Set();
    const edges = [];
    for (let sink of tree.scope.dependency_graph.keys()){
        nodes.add(sink);
        // console.log(sink.toJSON());
        let sources = tree.scope.dependency_graph.get(sink);
        // console.log(sources);
        for (let source of sources.values()){
            if (!source){
                console.log(`Something is wrong... source not found`);
                console.log(sink);
                console.log(sources);
            }
            // console.log(source);
            nodes.add(source);
            edges.push({
                source: source.id,
                sink: sink.id
            });
        }
    }
    // console.log('---nodes:');
    // console.log(Array.from(nodes));

    const graph = {
        nodes: Array.from(nodes),
        edges: edges
    };

    const graphMadeAt = Date.now();

    // count the io-related nodes
    const ioCount = {
        source: 0,
        sink: 0,
        // duplex: 0
    };

    const sinks = [];
    const sources = [];
    // const duplexes = [];

    nodes.forEach(node => {
        if (IO_SINK_ENTITY_TYPES.includes(node.node.type)){
            ioCount.sink += 1;
            sinks.push(node);
        }
        else if (IO_SOURCE_ENTITY_TYPES.includes(node.node.type) || IO_SOURCE_ENTITY_TYPES.includes(node.inherits)){
            ioCount.source += 1;
            sources.push(node);
        }
        // else if (IO_DUPLEX_ENTITY_TYPES.includes(node.node.type)){
        //     ioCount.duplex += 1;
        //     duplexes.push(node);
        // }
    });

    graph.sources = sources;
    graph.sinks = sinks;

    // count the flows
    const tracer = FlowTracer(graph);

    const flowsUp = [], flowsDown = [];
    const flowsFromSink = {}, flowsFromSource = {};
    const flowCollectorUp = (node, flow) => {
        if (IO_SOURCE_ENTITY_TYPES.includes(node.node.type) || IO_SOURCE_ENTITY_TYPES.includes(node.inherits)){
            if (!flowsFromSink[flow[0].id]){
                flowsFromSink[flow[0].id] = [];
            }
            flowsFromSink[flow[0].id].push(flow);

            flowsUp.push(flow);
        }
    }
    const flowCollectorDown = (node, flow) => {
        if (IO_SINK_ENTITY_TYPES.includes(node.node.type) || IO_SINK_ENTITY_TYPES.includes(node.inherits)){
            if (!flowsFromSource[flow[0].id]){
                flowsFromSource[flow[0].id] = [];
            }
            flowsFromSource[flow[0].id].push(flow);

            flowsDown.push(flow);
        }
    }

    sinks.forEach(sink => tracer.traceUpstream(sink, flowCollectorUp));
    sources.forEach(source => tracer.traceDownstream(source, flowCollectorDown));

    const ioCountedAt = Date.now();

    const stats = {
        strict_mode: options.strict,
        deep_mode: options.deep,
        started_at: startedAt,
        elapsed: ioCountedAt - startedAt,
        elapsed_tree: treeScannedAt - startedAt,
        elapsed_graph: graphMadeAt - treeScannedAt,
        elapsed_flow: ioCountedAt - graphMadeAt,
        io: ioCount,
        flowsUpstream: flowsUp,
        flowsFromSink: flowsFromSink,
        flowsDownstream: flowsDown,
        flowsFromSource: flowsFromSource
    };

    return { tree, graph, stats };
}

function nodeDescription(node, cwd){
    if (['Identifier', 'FunctionDeclaration', 'Property', 'SinkFunctionEnter', 'FunctionEnter', 'FunctionReturn', 'UnknownObject'].includes(node.type)){
        return `${node.type} "${node.name}" in line ${node.loc.start.line}, column ${node.loc.start.column} in ${path.relative(cwd, node.file)}`;
    }
    else {
        return `${node.type} in line ${node.loc.start.line}, column ${node.loc.start.column} in ${path.relative(cwd, node.file)}`;
    }
}

function FlowTracer(graph){
    const idMap = {};
    graph.nodes.forEach((node, index) => {
        idMap[node.id] = node;
    });

    const edgeMapUp = {};
    graph.edges.forEach(edge => {
        if (!edgeMapUp[edge.sink]){
            edgeMapUp[edge.sink] = [];
        }
        edgeMapUp[edge.sink].push(edge.source);
    });

    const edgeMapDown = {};
    graph.edges.forEach(edge => {
        if (!edgeMapDown[edge.source]){
            edgeMapDown[edge.source] = [];
        }
        edgeMapDown[edge.source].push(edge.sink);
    });

    const traceFlowUp = (node, hookFunction, terminalFunction, flow = [], result = { nodes: [], edges: [], flows: [] }, traversed = new Set()) => {
        if (traversed.has(node)) return;
        traversed.add(node);

        // assign a random id to the flow as metadata (helps in instrumentation)
        if (flow.length === 0){
            flow.id = crypto.randomBytes(4).toString('hex');
        }

        result.nodes.push(node);
        flow.push(node);

        if (hookFunction){
            hookFunction(node, flow);
        }

        if (terminalFunction instanceof Function && terminalFunction(node, flow)){
            result.flows.push(flow);
        }
        else if (!edgeMapUp[node.id]){
            result.flows.push(flow);
        }
        else {
            for (let sourceId of edgeMapUp[node.id]){
                let source = idMap[sourceId];

                traceFlowUp(source, hookFunction, terminalFunction, flow.slice(), result, traversed);
            }
        }
        return result;
    };

    const traceFlowDown = (node, hookFunction, terminalFunction, flow = [], result = { nodes: [], edges: [], flows: [] }, traversed = new Set()) => {
        if (traversed.has(node)) return;
        traversed.add(node);

        result.nodes.push(node);
        flow.push(node);

        if (hookFunction){
            hookFunction(node, flow);
        }

        if (terminalFunction instanceof Function && terminalFunction(node, flow)){
            result.flows.push(flow);
        }
        else if (!edgeMapDown[node.id]){
            result.flows.push(flow);
        }
        else {
            for (let sinkId of edgeMapDown[node.id]){
                let sink = idMap[sinkId];

                traceFlowDown(sink, hookFunction, terminalFunction, flow.slice(), result, traversed);
            }
        }
        return result;
    };

    return {
        traceUpstream: traceFlowUp,
        traceDownstream: traceFlowDown
    };
}

function explainGraph(graph){
    const cwd = process.cwd();
    
    const idMap = {};
    graph.nodes.forEach(node => {
        idMap[node.id] = node;
    });

    const edgeMap = {};
    graph.edges.forEach(edge => {
        if (!edgeMap[edge.sink]){
            edgeMap[edge.sink] = [];
        }
        edgeMap[edge.sink].push(edge.source);
    });

    let result = '';

    for (let id of Object.keys(idMap)){
        let node = idMap[id];
        // console.log(id, node);
        if (node.type.indexOf('Invocation') === 0 || node.type.indexOf('Dynamic') === 0) continue;

        let description = `${nodeDescription(node, cwd)} depends on:\n`;
        if (!edgeMap[id]){
            description += `    -> NOTHING\n`;
        }
        else {
            for (let sourceId of edgeMap[id]){
                let source = idMap[sourceId];
                // console.log(sourceId, source);
                description += `    -> ${nodeDescription(source, cwd)}\n`;
            }
        }
        // console.log(description);

        result += description + '\n';
    }

    return result;
}

async function interactiveExplainGraph(graph){
    const rl = readline.createInterface({ input: process.stdin, output: process.stdout });
    const ask = text => new Promise((resolve, reject) => rl.question(text, resolve));

    const cwd = process.cwd();
    
    const idMap = {};
    const indexMap = {};
    graph.nodes.forEach((node, index) => {
        idMap[node.id] = node;
        indexMap[index] = node;
    });

    const edgeMap = {};
    graph.edges.forEach(edge => {
        if (!edgeMap[edge.sink]){
            edgeMap[edge.sink] = [];
        }
        edgeMap[edge.sink].push(edge.source);
    });

    const traceFlow = (node) => {
        let description = '';
        if (!edgeMap[node.id]){
            description += `NOTHING ->\n`;
        }
        else {
            for (let sourceId of edgeMap[node.id]){
                let source = idMap[sourceId];
                // console.log(sourceId, source);
                description += `${nodeDescription(source, cwd)} ->\n`;
                

                traceFlow(source);
            }
        }
        description += `    Affects -> ${nodeDescription(node, cwd)}\n`;

        console.log(description);
    };

    const nodeList = graph.nodes.map((node, index) => `${index}.\t${nodeDescription(node, cwd)}`).join('\n');

    let answer = '';
    while (answer !== 'exit'){
        answer = await ask(nodeList + '\n\nChoose a node: ');
        if (indexMap[answer]){
            traceFlow(indexMap[answer]);
        }

        answer = await ask('Trace another flow? (y/n) ');
        if (answer !== 'y') break;
    }

    return;
}

const BUILTIN_NODEJS_OBJECT_NAMES = [
    'require',
    'module',
    '__dirname',
    '__filename',
    'constructor',
    'String',
    'Number',
    'Date',
    'Array',
    'Object',
    'Symbol',
    'Map',
    'Set',
    'Proxy',
    'Reflect'
];

function generateLineColumnClassName(node){
    let start = `s-${node.loc.start.line}-${node.loc.start.column}`;
    let end = node.loc.end ? `-e-${node.loc.end.line}-${node.loc.end.column}` : '';
    return start + end;
}

function generateHtmlNode(node, viewState){
    if (generateHtmlNode.Handlers[node.type]) {
        return generateHtmlNode.Handlers[node.type](node, viewState);
    }
    else {
        console.error(`WARN: Unsupported expression "${node.type}" in line ${node.loc.start.line}:${node.loc.start.column}`);
    }
    return escodegen.generate(node);
}

generateHtmlNode.Handlers = {
    Program: (node, viewState) => {
        return node.body.map((child, index, list) => {
            let lineBreaks = Array.from({ length: Math.max(0, child.loc.start.line - viewState.line) }).fill(`<br/>`).join('');
            viewState.line = child.loc.start.line;
            return lineBreaks + generateHtmlNode(child, viewState) + ';';
        }).join('');
    },
    Literal: (node) => {
        const escaped = typeof node.value === 'string' ? escodegen.generate(node).replace(/</g, '&lt;').replace(/>/g, '&gt;') : node.value;

        return `<span class="literal ${generateLineColumnClassName(node)}">${escaped}</span>`;
    },
    TemplateLiteral: (node, viewState) => {
        const escaped = escodegen.generate(node).replace(/</g, '&lt;').replace(/>/g, '&gt;');
        viewState.line = node.loc.end.line;
        return `<span class="literal ${generateLineColumnClassName(node)}">${escaped}</span>`;
    },
    Identifier: (node, viewState) => {
        if (node.entity){
            let classes = '';
            if (['IO:ProcessStdin', 'IO:FileContent', 'IO:FileReadStream', 'IO:ProcessOutput', 'IO:ProcessReadStream', 'IO:SocketContent', 'IO:HttpIncomingMessage', 'IO:Source'].includes(node.entity.node.type)){
                classes += ' io-source';
            }
            else if (['IO:FileWriteStream', 'IO:ProcessWriteStream', 'IO:HttpServerResponse'].includes(node.entity.node.type)){
                classes += ' io-sink';
            }
            else if (['IO:Socket'].includes(node.entity.node.type)){
                classes += ' io-duplex';
            }
            else if (BUILTIN_NODEJS_OBJECT_NAMES.includes(node.name)){
                classes += ' keyword-3';
            }
            else if (node.entity.inherits){
                if (IO_SOURCE_ENTITY_TYPES.includes(node.entity.inherits)){
                    classes += ' io-source';
                }
            }

            return `<span id="node-${node.entity.id}" class="${generateLineColumnClassName(node)}${classes}">${node.name}</span>`;
        }
        else {
            let classes = '';
            if (BUILTIN_NODEJS_OBJECT_NAMES.includes(node.name)){
                classes += ' keyword-3';
            }

            return `<span class="${generateLineColumnClassName(node)}${classes}">${node.name}</span>`;
        }
    },
    ExpressionStatement: (node, viewState) => generateHtmlNode(node.expression, viewState),
    SequenceExpression: (node, viewState) => {
        return node.expressions.map((child, index, list) => generateHtmlNode(child, viewState)).join('<span>, </span>');
    },
    VariableDeclaration: (node, viewState) => {
        const declarations = node.declarations.map(child => generateHtmlNode(child, viewState)).join(', ');

        return `<span><span class="name-declaration">${node.kind}</span> ${declarations}</span>`;
    },
    VariableDeclarator: (node, viewState) => {
        const init = node.init ? ` <span>=</span> ` + generateHtmlNode(node.init, viewState) : '';

        return `<span class="${generateLineColumnClassName(node)}">${generateHtmlNode(node.id, viewState)}${init}</span>`;
    },
    UpdateExpression: (node) => {
        return `<span>${escodegen.generate(node)}</span>`;
    },
    UnaryExpression: (node, viewState) => {
        let operator = node.operator;
        if (node.operator.length > 1) operator += ' ';
        
        return `<span><span class="keyword">${operator}</span>${generateHtmlNode(node.argument, viewState)}</span>`;
    },
    BinaryExpression: (node, viewState) => {
        
        const left = generateHtmlNode(node.left, viewState);
        const right = generateHtmlNode(node.right, viewState);

        return `<span>${left} <span class="keyword">${node.operator}</span> ${right}</span>`;
    },
    LogicalExpression: (node, viewState) => {
        
        const left = generateHtmlNode(node.left, viewState);
        const right = generateHtmlNode(node.right, viewState);

        return `<span>${left} <span class="keyword">${node.operator}</span> ${right}</span>`;
    },
    ConditionalExpression: (node, viewState) => {
        const test = generateHtmlNode(node.test, viewState);
        const consequent = generateHtmlNode(node.consequent, viewState);
        const alternate = generateHtmlNode(node.alternate, viewState);

        return `<span class="${generateLineColumnClassName(node)}">${test}<span> ? </span>${consequent}<span> : </span>${alternate}</span>`;
    },
    AssignmentExpression: (node, viewState) => {
        const left = generateHtmlNode(node.left, viewState);
        const right = generateHtmlNode(node.right, viewState);

        return `<span class="${generateLineColumnClassName(node)}">${left} <span>=</span> ${right}</span>`;
    },
    FunctionDeclaration: (node, viewState, type) => {

        const params = node.params.map(child => generateHtmlNode(child, viewState)).join(', ');

        let header = '';
        let async = node.async ? '<span class="keyword-4">async </span>' : '';
        if (type === 'FunctionExpression'){
            header = `${async}<span class="name-declaration">function</span> <span>(</span>${params}<span>)</span>`;
        }
        else if (type === 'ArrowFunctionExpression'){
            header = `${async}<span>(</span>${params}<span>)</span> <span>=></span>`;
        }
        else {
            header = `${async}<span class="name-declaration">function</span> <span>${generateHtmlNode(node.id, viewState)}</span> <span>(</span>${params}<span>)</span>`;
        }

        viewState.indent += 1;
        const body = generateHtmlNode(node.body, viewState);
        viewState.indent -= 1;

        return `<span class="${generateLineColumnClassName(node)}">${header} ${body}</span>`;
    },
    FunctionExpression: (node, viewState) => {
        return generateHtmlNode.Handlers.FunctionDeclaration(node, viewState, 'FunctionExpression');
    },
    ArrowFunctionExpression: (node, viewState) => {
        return generateHtmlNode.Handlers.FunctionDeclaration(node, viewState, 'ArrowFunctionExpression');
    },
    ReturnStatement: (node, viewState) => {
        let argument = '';
        if (node.argument){
            argument = ' ' + generateHtmlNode(node.argument, viewState);
        }

        return `<span class="${generateLineColumnClassName(node)}"><span class="keyword">return</span>${argument}</span>`;
    },
    CallExpression: (node, viewState) => {
        // console.log(`(${node.type}) at Line ${node.loc.start.line}: ${escodegen.generate(node)}`);
        const callee = generateHtmlNode(node.callee, viewState);

        const args = node.arguments.map((child, index, list) => generateHtmlNode(child, viewState)).join(', ');

        return `<span class="${generateLineColumnClassName(node)}">${callee}<span>(</span>${args}<span>)</span></span>`;
    },
    ObjectExpression: (node, viewState) => {
        const isInline = node.loc.start.line === node.loc.end.line;
        // viewState.indent += isInline ? 0 : 1;
        viewState.line = node.loc.start.line;
        const props = node.properties.map((child, index, list) => {
            let lineBreaks = Array.from({ length: child.loc.start.line - viewState.line }).fill(`<br/>`).join('');
            viewState.line = child.loc.start.line;
            return lineBreaks + generateHtmlNode(child, viewState)
        }).join(',');
        // viewState.indent -= isInline ? 0 : 1;
        viewState.line += isInline ? 0 : 1;

        return `<span class="${generateLineColumnClassName(node)}"><span>{ </span>${props}${isInline ? '' : '<br/>'}<span>}</span></span>`;
    },
    Property: (node, viewState) => {
        const prop = generateHtmlNode(node.value, viewState);

        return `<span class="${generateLineColumnClassName(node)}" style="margin-left: 2em;"><span>${escodegen.generate(node.key)}</span> <span>:</span> ${prop}</span>`;
    },
    ArrayExpression: (node, viewState) => {
        const elements = node.elements.map((child, index, list) => generateHtmlNode(child, viewState)).join(', ');

        return `<span class="${generateLineColumnClassName(node)}"><span>[</span> ${elements} <span>]</span></span>`;
    },
    MemberExpression: (node, viewState) => {
        let obj = generateHtmlNode(node.object, viewState);

        if (node.object.type === 'BinaryExpression'){
            obj = `<span>(</span>${obj}<span>)</span>`
        }

        let prop = generateHtmlNode(node.property, viewState);
        if (node.computed){
            prop = '<span>[</span>' + prop + '<span>]</span>'
        }
        else {
            prop = '<span>.</span>' + prop
        }

        let classes = '';
        if (node.entity && node.entity.node.type === 'IO:Writer'){
            classes += ' io-sink';
        }

        return `<span class="${generateLineColumnClassName(node)}${classes}">${obj}${prop}</span>`;
    },
    NewExpression: (node, viewState) => {
        // console.log(`(${node.type}) at Line ${node.loc.start.line}: ${escodegen.generate(node)}`);
        const callee = generateHtmlNode(node.callee, viewState);

        const args = node.arguments.map((child, index, list) => generateHtmlNode(child, viewState)).join(', ');

        return `<span class="${generateLineColumnClassName(node)}"><span class="keyword">new </span>${callee}<span>(</span>${args}<span>)</span></span>`;
    },
    ThisExpression: (node, viewState) => {
        return `<span class="keyword-2 ${generateLineColumnClassName(node)}">this</span>`;
    },
    Super: (node, viewState) => {
        return `<span class="keyword-2 ${generateLineColumnClassName(node)}">super</span>`
    },
    ClassDeclaration: (node, viewState, type) => {
        let header = '';
        if (type === 'ClassExpression'){
            header = `<span class="name-declaration">class</span>`;
        }
        else {
            header = `<span class="name-declaration">class</span> <span class="${generateLineColumnClassName(node)}">${generateHtmlNode(node.id, viewState)}</span>`;
        }

        // const id = generateHtmlNode(node.id, viewState);
        // const superclass = node.superclass ? ` <span class="keyword">extends</span> ` + generateHtmlNode(node.superclass, viewState) : '';
        if (node.superClass){
            header += ` <span class="keyword">extends</span> ${generateHtmlNode(node.superClass, viewState)}`;
        }

        viewState.line += 1;
        viewState.indent += 1;
        
        const body = node.body.body.map((child, index, list) => generateHtmlNode(child, viewState)).join('<br/>');
        
        viewState.indent -= 1;

        return `<div>${header} <span>{</span>${body}<span>}</span></div>`;
    },
    ClassExpression: (node, viewState) => {
        return generateHtmlNode.Handlers.ClassDeclaration(node, viewState, 'ClassExpression');
    },
    MethodDefinition: (node, viewState) => {
        let lineBreaks = Array.from({ length: node.loc.start.line - viewState.line }).fill(`<br/>`).join('');
        viewState.line = node.loc.start.line;

        const keyNode = generateHtmlNode(node.key);

        const params = node.value.params.map(child => generateHtmlNode(child, viewState)).join(', ');

        const body = generateHtmlNode(node.value.body, viewState);

        viewState.line = node.loc.end.line;

        return `<div style="margin-left: ${viewState.indent * 2}em;"><span class="${generateLineColumnClassName(node)}">${keyNode}</span> <span>(</span>${params}<span>)</span>${body}</div>`;
    },
    IfStatement: (node, viewState) => {
        const test = generateHtmlNode(node.test, viewState);
        const consequent = generateHtmlNode(node.consequent, viewState);
        let alternate = '';

        if (node.alternate){
            viewState.line += 1;
            alternate = `<br/><span class="keyword">else</span> ` + generateHtmlNode(node.alternate, viewState);
        }

        return `<span class="IfStatement"><span class="keyword">if</span> <span>(</span>${test}<span>)</span> ${consequent}${alternate}</span>`;
    },
    SwitchStatement: (node, viewState) => {

        const discriminant = generateHtmlNode(node.discriminant, viewState);

        viewState.line = node.discriminant.loc.end.line;

        const cases = node.cases.map((child, index, list) => generateHtmlNode(child, viewState)).join('');

        return `<div><span class="keyword">switch</span><span> (</span>${discriminant}<span>)</span><span>{</span><div style="margin-left: 2em;">${cases}</div><span>}</span></div>`;
    },
    SwitchCase: (node, viewState) => {
        let lineBreaks = Array.from({ length: node.loc.start.line - viewState.line }).fill(`<br/>`).join('');
        viewState.line = node.loc.start.line;

        const test = node.test ? '<span class="keyword">case</span> ' + generateHtmlNode(node.test, viewState) : '<span class="keyword">default</span>';
        
        const consequent = node.consequent.map((child, index, list) => (generateHtmlNode(child, viewState) + ';')).join('');
        
        return `<span>${test}<span>:</span><div style="margin-left: 2em;">${consequent}</div></span>`;
    },
    BlockStatement: (node, viewState) => {
        viewState.line = node.loc.start.line + 1;
        const body = node.body.map((child, index, list) => {
            let lineBreaks = Array.from({ length: child.loc.start.line - viewState.line }).fill(`<br/>`).join('');
            viewState.line = child.loc.start.line;
            return lineBreaks + generateHtmlNode(child, viewState) + ';'
        }).join('');
        return `<span><span>{</span><br/><div style="margin-left: ${viewState.indent * 2}em;">${body}</div><span style="margin-left: ${viewState.indent * 2 - 2}em;">}</span></span>`;
    },
    ForStatement: (node, viewState) => {
        const init = node.init ? generateHtmlNode(node.init, viewState) : '';
        const test = node.test ? generateHtmlNode(node.test, viewState) : '';
        const update = node.update ? generateHtmlNode(node.update, viewState) : '';
        const body = generateHtmlNode(node.body, viewState);
        return `<div><span class="keyword">for </span><span>(</span>${init}<span>; </span>${test}<span>; </span>${update}<span>)</span>${body}</div>`;
    },
    ForOfStatement: (node, viewState) => {
        const left = generateHtmlNode(node.left, viewState);
        const right = generateHtmlNode(node.right, viewState);
        const body = generateHtmlNode(node.body, viewState);
        return `<div><span class="keyword">for </span><span>(</span>${left} <span class="keyword">of</span> ${right}<span>)</span>${body}</div>`;
    },
    ForInStatement: (node, viewState) => {
        const left = generateHtmlNode(node.left, viewState);
        const right = generateHtmlNode(node.right, viewState);
        const body = generateHtmlNode(node.body, viewState);
        return `<div><span class="keyword">for </span><span>(</span>${left} <span class="keyword">in</span> ${right}<span>)</span>${body}</div>`;
    },
    WhileStatement: (node, viewState) => {
        const test = generateHtmlNode(node.test, viewState);
        const body = generateHtmlNode(node.body, viewState);

        return `<span><span class="keyword">while</span> <span>(</span>${test}<span>)</span> ${body}</span>`;
    },
    TryStatement: (node, viewState) => {
        const block = generateHtmlNode(node.block, viewState);
        const handler = node.handler ? generateHtmlNode(node.handler, viewState) : '';
        const finalizer = node.finalizer ? '<span class="keyword">finally </span>' + generateHtmlNode(node.finalizer, viewState) : '';
        return `<div><span class="keyword">try </span>${block}${handler}${finalizer}</div>`;
    },
    CatchClause: (node, viewState) => {
        const param = node.param ? `<span>(</span>${generateHtmlNode(node.param)}<span>)</span> ` : ''
        return `<div><span class="keyword">catch</span> ${param}${generateHtmlNode(node.body, viewState)}</div>`;
    },
    SpreadElement: (node, viewState) => {
        return `<span><span>...</span>${generateHtmlNode(node.argument, viewState)}</span>`;
    },
    RestElement: (node, viewState) => {
        return `<span><span>...</span>${generateHtmlNode(node.argument, viewState)}</span>`;
    },
    AssignmentPattern: (node, viewState) => {
        return `<span>${generateHtmlNode(node.left, viewState)}<span> = </span>${generateHtmlNode(node.right, viewState)}</span>`;
    },
    ArrayPattern: (node, viewState) => {
        const elems = node.elements.map((child, index, list) => (child !== null ? generateHtmlNode(child, viewState) : '<span> </span>')).join(', ');
        return `<span><span>[ </span>${elems}<span> ]</span></span>`;
    },
    ObjectPattern: (node, viewState) => {
        const props = node.properties.map((child, index, list) => generateHtmlNode(child, viewState)).join(', ');
        return `<span><span>{ </span>${props}<span> }</span></span>`;
    },
    ThrowStatement: (node, viewState) => {
        return `<span><span class="keyword-2">throw </span>${generateHtmlNode(node.argument, viewState)}</span>`;
    },
    AwaitExpression: (node, viewState) => {
        let argument = generateHtmlNode(node.argument, viewState);
        return `<span class="${generateLineColumnClassName(node)}"><span class="keyword-4">await </span>${argument}</span>`;
    },
    EmptyStatement: (node) => ``,
    BreakStatement: (node) => `<span class="keyword">${escodegen.generate(node)}</span>`,
    ContinueStatement: (node) => `<span class="keyword">${escodegen.generate(node)}</span>`,
}

// tree, graph must be the original output graph of analyze
function generateHtmlViewer(analysis){
    const { tree, graph, stats } = analysis;

    let body = generateHtmlNode(tree, { line: 0, indent: 0 });

    let cwd = process.cwd();
    let pages = {};
    pages[crypto.randomBytes(4).toString('hex')] = { path: path.relative(cwd, tree.absSourcePath), absPath: tree.absSourcePath, html: body };
    tree.scope.loadedModules.forEach(moduleTree => {
        pages[crypto.randomBytes(4).toString('hex')] = { path: path.relative(cwd, moduleTree.absSourcePath), absPath: moduleTree.absSourcePath, html: generateHtmlNode(moduleTree, { line: 0, indent: 0 }) };
    });

    let flowHtml = stats.flowsUpstream.length > 0 ? '<ul>' + stats.flowsUpstream.map(flow => {
        const sourceEntity = flow[flow.length - 1];
        const sinkEntity = flow[0];
        const source = `<span id="node-${sourceEntity.id}" nodeid="${sourceEntity.id}" linecolumn="${generateLineColumnClassName(sourceEntity.node)}" class="node-btn">${sourceEntity.node.type} in Line ${sourceEntity.node.loc.start.line}</span>`;
        const sink = `<span id="node-${sinkEntity.id}" nodeid="${sinkEntity.id}" linecolumn="${generateLineColumnClassName(sinkEntity.node)}" class="node-btn">${sinkEntity.node.type} in Line ${sinkEntity.node.loc.start.line}</span>`;
        return `<li><div>${source} to ${sink}</div></li>`;
    }).join('') + '</ul>' : '<div>No IO to IO flow found</div>';

    let html = `<!DOCTYPE html>
<html lang="en">
  <head>
    <meta charset="utf-8">
    <title>Dataflow Viewer</title>
    <style>
body {
    margin: 0;
    padding: 0;
}
#viewport {
    display: flex;
    align-items: stretch;
    height: 100vh;
}
#code-view {
    flex: 3;
    display: flex;
    flex-direction: column;
}
#source-viewer {
    padding: 1em;
    font-family: 'Consolas', monospace;
    background: #444;
    color: white;
    white-space: pre-wrap;
    position: relative;
    flex: 1;
}
#source-overlay {
    position: absolute;
    pointer-events: none;
    top: 0;
    left: 0;
    width: 100%;
    height: 100%;
}
#tabs {
    display: flex;
}
#tabs>a {
    padding: 0.5em;
    flex-grow: 1;
    cursor: pointer;
    font-family: 'Consolas', monospace;
    background: #aaa;
}
#tabs>a.is-active {
    border-top: 1px solid grey;
    border-left: 1px solid grey;
    border-right: 1px solid grey;
    background: #eee;
}
#flow-view {
    flex: 1;
    font-family: 'Consolas', monospace;
    background: #ccc;
    position: relative;
}
#flow-view>div {
    position: fixed;
    width: 25vw;
    height: 100%;
}
#flow-list {
    padding: 1em;
    height: 35%;
    overflow: auto;
}
#trace-viewer {
    padding: 1em;
    height: 45%;
    overflow: auto;
}
#trace-viewer .source-node-container {
    padding-left: 1em;
}
#context-menu {
    position: fixed;
    z-index: 10;
    background: white;
    font-family: 'Consolas', monospace;
    box-shadow: 0 0 5px #444;
}
#context-menu>div {
    padding: 0.5em;
    cursor: pointer;
}
#context-menu>div:hover {
    background: CornflowerBlue;
    color: white;
}

.heading {
    margin: 0;
    padding: 0.5em;
    background: #eee;
}

.keyword {
    color: violet;
}
.keyword-2 {
    color: tomato;
}
.keyword-3 {
    color: LightSkyBlue;
}
.keyword-4 {
    color: RoyalBlue;
}
.name-declaration {
    color: cyan;
}
.builtin {
    color: cyan;
    font-style: italic;
}
.clickable {
    cursor: pointer;
}
.clickable:hover {
    cursor: pointer;
    border: 1px solid yellow;
}
.highlight {
    border: 1px solid red;
}
.highlight.emphasize {
    border: 2px solid pink;
}
.literal {
    color: orange;
}
.sink-node,
.source-node {
    cursor: pointer;
}
.sink-node.emphasize,
.source-node.emphasize {
    background: #eee;
}
.io-source {
    color: lime;
    font-weight: bold;
    text-decoration: underline;
}
.io-sink {
    color: yellow;
    font-weight: bold;
    text-decoration: underline;
}
.io-duplex {
    color: gold;
    font-weight: bold;
    text-decoration: underline;
}
    </style>
  </head>
  <body>
    <div id="viewport">
        <div id="code-view">
            <div id="tabs">
                ${Object.keys(pages).map(key => `<a id="btn-${key}">${pages[key].path}</a>`).join('')}
            </div>
            <div id="source-viewer">${Object.keys(pages).map(key => `<div id="page-${key}" class="page">${pages[key].html}</div>`).join('')}<svg id="source-overlay"></svg></div>
        </div>
        <div id="flow-view">
            <div>
                <h4 class="heading">IO-to-IO Flows</h4>
                <div id="flow-list">
                    ${flowHtml}
                </div>
                <h4 class="heading">Trace</h4>
                <div id="trace-viewer"></div>
            </div>
        </div>
        <div id="context-menu"></div>
    </div>
    <script>
const graph = ${JSON.stringify(graph)};
const cwd = '${JSON.stringify(process.cwd())}';
const pages = ${JSON.stringify(Object.keys(pages).reduce((acc, key) => {
    acc[key] = {
        absPath: pages[key].absPath,
        path: pages[key].path
    };
    return acc;
}, {}))};
let pageShown = null;
let activeBtn = null;
let nodeSelected = null;
    
const idMap = {};
graph.nodes.forEach((node, index) => {
    idMap[node.id] = node;
});

const edgeMap = {};
graph.edges.forEach(edge => {
    if (!edgeMap[edge.sink]){
        edgeMap[edge.sink] = [];
    }
    edgeMap[edge.sink].push(edge.source);
});

const viewport = document.getElementById('viewport');
const sourceViewer = document.getElementById('source-viewer');
const sourceOverlay = document.getElementById('source-overlay');
const flowList = document.getElementById('flow-list');
const traceViewer = document.getElementById('trace-viewer');
const contextMenu = document.getElementById('context-menu');

function emptyDOM (elem){
    while (elem.firstChild) elem.removeChild(elem.firstChild);
}

function createDOM (htmlString){
    let template = document.createElement('template');
    template.innerHTML = htmlString.trim();
    return template.content.firstChild;
}

function showContextMenu(x, y, items){
    emptyDOM(contextMenu);
    items.forEach(item => contextMenu.appendChild(item));
    contextMenu.style.left = x + 'px';
    contextMenu.style.top = y + 'px';
    contextMenu.style.display = '';
}

function hideContextMenu(){
    contextMenu.style.display = 'none';
}

viewport.addEventListener('click', () => hideContextMenu(), true);

Object.keys(pages).forEach((key, index) => {
    const btn = document.querySelector('#btn-' + key);
    const page = document.querySelector('#page-' + key);
    page._key = key;
    page._absPath = pages[key].absPath;

    if (index > 0){
        page.style.display = 'none';
    }
    else {
        pageShown = page;
        activeBtn = btn;
        btn.classList.add('is-active');
    }
    btn.addEventListener('click', evt => {
        pageShown.style.display = 'none';
        activeBtn.classList.remove('is-active');
        page.style.display = '';
        pageShown = page;
        activeBtn = btn;
        btn.classList.add('is-active');

        sourceOverlay.innerHTML = '';
        if (nodeSelected){
            const flow = traceFlow(nodeSelected);
            traceViewer.innerHTML = '<div><span>'+nodeSelected.id+'</span><br/>' + nodeSelected.type + ' ' + nodeSelected.name + '</div><br/>' + flow;
            traceViewer.querySelectorAll('.sink-node,.source-node').forEach(elem => {
                elem.addEventListener('mouseenter', evt => {
                    const code = sourceViewer.querySelector('.' + evt.target.attributes['linecolumn'].value);
                    if (code) code.classList.add('emphasize');
                    elem.classList.add('emphasize');
                });
                elem.addEventListener('mouseleave', evt => {
                    const code = sourceViewer.querySelector('.' + evt.target.attributes['linecolumn'].value);
                    if (code) code.classList.remove('emphasize');
                    elem.classList.remove('emphasize');
                });
                elem.addEventListener('click', evt => {
                    let childContainer = elem.parentElement.children[1];
                    if (childContainer){
                        if (childContainer.style.display === 'none'){
                            childContainer.style.display = '';
                        }
                        else {
                            childContainer.style.display = 'none';
                        }
                    }
                });
            });
        }
    });
})

graph.nodes.forEach(node => {
    const elems = document.querySelectorAll('.' + generateLineColumnClassName(node));
    elems.forEach(elem => {
        if (!elem.nodes) elem.nodes = [];
        const menuItem = createDOM('<div>' + node.type + ' ' + node.name + '</div>');
        const selectNode = () => {
            flowShown.forEach(elem => elem.classList.remove('highlight'));
            flowShown = [];
            sourceOverlay.innerHTML = '';
            nodeSelected = node;
            const flow = traceFlow(node);
            traceViewer.innerHTML = '<div><span>'+nodeSelected.id+'</span><br/>' + nodeSelected.type + ' ' + nodeSelected.name + '</div><br/>' + flow;
            traceViewer.querySelectorAll('.sink-node,.source-node').forEach(elem => {
                elem.addEventListener('mouseenter', evt => {
                    const code = sourceViewer.querySelector('.' + evt.target.attributes['linecolumn'].value);
                    if (code) code.classList.add('emphasize');
                    elem.classList.add('emphasize');
                });
                elem.addEventListener('mouseleave', evt => {
                    const code = sourceViewer.querySelector('.' + evt.target.attributes['linecolumn'].value);
                    if (code) code.classList.remove('emphasize');
                    elem.classList.remove('emphasize');
                });
                elem.addEventListener('click', evt => {
                    let childContainer = elem.parentElement.children[1];
                    if (childContainer){
                        if (childContainer.style.display === 'none'){
                            childContainer.style.display = '';
                        }
                        else {
                            childContainer.style.display = 'none';
                        }
                    }
                });
            });
        }
        menuItem.addEventListener('click', evt => {
            evt.stopPropagation();
            selectNode();
        });

        elem.nodes.push(menuItem);

        elem.addEventListener('click', evt => {
            evt.stopPropagation();

            if (elem.nodes.length > 1){
                showContextMenu(evt.clientX, evt.clientY, elem.nodes);
            }
            else {
                selectNode();
            }
        });

        elem.classList.add('clickable');
    })
});

flowList.querySelectorAll('.node-btn').forEach(elem => {
    elem.classList.add('clickable');

    const nodeId = elem.attributes['nodeid'].value;
    const linecolumn = elem.attributes['linecolumn'].value;

    elem.addEventListener('mouseenter', evt => {
        sourceViewer.querySelectorAll('.' + linecolumn).forEach(target => {
            target.classList.add('highlight');  
        });
    });

    elem.addEventListener('mouseleave', evt => {
        sourceViewer.querySelectorAll('.' + linecolumn).forEach(target => {
            target.classList.remove('highlight');
        });
    });

    elem.addEventListener('click', evt => {
        flowShown.forEach(elem => elem.classList.remove('highlight'));
        flowShown = [];
        sourceOverlay.innerHTML = '';
        nodeSelected = idMap[nodeId];
        const flow = traceFlow(nodeSelected);
        traceViewer.innerHTML = '<div><span>'+nodeSelected.id+'</span><br/>' + nodeSelected.type + ' ' + nodeSelected.name + '</div><br/>' + flow;
        traceViewer.querySelectorAll('.sink-node,.source-node').forEach(elem => {
            elem.addEventListener('mouseenter', evt => {
                const code = sourceViewer.querySelector('.' + evt.target.attributes['linecolumn'].value);
                if (code) code.classList.add('emphasize');
                elem.classList.add('emphasize');
            });
            elem.addEventListener('mouseleave', evt => {
                const code = sourceViewer.querySelector('.' + evt.target.attributes['linecolumn'].value);
                if (code) code.classList.remove('emphasize');
                elem.classList.remove('emphasize');
            });
            elem.addEventListener('click', evt => {
                let childContainer = elem.parentElement.children[1];
                if (childContainer){
                    if (childContainer.style.display === 'none'){
                        childContainer.style.display = '';
                    }
                    else {
                        childContainer.style.display = 'none';
                    }
                }
            });
        });
    });

});

function generateLineColumnClassName(node){
    let start = \`s-\${node.loc.start.line}-\${node.loc.start.column}\`;
    let end = node.loc.end ? \`-e-\${node.loc.end.line}-\${node.loc.end.column}\` : '';
    return start + end;
}

function nodeDescription(node, cwd){
    if (['Identifier', 'FunctionDeclaration', 'Property', 'SinkFunctionEnter', 'FunctionEnter', 'FunctionReturn', 'UnknownObject', 'IO:ProcessStdin', 'IO:FileContent', 'IO:SocketContent'].includes(node.type)){
        return \`\${node.type} "\${node.name}" in line \${node.loc.start.line}, column \${node.loc.start.column} in \${node.file.replace(cwd, '')} (\${node.id})\`;
    }
    else {
        return \`\${node.type} in line \${node.loc.start.line}, column \${node.loc.start.column} in \${node.file.replace(cwd, '')} (\${node.id})\`;
    }
}

let flowShown = [];

function traceFlow(node, downstream, traversed = new Set()){
    if (traversed.has(node)) return;
    traversed.add(node);

    const elem = document.querySelector('.' + generateLineColumnClassName(node));
    if (elem){
        elem.classList.add('highlight');
        flowShown.push(elem);
    }

    let description;
    if (!downstream){
        description = '<div><div class="sink-node" linecolumn="' + generateLineColumnClassName(node) + '">' + nodeDescription(node, cwd) + ' <span>depends on:</span></div>';
    }
    else {
        description = '<div>';
    }

    if (!edgeMap[node.id]){
        description += '<div class="source-node-container"><div class="source-node" linecolumn="none"><span>-></span> NOTHING</div></div>';
    }
    else {
        for (let sourceId of edgeMap[node.id]){
            const source = idMap[sourceId];
            const sourceElem = document.querySelector('.' + generateLineColumnClassName(source));

            if (elem && sourceElem && node.file === pageShown._absPath && source.file === pageShown._absPath){
                const sinkBox = elem.getBoundingClientRect();
                sinkBox.cx = (sinkBox.left + sinkBox.right) / 2 - sourceViewer.offsetLeft;
                //sinkBox.cy = (sinkBox.top + sinkBox.bottom) / 2 - sourceViewer.offsetTop + document.scrollingElement.scrollTop;
                sinkBox.cy = sinkBox.top - sourceViewer.offsetTop + document.scrollingElement.scrollTop + 5;
                const sourceBox = sourceElem.getBoundingClientRect();
                sourceBox.cx = (sourceBox.left + sourceBox.right) / 2 - sourceViewer.offsetLeft;
                // sourceBox.cy = (sourceBox.top + sourceBox.bottom) / 2 - sourceViewer.offsetTop + document.scrollingElement.scrollTop;
                sourceBox.cy = sourceBox.top - sourceViewer.offsetTop + document.scrollingElement.scrollTop + 5;
                drawArrow(sourceOverlay, sinkBox.cx, sinkBox.cy, sourceBox.cx, sourceBox.cy, 'red', 1, 10);
            }

            description += '<div class="source-node-container"><div class="source-node" linecolumn="' + generateLineColumnClassName(source) + '"><span>-></span> ' + nodeDescription(source, cwd) + '</div>';
            description += traceFlow(source, node, traversed) + '</div>';
        }
    }
    description += '</div>';

    return description;
};

function drawArrow(svgElement, x1, y1, x2, y2, color, strokeWidth, arrowSize) {
    var line = document.createElementNS("http://www.w3.org/2000/svg", "line");
    line.setAttribute("x1", x1);
    line.setAttribute("y1", y1);
    line.setAttribute("x2", x2);
    line.setAttribute("y2", y2);
    line.setAttribute("stroke", color);
    line.setAttribute("stroke-width", strokeWidth);
    svgElement.appendChild(line);

    // Calculate arrow angle
    var angle = Math.atan2(y2 - y1, x2 - x1);

    // Calculate arrow points
    var x3 = x2 - arrowSize * Math.cos(angle - Math.PI / 6);
    var y3 = y2 - arrowSize * Math.sin(angle - Math.PI / 6);
    var x4 = x2 - arrowSize * Math.cos(angle + Math.PI / 6);
    var y4 = y2 - arrowSize * Math.sin(angle + Math.PI / 6);

    // Draw arrowhead
    var arrow = document.createElementNS("http://www.w3.org/2000/svg", "polygon");
    arrow.setAttribute("points", x2 + "," + y2 + " " + x3 + "," + y3 + " " + x4 + "," + y4);
    arrow.setAttribute("fill", color);
    svgElement.appendChild(arrow);
}
    </script>
  </body>
</html>`;

    return html;
}

module.exports = {
	analyze,
    explainGraph,
    interactiveExplainGraph,
    generateHtmlViewer,
    FlowTracer,
    EntityTypes: {
        SemanticEntity,
        BuiltInEntity,
        ExternalEntity,
        LiteralEntity,
        ConditionalEntity
    }
}