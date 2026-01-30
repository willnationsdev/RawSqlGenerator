// RawSqlGenerator.cs
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using SpiritSync.RawSqlGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// ReSharper disable ConvertIfStatementToSwitchStatement

namespace RawSqlGen;

public record EvalSyntaxRecord(string FilePath, int MethodStart, int MethodLength, EquatableArray<(int Start, int Length)> AttributeSpans);
public record RawSqlInstance(string VariableName, string ConstantName, Location Location);

[Generator(LanguageNames.CSharp)]
public sealed class RawSqlGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor DuplicateVariableWarning = new(
            id: "RSQL001",
            title: "Duplicate RawSql variable target",
            messageFormat: "Multiple [RawSql] attributes on '{0}' target the same variable '{1}'. Only one will be used.",
            category: "RawSqlGenerator",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateConstantWarning = new(
        id: "RSQL002",
        title: "Duplicate RawSql constant name",
        messageFormat: "Multiple [RawSql] attributes on '{0}' would produce the same constant '{1}'. Constant generation skipped.",
        category: "RawSqlGenerator",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UninitializedVarInInvocationWarning = new(
        id: "RSQL003",
        title: "Uninitialized var declaration in interpolated string invocation",
        messageFormat: "[RawSql] can only parse interpolated strings that embed invocation calls with initialized variable declarations in the body. Variable '{0}' may be interpolated improperly.",
        category: "RawSqlGenerator",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MultipleReturnStatementsInInvocationWarning = new(
        id: "RSQL004",
        title: "Multiple return statements present in nested interpolated string invocation",
        messageFormat: "Only static methods with a single return statement are permitted within interpolated strings evaluated by the RawSqlGenerator. Only the first one will be used. Method '{0}' may be interpolated improperly.",
        category: "RawSqlGenerator",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnresolvedInterpolationError = new(
        id: "RSQL005",
        title: "Failed to statically resolve an interpolated value",
        messageFormat: "Unresolved interpolation `{{{0}}}` detected on line {1}, column {2}",
        category: "RawSqlGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor EvalRawSqlArgumentError = new(
        id: "RSQL006",
        title: "Failed to statically resolve an argument in a [RawSql] attribute",
        messageFormat: "Error evaluating [RawSql] argument: {0}",
        category: "RawSqlGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(ctx =>
        {
            ctx.AddSource("RawSqlAttribute.g.cs", """
                // ReSharper disable RedundantNameQualifier
                #nullable enable
                namespace RawSqlGen;

                [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]
                public sealed class RawSqlAttribute : global::System.Attribute
                {
                    /// <summary>
                    /// The name of the local variable to scan for SQL text (default: "sql").
                    /// </summary>
                    public string VariableName { get; }

                    /// <summary>
                    /// The name of the generated constant (default: &lt;MethodName&gt;RawSql).
                    /// </summary>
                    public string? ConstantName { get; }

                    public RawSqlAttribute(string variableName = "sql", string? constantName = null)
                    {
                        VariableName = variableName;
                        ConstantName = constantName;
                    }
                }
                """);
        });

        var methods = context.SyntaxProvider.ForAttributeWithMetadataName("RawSqlGen.RawSqlAttribute",
            predicate: static (n, _) => n is MethodDeclarationSyntax,
            transform: static (ctx, _) =>
                {
                    var method = (MethodDeclarationSyntax)ctx.TargetNode;
                    var sm = ctx.SemanticModel;
                    var attrs = method.AttributeLists
                        .SelectMany(static list => list.Attributes)
                        .Where(a => sm.GetSymbolInfo(a).Symbol is IMethodSymbol ms &&
                            ms.ContainingType.ToDisplayString() == "RawSqlGen.RawSqlAttribute")
                        .ToList();

                    if (attrs.Count == 0) return null;

                    var filePath = method.SyntaxTree.FilePath;
                    var methodSpan = method.Span;

                    var attrSpans = attrs
                        .Select(static a => (a.Span.Start, a.Span.Length))
                        .ToEquatableArray();

                    return attrs.Count > 0 ? new EvalSyntaxRecord(filePath, method.Span.Start, methodSpan.Length, attrSpans) : null;
                })
            .Where(static x => x != null);

        var combined = context.CompilationProvider.Combine(methods.Collect());

        context.RegisterSourceOutput(combined, Execute);
    }

    private static void Execute(SourceProductionContext context, (Compilation compilation, ImmutableArray<EvalSyntaxRecord?>) input)
    {
        var (compilation, items) = input;

        foreach (var rec in items)
        {
            if (rec is null) continue;
            var (filePath, methodStart, methodLength, attrSpanInfos) = rec;
            var tree = compilation.SyntaxTrees.FirstOrDefault(t => t.FilePath == filePath);

            var root = tree?.GetRoot();
            if (root?.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(methodStart, methodLength)) is not MethodDeclarationSyntax method) continue;

            var model = compilation.GetSemanticModel(method.SyntaxTree);
            if (method.Parent is not ClassDeclarationSyntax { } classDecl) continue;

            var methodName = method.Identifier.Text;

            var attrNodes = attrSpanInfos
                .Select(s => root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(s.Start, s.Length)) as AttributeSyntax)
                .Where(s => s is not null)
                .Cast<AttributeSyntax>()
                .ToImmutableArray();

            var attrData = attrNodes.Select(a =>
                {
                    //var symbol = model.GetSymbolInfo(a).Symbol as IMethodSymbol;
                    var constantArgs = a.ArgumentList?.Arguments
                        .Select(arg =>
                        {
                            var val = SafeGetTextValue(arg.Expression, model, compilation, out var matched);
                            return matched ? val : null;

                            static string? SafeGetTextValue(ExpressionSyntax expr, SemanticModel model, Compilation compilation, out bool matched)
                            {
                                try
                                {
                                    var m = expr.SyntaxTree != model.SyntaxTree ? compilation.GetSemanticModel(expr.SyntaxTree) : model;
                                    var constVal = m.GetConstantValue(expr);
                                    if (constVal.HasValue)
                                    {
                                        matched = true;
                                        return constVal.Value?.ToString();
                                    }

                                    if (expr is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "nameof" } nameOf2 })
                                    {
                                        matched = true;
                                        return nameOf2.Identifier.Text;
                                    }
                                    if (expr is MemberAccessExpressionSyntax member)
                                    {
                                        matched = true;
                                        return member.ToString();
                                    }
                                    if (expr is LiteralExpressionSyntax lit)
                                    {
                                        matched = true;
                                        return lit.Token.ValueText;
                                    }
                                    if (expr is BinaryExpressionSyntax bin)
                                    {
                                        var left = SafeGetTextValue(bin.Left, model, compilation, out _);
                                        var right = SafeGetTextValue(bin.Right, model, compilation, out _);
                                        matched = true;
                                        return left + right;
                                    }
                                    if (expr is InterpolatedStringExpressionSyntax interpolated)
                                    {
                                        var sb = new StringBuilder();

                                        foreach (var content in interpolated.Contents)
                                        {
                                            if (content is InterpolatedStringTextSyntax interpolatedText)
                                            {
                                                sb.Append(interpolatedText.TextToken.ValueText);
                                            }
                                            else if (content is InterpolationSyntax interpolation)
                                            {
                                                var text = SafeGetTextValue(interpolation.Expression, model, compilation, out _);
                                                sb.Append(!string.IsNullOrEmpty(text)
                                                    ? text
                                                    : $"{{{interpolation.Expression.ToFullString().Trim()}}}");
                                            }
                                        }

                                        matched = true;
                                        return sb.ToString();
                                    }
                                    matched = false;
                                    return null;
                                }
                                catch
                                {
                                    // cross-tree or model mismatch — fall back to textual representation
                                    matched = false;
                                    return null;
                                }
                            }
                        })
                        .ToArray() ?? [];
                    var variableName = constantArgs.Length > 0 && !string.IsNullOrEmpty(constantArgs[0]) ? constantArgs[0]! : "sql";
                    var constantName = constantArgs.Length > 1 && !string.IsNullOrEmpty(constantArgs[1]) ? constantArgs[1]! : $"{methodName}RawSql";
                    return new RawSqlInstance(variableName, constantName, a.GetLocation());
                })
                .ToList();

            // Detect duplicates
            var duplicateVars = attrData.GroupBy(x => x.VariableName).Where(g => g.Count() > 1);
            foreach (var dup in duplicateVars)
                context.ReportDiagnostic(Diagnostic.Create(DuplicateVariableWarning, dup.First().Location, methodName, dup.Key));

            var duplicateConstants = attrData.GroupBy(x => x.ConstantName).Where(g => g.Count() > 1);
            foreach (var dup in duplicateConstants)
                context.ReportDiagnostic(Diagnostic.Create(DuplicateConstantWarning, dup.First().Location, methodName, dup.Key));

            // Skip constants with duplicate names
            var uniqueAttrs = attrData
                .GroupBy(x => x.ConstantName)
                .Select(g => g.First())
                .ToList();

            GenerateFile(context, classDecl, method, model, uniqueAttrs);
        }
    }

    private static void GenerateFile(in SourceProductionContext context, ClassDeclarationSyntax classDecl, MethodDeclarationSyntax method, SemanticModel model, List<RawSqlInstance> uniqueAttrs)
    {
        var ns = GetNamespace(classDecl);
        var filePath = method.SyntaxTree.FilePath;

        var generatedFilePath = $"{classDecl.Identifier.Text}_{method.Identifier.Text}_RawSql.g.cs";

        // Generate partial for each unique attribute
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine($"namespace {ns}");
        sb.AppendLine("{");
        sb.AppendLine($"    public partial class {classDecl.Identifier.Text}");
        sb.AppendLine("    {");
        const int leadingLines = 5;

        foreach (var (variableName, constantName, loc) in uniqueAttrs)
        {
            var lineCount = leadingLines;
            var sqlDecl = method.DescendantNodes()
                .OfType<VariableDeclaratorSyntax>()
                .FirstOrDefault(v => v.Identifier.Text == variableName);

            if (sqlDecl?.Initializer?.Value is not { } expr)
                continue;

            var locals = new Dictionary<string, string>();
            if (method.Body is { } body)
            {
                foreach (var stmt in body.Statements)
                {
                    if (stmt is not LocalDeclarationStatementSyntax decl) continue;

                    foreach (var v in decl.Declaration.Variables)
                    {
                        if (v.Initializer is null)
                        {
                            context.ReportDiagnostic(Diagnostic.Create(UninitializedVarInInvocationWarning, v.GetLocation(), v.Identifier.Text));
                        }
                        else if (TryEvaluateSqlExpression(context, v.Initializer.Value, model, out var val, context.CancellationToken, 0, locals))
                        {
                            locals[v.Identifier.Text] = val;
                        }
                    }
                }
            }

            if (!TryEvaluateSqlExpression(context, expr, model, out var evaluatedSql, context.CancellationToken, 0, locals))
                continue;

            // Normalize indentation and trim to align with generated triple quotes
            var preparedSql = PrepareSqlForRawString(evaluatedSql);

            var lineSpan = loc.GetLineSpan();
            var lineNum = lineSpan.StartLinePosition.Line + 1;

            var symbol = model.GetDeclaredSymbol(method, context.CancellationToken);
            var crefName = symbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? method.Identifier.Text;
            crefName = System.Security.SecurityElement.Escape(crefName);
            filePath = System.Security.SecurityElement.Escape(filePath);

            const string indent = "        "; // 8
            
            Append(sb, ref lineCount,$"{indent}/// <summary>");
            Append(sb, ref lineCount,$"{indent}/// <para>");
            Append(sb, ref lineCount,$"{indent}/// Computed value from interpolated string.");
            Append(sb, ref lineCount,$"{indent}/// </para>");
            Append(sb, ref lineCount,$"{indent}/// <para>");
            Append(sb, ref lineCount,$"{indent}/// Source: <see cref=\"{crefName}\" />");
            Append(sb, ref lineCount,$"{indent}/// </para>");
            Append(sb, ref lineCount,$"{indent}/// <para>");
            Append(sb, ref lineCount,$"{indent}/// From: {filePath} (line {lineNum}), <c>var {variableName}</c>");
            Append(sb, ref lineCount,$"{indent}/// </para>");
            Append(sb, ref lineCount,$"{indent}/// </summary>");
            Append(sb, ref lineCount,$"{indent}public const string {constantName} =");
            Append(sb, ref lineCount,$"{indent}    \"\"\"");

            foreach (var (line, column, content) in FindUnresolvedInterpolations(preparedSql))
            {
                context.ReportDiagnostic(Diagnostic.Create(UnresolvedInterpolationError, sqlDecl.GetLocation(), content, lineCount + line, column));
            }

            sb.AppendLine(preparedSql);

            sb.AppendLine($"{indent}    \"\"\";");
            sb.AppendLine();

            continue;

            // This local function is needed to ensure the line numbers of any unresolved interpolations are accurate.
            static void Append(StringBuilder sb, ref int lineCount, string v) { sb.AppendLine(v); lineCount++; } 
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        context.AddSource(generatedFilePath, sb.ToString());
    }

    private static string GetNamespace(SyntaxNode? node)
    {
        while (node is not null)
        {
            if (node is NamespaceDeclarationSyntax ns)
                return ns.Name.ToString();
            if (node is FileScopedNamespaceDeclarationSyntax fns)
                return fns.Name.ToString();
            node = node.Parent!;
        }
        return "GlobalNamespace";
    }

    // Place near the rest of your generator code.
    // Assumes you have: using Microsoft.CodeAnalysis.CSharp.Syntax; using Microsoft.CodeAnalysis;

    private const string GeneratedIndent = "            "; // 12 spaces; must match how you emit the closing quotes line.

    // Entry point used by the generator when you want to produce the raw string constant.
    private static string PrepareSqlForRawString(string sqlText)
    {
        // 1) Normalize common leading indentation from the SQL text (so internal formatting isn't skewed)
        var lines = sqlText.Replace("\r\n", "\n").Split('\n').ToList();

        // Remove leading and trailing blank lines
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines.First()))
            lines.RemoveAt(0);
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines.Last()))
            lines.RemoveAt(lines.Count - 1);

        if (lines.Count == 0)
            return string.Empty;

        // Find minimum indentation among non-empty lines
        var minIndent = int.MaxValue;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var count = 0;
            foreach (var ch in line)
            {
                if (ch == ' ') count++;
                else if (ch == '\t') count += 4; // approximate tabs as 4 spaces
                else break;
            }
            if (count < minIndent) minIndent = count;
        }
        if (minIndent == int.MaxValue) minIndent = 0;

        // Trim that indentation and then re-indent with GeneratedIndent
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var trimmed = line.Length <= minIndent ? string.Empty : line.Substring(minIndent);
            sb.Append(GeneratedIndent);
            sb.AppendLine(trimmed);
        }

        // Note: we leave an ending newline so closing quotes are on their own line
        return sb.ToString().TrimEnd('\r', '\n'); // final AppendLine will add newline in generator
    }

    // --------------------------------------------------
    // Expression evaluation with support for nameof() and evaluating simple helper methods in-source
    // --------------------------------------------------

    // TODO: Add a warning that registers if an interpolated value in the attributed method
    //       references a static method that has more than 1 return statement (not counting any local functions it may have).
    //       We do not plan to support if/else blocks in the injection methods for now.
 
    private static bool TryEvaluateSqlExpression(
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
            if (!TryEvaluateSqlExpression(context, cond.Condition, model, out var condVal, token, currentIndent, parameterMap))
                return false;

            var truthy = bool.TryParse(condVal, out var condBool) && condBool;
            var chosen = truthy ? cond.WhenTrue : cond.WhenFalse;
            if (!TryEvaluateSqlExpression(context, chosen, model, out var condEvalVal, token, currentIndent, parameterMap))
                return false;

            value = condEvalVal;
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

            // Try to get Roslyn’s constant-evaluated value (if known)
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
                        // If semantic info isn’t complete, just return the string
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
                value = PreserveRelativeIndent(literal.Token.ValueText);
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
            if (constantValue is { HasValue: true, Value: string s })
            {
                value = s;
                return true;
            }

            // Check for static properties or fields.

            var symbolInfo = model.GetSymbolInfo(expr, token).Symbol;
            if (symbolInfo is IPropertySymbol { IsStatic: true } prop)
            {
                // Retrieve syntax where property is declared
                if (prop.DeclaringSyntaxReferences.FirstOrDefault() is { } declRef &&
                    declRef.GetSyntax(token) is PropertyDeclarationSyntax propDecl)
                {
                    // static string Foo { get; } = <initializer>;
                    if (propDecl.Initializer?.Value is { } initExpr)
                    {
                        var propModel = model.Compilation.GetSemanticModel(propDecl.SyntaxTree);
                        if (TryEvaluateSqlExpression(context, initExpr, propModel, out var val, token, currentIndent, parameterMap))
                        {
                            value = val;
                            return true;
                        }
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
            if (constantValue is { HasValue: true, Value: string s })
            {
                value = s;
                return true;
            }
        }

        // Syntax-generated string constants from nameof(...), e.g. `nameof(firstName)`.
        if (expr is InvocationExpressionSyntax
            {
                Expression: IdentifierNameSyntax
                {
                    Identifier: { Text: "nameof" }
                }
            } nameOf &&
            nameOf.ArgumentList.Arguments.FirstOrDefault()?.Expression is { } nameOfTarget)
        {
            // nameof(X) → extract the symbol’s name text
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

        // Some other method call.
        // We only care about processing static methods (or maybe local functions within those static methods?) that return a value and are not async.
        // TODO: Implement support for local functions. As it stands, this logic wouldn't populate their parameters / closure-provided variables correctly.
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
                                context.ReportDiagnostic(Diagnostic.Create(UninitializedVarInInvocationWarning, v.GetLocation(), v.Identifier.Text));
                            }
                            else if (TryEvaluateSqlExpression(context, v.Initializer.Value, newModel, out var val, token, currentIndent, locals))
                            {
                                locals[v.Identifier.Text] = val;
                            }
                        }
                    }
                }

                var returnStatements = body.Statements.OfType<ReturnStatementSyntax>().ToImmutableArray();
                if (returnStatements.Length > 1)
                {
                    context.ReportDiagnostic(Diagnostic.Create(MultipleReturnStatementsInInvocationWarning, body.GetLocation(), methodDecl.Identifier.Text));
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
            var sb = new StringBuilder();

            foreach (var content in interpolated.Contents)
            {
                if (content is InterpolatedStringTextSyntax text)
                {
                    sb.Append(text.TextToken.ValueText);
                }
                else if (content is InterpolationSyntax interpolation)
                {
                    var relativeIndent = GetRelativeIndent(interpolation);
                    var nestedIndent = currentIndent + relativeIndent - GeneratedIndent.Length;
                    if (TryEvaluateSqlExpression(context, interpolation.Expression, model, out var interpolatedValue, token, nestedIndent, parameterMap))
                    {
                        sb.Append(IndentMultiline(interpolatedValue, nestedIndent));
                    }
                    else
                    {
                        sb.Append($"{{{interpolation.Expression}}}");
                    }
                }
            }

            // --- Handle indentation for multiline strings ---
            // Preserve relative indentation to the closing triple quotes
            value = PreserveRelativeIndent(sb.ToString());
            return true;
        }

        return false;
    }

    private static string? ResolveCallerArgumentExpressionRecursive(
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

    [return: NotNullIfNotNull(nameof(value))]
    private static string? PreserveRelativeIndent(string? value)
    {
        if (value is null) return null;
        var lines = value.Split(["\r\n", "\n"], StringSplitOptions.None);

        if (lines.Length <= 1) return string.Join("\n", lines);

        // Find minimal non-empty indent
        var minIndent = lines
            .Where(l => l.Trim().Length > 0)
            .Select(l => l.TakeWhile(char.IsWhiteSpace).Count())
            .DefaultIfEmpty(0)
            .Min();

        if (minIndent > 0)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length >= minIndent)
                    lines[i] = lines[i].Substring(minIndent);
            }
        }

        return string.Join("\n", lines);
    }

    private static string IndentMultiline(string value, int indent)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains('\n')) return value;

        var indentStr = new string(' ', indent);
        var lines = value.Split(["\r\n", "\n"], StringSplitOptions.None);

        var sb = new StringBuilder(lines[0]);
        for (var i = 1; i < lines.Length; i++)
        {
            sb.AppendLine();
            sb.Append(indentStr);
            sb.Append(lines[i]);
        }

        return sb.ToString();
    }


    private static int GetRelativeIndent(InterpolationSyntax interpolation)
    {
        var syntaxTree = interpolation.SyntaxTree;
        var text = syntaxTree.GetText();
        var startLine = text.Lines.GetLineFromPosition(interpolation.SpanStart);

        var lineText = startLine.ToString();
        var column = interpolation.SpanStart - startLine.Start; // column where '{' starts

        // Count leading spaces/tabs only up to that column
        var actualIndent = lineText.Take(column).TakeWhile(ch => ch == ' ' || ch == '\t').Count();

        return actualIndent;
    }

    private static IEnumerable<(int Line, int Column, string Content)> FindUnresolvedInterpolations(string sqlText)
    {
        var inString = false;
        var quoteChar = '\0';
        var line = 1;
        var column = 0;

        for (var i = 0; i < sqlText.Length; i++)
        {
            var c = sqlText[i];
            column++;

            // Track line numbers
            if (c == '\n')
            {
                line++;
                column = 0;
            }

            // Toggle in/out of quoted string
            if (!inString && c is '\'' or '\"')
            {
                inString = true;
                quoteChar = c;
                continue;
            }
            if (inString && c == quoteChar)
            {
                inString = false;
                continue;
            }

            // Detect { ... } blocks only outside string literals
            if (inString || c != '{') continue;

            var start = i;
            var depth = 1;

            for (i += 1; i < sqlText.Length; i++)
            {
                c = sqlText[i];
                column++;

                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth != 0) continue;
                    // Capture substring content (without braces)
                    var inner = sqlText.Substring(start + 1, i - start - 1).Trim();
                    yield return (line, column, inner);
                    break;
                }
                else if (c == '\n')
                {
                    line++;
                    column = 0;
                }
            }
        }
    }

    private static Type? GetSystemType(ITypeSymbol? typeSymbol)
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

    private static string FormatConstant(object value) =>
        value switch
        {
            string s => $"'{s.Replace("'", "''")}'",
            bool b => b ? "1" : "0",
            char c => $"'{c}'",
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null"
        };
}