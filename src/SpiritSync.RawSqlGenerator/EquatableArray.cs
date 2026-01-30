// ReSharper disable MemberCanBePrivate.Global
using System.Collections;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
 
namespace SpiritSync.RawSqlGenerator;
 
// Original source:
// https://github.com/CommunityToolkit/dotnet/blob/main/src/CommunityToolkit.Mvvm.SourceGenerators/Helpers/EquatableArray%7BT%7D.cs
 
public readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
	where T : IEquatable<T>
{
	private readonly T[]? _data;
 
	private EquatableArray(ImmutableArray<T> array)
	{
		_data = Unsafe.As<ImmutableArray<T>, T[]?>(ref array);
	}
 
	public EquatableArray(T[] array) : this(array.ToImmutableArray())
	{
	}
 
	public ref readonly T this[int index]
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => ref AsImmutableArray().ItemRef(index);
	}
 
	public int Length => _data?.Length ?? 0;

    public bool Equals(EquatableArray<T> array) => AsSpan().SequenceEqual(array.AsSpan());

    public override bool Equals(object? obj) => obj is EquatableArray<T> array && Equals(this, array);

    public override int GetHashCode()
	{
		if (_data is not { } array)
		{
			return 0;
		}
 
		HashCode hashCode = default;
 
		foreach (var item in array)
		{
			hashCode.Add(item);
		}
 
		return hashCode.ToHashCode();
	}
 
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public ImmutableArray<T> AsImmutableArray() => Unsafe.As<T[]?, ImmutableArray<T>>(ref Unsafe.AsRef(in _data));
    public ReadOnlySpan<T> AsSpan() => AsImmutableArray().AsSpan();
    public T[] ToArray() => AsImmutableArray().ToArray();
    public ImmutableArray<T>.Enumerator GetEnumerator() => AsImmutableArray().GetEnumerator();
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)AsImmutableArray()).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)AsImmutableArray()).GetEnumerator();
    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);
    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);
}

public static class EquatableArrayExtensions
{
	public static EquatableArray<T> ToEquatableArray<T>(this T[] array) where T : IEquatable<T> => new(array);

	public static EquatableArray<T> ToEquatableArray<T>(this IEnumerable<T> source) where T : IEquatable<T> =>
		source.ToArray().ToEquatableArray();
}