// RawSqlGenerator.cs
using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SpiritSync.Generators.Parser;
using SpiritSync.Generators.Utilities;

namespace SpiritSync.Generators;

internal record EvalSyntaxRecord(string FilePath, int MethodStart, int MethodLength, EquatableArray<(int Start, int Length)> AttributeSpans);
internal record RawSqlInstance(string VariableName, string ConstantName, string? IdsColumnExpression, string? IdsCteAlias, string? IdsOrderByExpression, Location Location);

[Generator(LanguageNames.CSharp)]
public sealed class RawSqlGenerator : IIncrementalGenerator
{
    private const string DefaultVariableName = "sql";
    private const string ConstantNameSuffix = "RawSql";
    private const string IdsOnlySuffix = "IdsOnly";
    private const string IdsColumnExpressionProperty = "IdsColumnExpression";
    private const string IdsCteAliasProperty = "IdsCteAlias";
    private const string IdsOrderByExpressionProperty = "IdsOrderByExpression";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(ctx =>
        {
            ctx.AddSource("RawSqlAttribute.g.cs", """
                // ReSharper disable RedundantNameQualifier
                #nullable enable
                namespace BaylorCoreFramework.Core.Generators.RawSql;

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

                    /// <summary>
                    /// When specified, an additional <c>IdsOnly</c> constant is generated that SELECTs only this column expression.
                    /// Example: <c>"i.pid AS Pid"</c>
                    /// </summary>
                    public string? IdsColumnExpression { get; set; }

                    /// <summary>
                    /// When specified alongside <see cref="IdsColumnExpression"/>, targets the SELECT inside the named CTE
                    /// (common table expression) for column replacement instead of the first top-level SELECT.
                    /// The final outer SELECT will also be simplified to <c>alias.*</c> if it contains extra expressions.
                    /// Example: <c>"result"</c>
                    /// </summary>
                    public string? IdsCteAlias { get; set; }

                    /// <summary>
                    /// When <see cref="IdsCteAlias"/> is specified and the CTE contains an ORDER BY clause,
                    /// this expression replaces the ORDER BY expression in the IDs-only variant.
                    /// Required whenever the CTE has an ORDER BY; omitting it is a compile-time error.
                    /// Example: <c>"Pid"</c>
                    /// </summary>
                    public string? IdsOrderByExpression { get; set; }

                    public RawSqlAttribute(string variableName = "sql", string? constantName = null)
                    {
                        VariableName = variableName;
                        ConstantName = constantName;
                    }
                }
                """);
        });

