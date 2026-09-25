// RawSqlDiagnostics.cs

using Microsoft.CodeAnalysis;

namespace SpiritSync.Generators;

internal static class RawSqlDiagnostics
{
    internal static readonly DiagnosticDescriptor DuplicateVariableWarning = new(
        id: "RSQL001",
        title: "Duplicate RawSql variable target",
        messageFormat: "Multiple [RawSql] attributes on '{0}' target the same variable '{1}'. Only one will be used.",
        category: "RawSqlGenerator",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor DuplicateConstantWarning = new(
        id: "RSQL002",
        title: "Duplicate RawSql constant name",
        messageFormat: "Multiple [RawSql] attributes on '{0}' would produce the same constant '{1}'. Constant generation skipped.",
        category: "RawSqlGenerator",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UninitializedVarInInvocationWarning = new(
        id: "RSQL003",
        title: "Uninitialized var declaration in interpolated string invocation",
        messageFormat: "[RawSql] can only parse interpolated strings that embed invocation calls with initialized variable declarations in the body. Variable '{0}' may be interpolated improperly.",
        category: "RawSqlGenerator",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor MultipleReturnStatementsInInvocationWarning = new(
        id: "RSQL004",
        title: "Multiple return statements present in nested interpolated string invocation",
        messageFormat: "Only static methods with a single return statement are permitted within interpolated strings evaluated by the RawSqlGenerator. Only the first one will be used. Method '{0}' may be interpolated improperly.",
        category: "RawSqlGenerator",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnresolvedInterpolationError = new(
        id: "RSQL005",
        title: "Failed to statically resolve an interpolated value",
        messageFormat: "Unresolved interpolation `{{{0}}}` detected on line {1}, column {2}",
        category: "RawSqlGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor EvalRawSqlArgumentError = new(
        id: "RSQL006",
        title: "Failed to statically resolve an argument in a [RawSql] attribute",
        messageFormat: "Error evaluating [RawSql] argument: {0}",
        category: "RawSqlGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor MissingIdsOrderByExpressionError = new(
        id: "RSQL007",
        title: "Missing IdsOrderByExpression when CTE contains ORDER BY",
        messageFormat: "The CTE '{0}' contains an ORDER BY clause. When IdsCteAlias is specified and the CTE has an ORDER BY, IdsOrderByExpression must also be provided on the [RawSql] attribute.",
        category: "RawSqlGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
