// SqlExpressionEvaluator.cs

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SpiritSync.Generators.Parser;
using SpiritSync.Generators.Supplemental;

// ReSharper disable ConvertIfStatementToSwitchStatement

namespace SpiritSync.Generators;

/// <summary>
/// Statically evaluates Roslyn expression syntax nodes to their string values for use in SQL constant generation.
/// </summary>
internal static class SqlExpressionEvaluator
{
    /// <summary>
    /// Attempts to evaluate <paramref name="expr"/> to a constant string value by walking the syntax tree
    /// and resolving literals, identifiers, interpolated strings, method calls, and more.
    /// </summary>
    internal static bool TryEvaluateSqlExpression(
        in SourceProductionContext context,
        ExpressionSyntax expr,
        SemanticModel model,
        [NotNullWhen(true)] out string? value,
        CancellationToken token,
        int currentIndent,
        Dictionary<string, string>? parameterMap = null)
    {
        value = null;

        // Before evaluating sub-expressions:
        if (expr.SyntaxTree != model.SyntaxTree)
        {
            model = model.Compilation.GetSemanticModel(expr.SyntaxTree);
        }

        // Needed to expand inner ternaries and the like in interpolated strings, e.g. $"{(isNum ? "NUMBER" : "VARCHAR(100)")}".
        if (expr is ParenthesizedExpressionSyntax parens)
        {
            if (!TryEvaluateSqlExpression(context, parens.Expression, model, out var parensValue, token, currentIndent, parameterMap))
                return false;
            value = parensValue;
            return true;
        }

        // 't' + "rue", firstName + lastName, etc.
        if (expr is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.AddExpression))
        {
            var left = TryEvaluateSqlExpression(context, b.Left, model, out var leftValue, token, currentIndent, parameterMap) ? leftValue : null;
            var right = TryEvaluateSqlExpression(context, b.Right, model, out var rightValue, token, currentIndent, parameterMap) ? rightValue : null;
            value = left is null || right is null ? null : left + right;
            return value is not null;
        }

        // Ternary operators
        if (expr is ConditionalExpressionSyntax cond)
        {
            if (!TryEvaluateCondition(context, cond.Condition, model, out var condBool, token, parameterMap))
                return false;

            var chosen = condBool ? cond.WhenTrue : cond.WhenFalse;
            if (!TryEvaluateSqlExpression(context, chosen, model, out var condEvalVal, token, currentIndent, parameterMap))
                return false;

            value = condEvalVal;
            return true;
        }

        // Pattern-matching expressions, e.g. `x is not null`, `x is null`, `x is { Length: > 0 }`.
        if (expr is IsPatternExpressionSyntax isPattern)
        {
            if (!TryEvaluateIsPattern(context, isPattern, model, out var patternBool, token, parameterMap))
                return false;

            value = patternBool ? "true" : "false";
            return true;
        }

        // Cast operators, e.g. {(int)MyEnum.MyValue}.
        if (expr is CastExpressionSyntax castExpr)
        {
            // Evaluate the inner expression first
            if (!TryEvaluateSqlExpression(context, castExpr.Expression, model, out var innerValue, token, currentIndent, parameterMap))
                innerValue = castExpr.Expression.ToFullString();

            // Ask Roslyn for the type info
            var castType = model.GetTypeInfo(castExpr.Type, token).Type;
            var sourceType = model.GetTypeInfo(castExpr.Expression, token).Type;

            // Try to get Roslyn's constant-evaluated value (if known)
            var constValue = model.GetConstantValue(castExpr, token);
            if (constValue.HasValue)
                value = FormatConstant(constValue.Value!);
            else
            {
                // Fallback: try runtime conversion if both sides are known
                object? valueObj;
                try
                {
                    // Try to interpret literal text as a value of the source type
                    if (sourceType != null && castType != null && GetSystemType(castType) is { } sysType)
                    {
                        // Convert.ChangeType works for primitive casts (string, int, double, bool, etc.)
                        valueObj = Convert.ChangeType(innerValue, sysType);
                    }
                    else
                    {
                        // If semantic info isn't complete, just return the string
                        valueObj = innerValue;
                    }
                }
                catch
                {
                    // Conversion failed; just keep the text
                    valueObj = innerValue;
                }

                value = valueObj?.ToString() ?? "null";
            }
            return true;
        }