        var methods = context.SyntaxProvider.ForAttributeWithMetadataName("BaylorCoreFramework.Core.Generators.RawSql.RawSqlAttribute",
            predicate: static (n, _) => n is MethodDeclarationSyntax,
            transform: static (ctx, _) =>
                {
                    var method = (MethodDeclarationSyntax)ctx.TargetNode;
                    var sm = ctx.SemanticModel;
                    var attrs = method.AttributeLists
                        .SelectMany(static list => list.Attributes)
                        .Where(a => sm.GetSymbolInfo(a).Symbol is IMethodSymbol ms &&
                            ms.ContainingType.ToDisplayString() == "BaylorCoreFramework.Core.Generators.RawSql.RawSqlAttribute")
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
            if (tree is null) continue;

            var root = tree.GetRoot();
            var method = root.FindNode(new TextSpan(methodStart, methodLength)) as MethodDeclarationSyntax;
            if (method is null) continue;

            var model = compilation.GetSemanticModel(method.SyntaxTree);
            if (method.Parent is not ClassDeclarationSyntax classDecl) continue;

            var methodName = method.Identifier.Text;

            var attrNodes = attrSpanInfos
                .Select(s => root.FindNode(new TextSpan(s.Start, s.Length)) as AttributeSyntax)
                .Where(s => s is not null)
                .Cast<AttributeSyntax>()
                .ToImmutableArray();

            var attrData = attrNodes.Select(a =>
                {
                    var positionalArgs = a.ArgumentList?.Arguments
                        .Where(arg => arg.NameEquals is null)
                        .Select(arg =>
                        {
                            var val = RawSqlAttributeArgumentParser.SafeGetTextValue(arg.Expression, model, compilation, out var matched);
                            return matched ? val : null;
                        })
                        .ToArray() ?? [];
                    var variableName = positionalArgs.Length > 0 && !string.IsNullOrEmpty(positionalArgs[0]) ? positionalArgs[0]! : DefaultVariableName;
                    var constantName = positionalArgs.Length > 1 && !string.IsNullOrEmpty(positionalArgs[1]) ? positionalArgs[1]! : $"{methodName}{ConstantNameSuffix}";

                    string? idsColumnExpression = null;
                    var idsArg = a.ArgumentList?.Arguments
                        .FirstOrDefault(arg => arg.NameEquals?.Name.Identifier.Text == IdsColumnExpressionProperty);
                    if (idsArg is not null)
                        idsColumnExpression = RawSqlAttributeArgumentParser.SafeGetTextValue(idsArg.Expression, model, compilation, out _);

                    string? idsCteAlias = null;
                    var idsCteArg = a.ArgumentList?.Arguments
                        .FirstOrDefault(arg => arg.NameEquals?.Name.Identifier.Text == IdsCteAliasProperty);
                    if (idsCteArg is not null)
                        idsCteAlias = RawSqlAttributeArgumentParser.SafeGetTextValue(idsCteArg.Expression, model, compilation, out _);

                    string? idsOrderByExpression = null;
                    var idsOrderByArg = a.ArgumentList?.Arguments
                        .FirstOrDefault(arg => arg.NameEquals?.Name.Identifier.Text == IdsOrderByExpressionProperty);
                    if (idsOrderByArg is not null)
                        idsOrderByExpression = RawSqlAttributeArgumentParser.SafeGetTextValue(idsOrderByArg.Expression, model, compilation, out _);

                    return new RawSqlInstance(variableName, constantName, idsColumnExpression, idsCteAlias, idsOrderByExpression, a.GetLocation());
                })
                .ToList();

            // Detect duplicates
            var duplicateVars = attrData.GroupBy(x => x.VariableName).Where(g => g.Count() > 1);
            foreach (var dup in duplicateVars)
                context.ReportDiagnostic(Diagnostic.Create(RawSqlDiagnostics.DuplicateVariableWarning, dup.First().Location, methodName, dup.Key));

            var duplicateConstants = attrData.GroupBy(x => x.ConstantName).Where(g => g.Count() > 1);
            foreach (var dup in duplicateConstants)
                context.ReportDiagnostic(Diagnostic.Create(RawSqlDiagnostics.DuplicateConstantWarning, dup.First().Location, methodName, dup.Key));

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

        foreach (var (variableName, constantName, idsColumnExpression, idsCteAlias, idsOrderByExpression, loc) in uniqueAttrs)
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
                            context.ReportDiagnostic(Diagnostic.Create(RawSqlDiagnostics.UninitializedVarInInvocationWarning, v.GetLocation(), v.Identifier.Text));
                        }
                        else if (SqlExpressionEvaluator.TryEvaluateSqlExpression(context, v.Initializer.Value, model, out var val, context.CancellationToken, 0, locals))
                        {
                            locals[v.Identifier.Text] = val!;
                        }
                    }
                }
            }

            if (!SqlExpressionEvaluator.TryEvaluateSqlExpression(context, expr, model, out var evaluatedSql, context.CancellationToken, 0, locals))
                continue;

            // Setup formatting (e.g. splitting ORDER BY columns onto separate lines) before
            // normalization. Formatting guides SqlParser logic.
            var formatter = new SqlFormatter(new SqlFormatOptions
            {
                SplitOrderByColumns = true,
            });

            // Parse the evaluated SQL exactly once; reuse this parser for the raw-string variant and
            // for any query/transform passes that follow.
            var parser = new SqlParser(evaluatedSql, formatter, constantName);

            // Apply all formatting options (ORDER BY splitting, subquery indentation, WHERE AND/OR
            // alignment) to the parse tree. Build() renders the mutated tree and returns a new
            // SqlParser so downstream query/transform passes see the formatted text.
            parser.Format();


            // Normalize indentation and trim to align with generated triple quotes.
            // PrepareForRawString only adjusts whitespace so no reparse is needed.
            var preparedSql = parser.PrepareForRawString();

            var lineSpan = loc.GetLineSpan();
            var lineNum = lineSpan.StartLinePosition.Line + 1;

            var symbol = model.GetDeclaredSymbol(method, context.CancellationToken);
            var crefName = symbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? method.Identifier.Text;
            crefName = System.Security.SecurityElement.Escape(crefName);
            filePath = System.Security.SecurityElement.Escape(filePath);

            const string indent = "        "; // 8

            Append(sb, ref lineCount, $"{indent}/// <summary>");
            Append(sb, ref lineCount, $"{indent}/// <para>");
            Append(sb, ref lineCount, $"{indent}/// Computed value from interpolated string.");
            Append(sb, ref lineCount, $"{indent}/// </para>");
            Append(sb, ref lineCount, $"{indent}/// <para>");
            Append(sb, ref lineCount, $"{indent}/// Source: <see cref=\"{crefName}\" />");
            Append(sb, ref lineCount, $"{indent}/// </para>");
            Append(sb, ref lineCount, $"{indent}/// <para>");
            Append(sb, ref lineCount, $"{indent}/// From: {filePath} (line {lineNum}), <c>var {variableName}</c>");
            Append(sb, ref lineCount, $"{indent}/// </para>");
            Append(sb, ref lineCount, $"{indent}/// </summary>");
            Append(sb, ref lineCount, $"{indent}public const string {constantName} =");
            Append(sb, ref lineCount, $"{indent}    \"\"\"");

            foreach (var (line, column, content) in SqlText.FindUnresolvedInterpolations(preparedSql))
            {
                context.ReportDiagnostic(Diagnostic.Create(RawSqlDiagnostics.UnresolvedInterpolationError, sqlDecl.GetLocation(), content, lineCount + line, column));
            }

            sb.AppendLine(preparedSql);

            sb.AppendLine($"{indent}    \"\"\";");
            sb.AppendLine();

            // Generate the IdsOnly variant if an ID column expression was specified
            if (idsColumnExpression is { Length: > 0 })
            {
                var idsOnlyConstantName = $"{constantName}{IdsOnlySuffix}";

                // If a CTE alias is specified and the CTE has an ORDER BY, require IdsOrderByExpression.
                // Reuse the already-built parser (parser.Query() consults the cached parse tree; no reparse).
                if (idsCteAlias is { Length: > 0 } && parser.Query().CteHasOrderBy(idsCteAlias) && idsOrderByExpression is not { Length: > 0 })
                {
                    context.ReportDiagnostic(Diagnostic.Create(RawSqlDiagnostics.MissingIdsOrderByExpressionError, loc, idsCteAlias));
                    continue;
                }

                // A transformation potentially produces new output, so it produces a new SqlParser via Build().
                var idsOnlyParser = (idsCteAlias is not { Length: > 0 }
                    ? parser.Transform().ReplaceSelectColumns(idsColumnExpression)
                    : parser.Transform().ReplaceCteSqlColumns(idsCteAlias, idsColumnExpression, idsOrderByExpression)).Build();

                // Re-indent for the raw string literal; no reparse needed.
                var preparedIdsOnlySql = idsOnlyParser.PrepareForRawString();

                var idsLineCount = leadingLines;
                Append(sb, ref idsLineCount, $"{indent}/// <summary>");
                Append(sb, ref idsLineCount, $"{indent}/// <para>");
                Append(sb, ref idsLineCount, $"{indent}/// IDs-only variant: SELECTs only <c>{System.Security.SecurityElement.Escape(idsColumnExpression)}</c>.");
                Append(sb, ref idsLineCount, $"{indent}/// </para>");
                Append(sb, ref idsLineCount, $"{indent}/// <para>");
                Append(sb, ref idsLineCount, $"{indent}/// Source: <see cref=\"{crefName}\" />");
                Append(sb, ref idsLineCount, $"{indent}/// </para>");
                Append(sb, ref idsLineCount, $"{indent}/// <para>");
                Append(sb, ref idsLineCount, $"{indent}/// From: {filePath} (line {lineNum}), <c>var {variableName}</c>");
                Append(sb, ref idsLineCount, $"{indent}/// </para>");
                Append(sb, ref idsLineCount, $"{indent}/// </summary>");
                Append(sb, ref idsLineCount, $"{indent}public const string {idsOnlyConstantName} =");
                Append(sb, ref idsLineCount, $"{indent}    \"\"\"");

                foreach (var (line, column, content) in SqlText.FindUnresolvedInterpolations(preparedIdsOnlySql))
                {
                    context.ReportDiagnostic(Diagnostic.Create(RawSqlDiagnostics.UnresolvedInterpolationError, sqlDecl.GetLocation(), content, idsLineCount + line, column));
                }

                sb.AppendLine(preparedIdsOnlySql);
                sb.AppendLine($"{indent}    \"\"\";");
                sb.AppendLine();
            }

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
}
