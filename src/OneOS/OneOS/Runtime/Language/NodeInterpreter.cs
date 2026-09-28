using System;
using System.Collections.Generic;
using OneOS.Runtime.Language.Ast;
using OneOS.Runtime.Language.Models;

namespace OneOS.Runtime.Language;

public class NodeInterpreter
{
    private readonly NodeParser _parser;
    private NodeFileAst? _ast;
    
    // Environment for variables
    private readonly Dictionary<string, double> _variables = new();

    public NodeInterpreter()
    {
        _parser = new NodeParser();
    }

    public void LoadNode(string content)
    {
        _ast = _parser.Parse(content);
        _variables.Clear();
    }

    public void Evaluate()
    {
        if (_ast == null) throw new InvalidOperationException("Node not loaded.");

        foreach (var stmt in _ast.Statements)
        {
            ExecuteStatement(stmt);
        }
    }

    private void ExecuteStatement(ScriptStatement stmt)
    {
        if (stmt is LetStatement letStmt)
        {
            double val = EvaluateExpression(letStmt.Value);
            _variables[letStmt.Identifier] = val;
        }
        else if (stmt is AssignStatement assignStmt)
        {
            if (!_variables.ContainsKey(assignStmt.Identifier))
                throw new InvalidOperationException($"Variable '{assignStmt.Identifier}' is not defined.");
            
            double val = EvaluateExpression(assignStmt.Value);
            _variables[assignStmt.Identifier] = val;
        }
        else if (stmt is PrintStatement printStmt)
        {
            if (printStmt.Value is StringLiteralExpression strLit)
            {
                Console.WriteLine(strLit.Value);
            }
            else
            {
                double val = EvaluateExpression(printStmt.Value);
                Console.WriteLine(val);
            }
        }
    }

    private double EvaluateExpression(ScriptExpression expr)
    {
        if (expr is LiteralExpression lit)
        {
            return lit.Value;
        }
        else if (expr is VariableExpression varExpr)
        {
            if (_variables.TryGetValue(varExpr.Identifier, out double val))
                return val;
            throw new InvalidOperationException($"Variable '{varExpr.Identifier}' is not defined.");
        }
        else if (expr is BinaryExpression binExpr)
        {
            double left = EvaluateExpression(binExpr.Left);
            double right = EvaluateExpression(binExpr.Right);
            
            return binExpr.Operator switch
            {
                "+" => left + right,
                "-" => left - right,
                "*" => left * right,
                "/" => left / right,
                _ => throw new InvalidOperationException($"Operator '{binExpr.Operator}' not supported.")
            };
        }
        
        throw new NotSupportedException($"Expression of type {expr.GetType().Name} not supported for evaluation.");
    }
}
