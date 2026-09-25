// RawSqlAttributeArgumentParser.cs

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SpiritSync.Generators;

/// <summary>
/// Parses argument values from <c>[RawSql]</c> attribute syntax nodes at compile time.
/// </summary>
internal static class RawSqlAttributeArgumentParser
{
    /// <summary>
    /// Attempts to extract a text value from an attribute argument expression using the semantic model.
    /// Returns <see langword="null"/> and sets <paramref name="matched"/> to <see langword="false"/> on failure.
    /// </summary>
    internal static string? SafeGetTextValue(ExpressionSyntax expr, SemanticModel model, Compilation compilation, out bool matched)
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
}
