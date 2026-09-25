// ISqlFormatter.cs

namespace SpiritSync.Generators.Parser;

/// <summary>
/// Represents a SQL formatter that exposes its formatting configuration as a
/// <see cref="SqlFormatOptions"/> instance.
/// </summary>
internal interface ISqlFormatter : ISqlVisitor
{
    /// <summary>Gets the formatting options that govern how this formatter processes SQL text.</summary>
    SqlFormatOptions Options { get; }
}