        // Literals:
        // true, false
        // 0, -1, 3.14
        // 'a', "hello"
        // default
        if (expr is LiteralExpressionSyntax literal)
        {
            if (literal.IsKind(SyntaxKind.StringLiteralExpression) && literal.Token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken))
            {
                value = SqlText.PreserveRelativeIndent(literal.Token.ValueText);
            }
            else
            {
                value = literal.Token.ValueText;
            }
            return true;
        }

        // default(int), which would evaluate to 0, thus assigning "0" to `value`.
        // default(T), etc.
        if (expr is DefaultExpressionSyntax @default)
        {
            var constant = model.GetConstantValue(@default);
            if (constant.HasValue)
            {
                value = constant.Value!.ToString();
                return true;
            }
            return false;
        }

        // Local variable reference or parameter reference OR Static property or field references.
        if (expr is IdentifierNameSyntax or MemberAccessExpressionSyntax)
        {
            // Check for a local variable or parameter.
            if (parameterMap is not null)
            {
                // If evaluating inside a helper body and this identifier matches a parameter,
                // substitute the argument value.
                if (expr is IdentifierNameSyntax idSyntax && parameterMap.TryGetValue(idSyntax.Identifier.Text, out var replacement))
                {
                    value = replacement;
                    return true;
                }
            }

            // Evaluate compile-time constants if possible
            var constantValue = model.GetConstantValue(expr, token);
            if (constantValue.HasValue)
            {
                value = constantValue.Value switch
                {
                    string s => s,
                    char c => c.ToString(),
                    _ => constantValue.Value?.ToString() ?? string.Empty
                };
                return true;
            }

            // Check for static properties or fields.

            var symbolInfo = model.GetSymbolInfo(expr, token).Symbol;
            if (symbolInfo is IPropertySymbol { IsStatic: true } prop)
            {
                // Retrieve syntax where property is declared
                if (prop.DeclaringSyntaxReferences.FirstOrDefault() is { } declRef &&
                    declRef.GetSyntax(token) is PropertyDeclarationSyntax { Initializer.Value: { } initExpr } propDecl)
                    // static string Foo { get; } = <initializer>;
                {
                    var propModel = model.Compilation.GetSemanticModel(propDecl.SyntaxTree);
                    if (TryEvaluateSqlExpression(context, initExpr, propModel, out var val, token, currentIndent, parameterMap))
                    {
                        value = val;
                        return true;
                    }
                }
            }
            else if (symbolInfo is IFieldSymbol { IsStatic: true } field)
            {
                if (field.DeclaringSyntaxReferences.FirstOrDefault() is { } declRef &&
                    declRef.GetSyntax(token) is VariableDeclaratorSyntax { Initializer.Value: { } initExpr } vDecl)
                {
                    var fieldModel = model.Compilation.GetSemanticModel(vDecl.SyntaxTree);
                    if (TryEvaluateSqlExpression(context, initExpr, fieldModel, out var val, token, currentIndent, parameterMap))
                    {
                        value = val;
                        return true;
                    }
                }
            }
        }

        // Constants on classes, e.g. `PagingOptions.DefaultPageSize` or something.
        if (expr is MemberAccessExpressionSyntax)
        {
            // Evaluate compile-time constants if possible
            var constantValue = model.GetConstantValue(expr, token);
            if (constantValue.HasValue)
            {
                value = constantValue.Value switch
                {
                    string s => s,
                    char c => c.ToString(),
                    _ => constantValue.Value?.ToString() ?? string.Empty
                };
                return true;
            }
        }

        // Syntax-generated string constants from nameof(...), e.g. `nameof(firstName)`.
        if (expr is InvocationExpressionSyntax
            {
                Expression: IdentifierNameSyntax
                {
                    Identifier.Text: "nameof"
                }
            } nameOf &&
            nameOf.ArgumentList.Arguments.FirstOrDefault()?.Expression is { } nameOfTarget)
        {
            // nameof(X) → extract the symbol's name text
            if (nameOfTarget is IdentifierNameSyntax nameId)
            {
                value = nameId.Identifier.Text;
                return true;
            }

            // fallback: try semantic model
            var symbol = model.GetSymbolInfo(nameOfTarget, token).Symbol;
            if (symbol is null) return false;
            value = symbol.Name;
            return true;
        }

        // Handle .ToString() calls on a resolvable expression, e.g. `type.ToString()` where `type` is a char parameter.
        if (expr is InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax
                {
                    Name: IdentifierNameSyntax { Identifier.Text: "ToString" }
                } toStringAccess,
                ArgumentList.Arguments.Count: 0
            })
        {
            if (TryEvaluateSqlExpression(context, toStringAccess.Expression, model, out var receiverValue, token, currentIndent, parameterMap))
            {
                value = receiverValue;
                return true;
            }
        }

        // Some other method call.
        // We only care about processing static methods (or maybe local functions within those static methods?) that return a value and are not async.
        // TODO: Implement support for local functions. As it stands, this logic wouldn't populate their parameters / closure-provided variables correctly.
        //       The use case is infrequent enough to be postponed for now though.
        if (expr is InvocationExpressionSyntax inv)
        {
            if (model.GetSymbolInfo(inv, token).Symbol is not IMethodSymbol symbol
                || (!symbol.IsStatic &&
                    symbol.MethodKind != MethodKind.LocalFunction &&
                    symbol is { ReturnsVoid: false, IsAsync: false }))
                return false;

            if (symbol.DeclaringSyntaxReferences.FirstOrDefault() is not { } declRef
                || declRef.GetSyntax(token) is not MethodDeclarationSyntax methodDecl)
                return false;

            var tree = methodDecl.SyntaxTree;
            var newModel = model.Compilation.GetSemanticModel(tree);

            var argumentList = inv.ArgumentList.Arguments;
            var parameterMap2 = parameterMap is null ? [] : new Dictionary<string, string>(parameterMap);

            for (var i = 0; i < symbol.Parameters.Length; i++)
            {
                var param = symbol.Parameters[i];
                var argValue = default(string?);

                // Was the argument passed explicitly?
                if (i < argumentList.Count)
                {
                    var argExpr = argumentList[i].Expression;
                    if (!TryEvaluateSqlExpression(context, argExpr, newModel, out argValue, token, currentIndent, parameterMap))
                        argValue = $"{{{argExpr.ToFullString().Trim()}}}"; // fallback
                }
                else
                {
                    // Not passed — maybe has a default or caller info
                    if (param.HasExplicitDefaultValue)
                    {
                        argValue = param.ExplicitDefaultValue switch
                        {
                            null => "null",
                            string s => s,
                            bool bv => bv ? "1" : "0",
                            char c => c.ToString(),
                            _ => param.ExplicitDefaultValue.ToString() ?? "null"
                        };
                    }

                    // Check for `[Caller*]` attributes which procedurally generate values when their arguments are omitted from parameter lists.
                    if (param is { HasExplicitDefaultValue: true, ExplicitDefaultValue: null } && param.GetAttributes() is { Length: > 0 } attrs)
                    {
                        var maps = new List<Dictionary<string, string>?> { parameterMap, parameterMap2 };
                        foreach (var attr in attrs)
                        {
                            var name = attr.AttributeClass?.Name;

                            if (name is "CallerArgumentExpressionAttribute" &&
                                attr.ConstructorArguments.Length == 1)
                            {
                                var targetParamName = (string?)attr.ConstructorArguments[0].Value;
                                if (targetParamName is null) continue;

                                argValue = ResolveCallerArgumentExpressionRecursive(symbol, argumentList, targetParamName, newModel, maps, token, currentIndent, context);
                            }
                            else if (name is "CallerFilePathAttribute")
                            {
                                argValue = Path.GetFileName(model.SyntaxTree.FilePath);
                            }
                            else if (name is "CallerLineNumberAttribute")
                            {
                                var line = inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                                argValue = line.ToString();
                            }
                            else if (name is "CallerMemberNameAttribute")
                            {
                                // Find the member name containing this invocation
                                var member = inv.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
                                argValue = member switch
                                {
                                    MethodDeclarationSyntax m => m.Identifier.Text,
                                    PropertyDeclarationSyntax p => p.Identifier.Text,
                                    _ => "unknown"
                                };
                            }
                        }
                    }
                }

                argValue ??= "null";
                parameterMap2[param.Name] = argValue;
            }

            // Arrow functions, e.g. `public static string Select1() => "SELECT 1";`
            if (methodDecl.ExpressionBody is { Expression: { } exprBody })
            {
                if (TryEvaluateSqlExpression(context, exprBody, newModel, out var exprBodyValue, token, currentIndent, parameterMap2))
                {
                    value = exprBodyValue;
                    return true;
                }
            }
            // Traditional body functions, e.g. `public static string Select1() { return "SELECT 1"; }`
            else if (methodDecl.Body is { } body)
            {
                var locals = new Dictionary<string, string>(parameterMap2);
                foreach (var stmt in body.Statements)
                {
                    if (stmt is LocalDeclarationStatementSyntax decl)
                    {
                        foreach (var v in decl.Declaration.Variables)
                        {
                            if (v.Initializer is null)
                            {
                                context.ReportDiagnostic(Diagnostic.Create(RawSqlDiagnostics.UninitializedVarInInvocationWarning, v.GetLocation(), v.Identifier.Text));
                            }
                            else if (TryEvaluateSqlExpression(context, v.Initializer.Value, newModel, out var val, token, currentIndent, locals))
                            {
                                locals[v.Identifier.Text] = val!;
                            }
                        }
                    }
                }

                var returnStatements = body.Statements.OfType<ReturnStatementSyntax>().ToImmutableArray();
                if (returnStatements.Length > 1)
                {
                    context.ReportDiagnostic(Diagnostic.Create(RawSqlDiagnostics.MultipleReturnStatementsInInvocationWarning, body.GetLocation(), methodDecl.Identifier.Text));
                }
                var ret = returnStatements.FirstOrDefault();
                if (ret is not null && TryEvaluateSqlExpression(context, ret.Expression!, newModel, out var retVal, token, currentIndent, locals))
                {
                    value = retVal;
                    return true;
                }
            }
            return false;
        }

        // Interpolated strings.
        if (expr is InterpolatedStringExpressionSyntax interpolated)
        {
            var sb = new System.Text.StringBuilder();

            foreach (var content in interpolated.Contents)
            {
                if (content is InterpolatedStringTextSyntax text)
                {
                    sb.Append(text.TextToken.ValueText);
                }
                else if (content is InterpolationSyntax interpolation)
                {
                    var relativeIndent = SqlText.GetRelativeIndent(interpolation);
                    var nestedIndent = currentIndent + relativeIndent - SqlText.GeneratedIndent.Length;
                    // ReSharper disable once ConvertIfStatementToConditionalTernaryExpression
                    if (TryEvaluateSqlExpression(context, interpolation.Expression, model, out var interpolatedValue, token, nestedIndent, parameterMap))
                    {
                        sb.Append(SqlText.IndentMultiline(interpolatedValue!, nestedIndent));
                    }
                    else
                    {
                        sb.Append($"{{{interpolation.Expression}}}");
                    }
                }
            }

            // --- Handle indentation for multiline strings ---
            // Preserve relative indentation to the closing triple quotes
            value = SqlText.PreserveRelativeIndent(sb.ToString());
            return true;
        }

        return false;
    }

    internal static string? ResolveCallerArgumentExpressionRecursive(
        IMethodSymbol currentMethod,
        IReadOnlyList<ArgumentSyntax> currentArgs,
        string targetParamName,
        SemanticModel currentModel,
        List<Dictionary<string, string>?> maps,
        CancellationToken token,
        int currentIndent,
        in SourceProductionContext context)
    {
        // Find parameter index
        var idx = currentMethod.Parameters
            .Select((p, i) => (p.Name, i))
            .FirstOrDefault(t => t.Name == targetParamName).i;

        if (idx < 0)
            return null;

        // 1. If the argument was explicitly passed
        if (idx < currentArgs.Count)
        {
            var argExpr = currentArgs[idx].Expression;

            // Try to get identifier name (symbol text) if possible
            if (argExpr is IdentifierNameSyntax id)
                return id.Identifier.Text;

            // Otherwise, evaluate or fall back to raw expression text
            return TryEvaluateSqlExpression(context, argExpr, currentModel, out var val, token, currentIndent, maps.Last())
                ? val
                : argExpr.ToFullString().Trim();
        }

        // 2. If the argument wasn't passed explicitly, check if it exists in the map (maybe passed indirectly)
        foreach (var map in maps)
        {
            if (map is null) continue;
            if (map.TryGetValue(targetParamName, out var mappedValue))
                return mappedValue;
        }

        // 3. If the target parameter itself had its own CallerArgumentExpression, recurse
        var param = currentMethod.Parameters[idx];
        foreach (var attr in param.GetAttributes())
        {
            if (attr.AttributeClass?.Name is not "CallerArgumentExpressionAttribute"
                || attr.ConstructorArguments.Length != 1)
                continue;

            var nestedTarget = (string?)attr.ConstructorArguments[0].Value;
            if (nestedTarget is not null)
                return ResolveCallerArgumentExpressionRecursive(
                    currentMethod, currentArgs, nestedTarget, currentModel, maps, token, currentIndent, context);
        }

        // 4. No explicit argument, no map, no nested attribute
        return null;
    }

    /// <summary>
    /// Evaluates a boolean condition expression, handling both ordinary boolean expressions
    /// (by delegating to <see cref="TryEvaluateSqlExpression"/>) and pattern-matching
    /// expressions such as <c>x is not null</c> or <c>x is null</c>.
    /// </summary>
    internal static bool TryEvaluateCondition(
        in SourceProductionContext context,
        ExpressionSyntax condExpr,
        SemanticModel model,
        out bool result,
        CancellationToken token,
        Dictionary<string, string>? parameterMap)
    {
        result = false;

        if (condExpr is IsPatternExpressionSyntax isPattern)
            return TryEvaluateIsPattern(context, isPattern, model, out result, token, parameterMap);

        if (!TryEvaluateSqlExpression(context, condExpr, model, out var condVal, token, 0, parameterMap))
            return false;

        result = bool.TryParse(condVal, out var b) ? b : condVal is not ("0" or "" or "null");
        return true;
    }

    /// <summary>
    /// Evaluates an <c>is</c>-pattern expression such as <c>x is not null</c>, <c>x is null</c>,
    /// or a property-pattern like <c>x is { Length: > 0 }</c> against the current parameter map.
    /// </summary>
    internal static bool TryEvaluateIsPattern(
        in SourceProductionContext context,
        IsPatternExpressionSyntax isPattern,
        SemanticModel model,
        out bool result,
        CancellationToken token,
        Dictionary<string, string>? parameterMap)
    {
        result = false;

        // Resolve the left-hand side value (maybe null if the identifier maps to "null" or is absent).
        TryEvaluateSqlExpression(context, isPattern.Expression, model, out var lhsValue, token, 0, parameterMap);

        return TryMatchPattern(isPattern.Pattern, lhsValue, out result);

        static bool TryMatchPattern(PatternSyntax pattern, string? lhsValue, out bool matched)
        {
            matched = false;

            switch (pattern)
            {
                // x is null
                case ConstantPatternSyntax { Expression: LiteralExpressionSyntax lit }
                    when lit.IsKind(SyntaxKind.NullLiteralExpression):
                    matched = lhsValue is null or "null";
                    return true;

                // x is not <pattern>
                case UnaryPatternSyntax { OperatorToken.Text: "not" } notPattern:
                    if (!TryMatchPattern(notPattern.Pattern, lhsValue, out var inner))
                        return false;
                    matched = !inner;
                    return true;

                // x is { Length: > 0 }, etc.
                case RecursivePatternSyntax recursive:
                {
                    // We only handle simple property sub-patterns against known string values.
                    foreach (var subPattern in recursive.PropertyPatternClause?.Subpatterns ?? default)
                    {
                        var propName = subPattern.NameColon?.Name.Identifier.Text
                                       ?? subPattern.ExpressionColon?.Expression.ToString();
                        if (propName is null) return false;

                        // For now only handle the `Length` property on strings.
                        if (propName != "Length") return false;
                        if (lhsValue is null or "null")
                        {
                            // null has no Length — treat length as 0
                            if (!TryMatchPattern(subPattern.Pattern, "0", out var subResult))
                                return false;
                            matched = subResult;
                        }
                        else
                        {
                            // Represent the length as a string so relational patterns can compare it.
                            var lengthStr = lhsValue.Length.ToString();
                            if (!TryMatchPattern(subPattern.Pattern, lengthStr, out var subResult))
                                return false;
                            matched = subResult;
                        }
                    }
                    return true;
                }

                // x is > 0, x is >= 1, etc.
                case RelationalPatternSyntax relational:
                {
                    if (lhsValue is null) return false;
                    if (!int.TryParse(lhsValue, out var lhsInt)) return false;
                    if (!int.TryParse(relational.Expression.ToString(), out var rhsInt)) return false;
                    matched = relational.OperatorToken.Text switch
                    {
                        ">" => lhsInt > rhsInt,
                        ">=" => lhsInt >= rhsInt,
                        "<" => lhsInt < rhsInt,
                        "<=" => lhsInt <= rhsInt,
                        _ => false
                    };
                    return true;
                }

                default:
                    return false;
            }
        }
    }

    internal static Type? GetSystemType(ITypeSymbol? typeSymbol)
    {
        if (typeSymbol == null)
            return null;

        return typeSymbol.SpecialType switch
        {
            SpecialType.System_Boolean => typeof(bool),
            SpecialType.System_Byte => typeof(byte),
            SpecialType.System_SByte => typeof(sbyte),
            SpecialType.System_Int16 => typeof(short),
            SpecialType.System_UInt16 => typeof(ushort),
            SpecialType.System_Int32 => typeof(int),
            SpecialType.System_UInt32 => typeof(uint),
            SpecialType.System_Int64 => typeof(long),
            SpecialType.System_UInt64 => typeof(ulong),
            SpecialType.System_Single => typeof(float),
            SpecialType.System_Double => typeof(double),
            SpecialType.System_Decimal => typeof(decimal),
            SpecialType.System_String => typeof(string),
            SpecialType.System_Object => typeof(object),
            SpecialType.System_Char => typeof(char),
            SpecialType.System_DateTime => typeof(DateTime),
            _ => null
        };
    }

    internal static string FormatConstant(object value) =>
        value switch
        {
            string s => $"'{s.Replace("'", "''")}'",
            bool b => b ? "1" : "0",
            char c => $"'{c}'",
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null"
        };
}
