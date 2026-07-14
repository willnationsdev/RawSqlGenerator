namespace SpiritSync.RawSqlGenerator;

/// <summary>
/// Minimal HashCode helper for netstandard2.0.
/// Intended for use in source generators and other dependency-free contexts.
/// </summary>
internal struct HashCode
{
    // Large odd prime (same one used by many .NET hash implementations)
    private const int Seed = unchecked((int)2166136261);
    private const int Factor = 16777619;

    private int _hash;

    public void Add<T>(T? value)
    {
        Add(value?.GetHashCode() ?? 0);
    }

    public void Add(int value)
    {
        unchecked
        {
            _hash = (_hash == 0 ? Seed : _hash);
            _hash = (_hash * Factor) ^ value;
        }
    }

    public int ToHashCode()
    {
        return _hash;
    }

    public static int Combine<T1>(T1? value1)
    {
        var hc = new HashCode();
        hc.Add(value1);
        return hc.ToHashCode();
    }

    public static int Combine<T1, T2>(T1? value1, T2? value2)
    {
        var hc = new HashCode();
        hc.Add(value1);
        hc.Add(value2);
        return hc.ToHashCode();
    }

    public static int Combine<T1, T2, T3>(T1? value1, T2? value2, T3? value3)
    {
        var hc = new HashCode();
        hc.Add(value1);
        hc.Add(value2);
        hc.Add(value3);
        return hc.ToHashCode();
    }
}