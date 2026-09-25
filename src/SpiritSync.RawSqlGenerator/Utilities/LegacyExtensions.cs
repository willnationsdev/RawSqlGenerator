namespace SpiritSync.Generators.Utilities;

public static class LegacyExtensions
{
    extension(char)
    {
        public static bool IsAscii(char other) => other >= 0 && other <= 127;
    }

    extension(Type type)
    {
        public bool IsAssignableTo(Type otherType)
        {
            return otherType.IsAssignableFrom(type);
        }
    }

    extension(ReadOnlySpan<char> span)
    {
        public bool StartsWith(char value)
        {
            if (span.Length == 0) return false;
            return value == span[0];
        }
    }
}
