namespace Fluorite.Core.Nbt;

/// <summary>Tag ids of Java Edition NBT.</summary>
#pragma warning disable CA1720 // The members are the spec's tag names (TAG_Int, TAG_String…).
public enum NbtTagType : byte
{
    End = 0,
    Byte = 1,
    Short = 2,
    Int = 3,
    Long = 4,
    Float = 5,
    Double = 6,
    ByteArray = 7,
    String = 8,
    List = 9,
    Compound = 10,
    IntArray = 11,
    LongArray = 12,
}
#pragma warning restore CA1720

/// <summary>An NBT value. Files read and written back keep every tag, including ones Fluorite doesn't know.</summary>
public abstract class NbtTag
{
    public abstract NbtTagType Type { get; }

    /// <summary>Numeric tags as a 64-bit integer (floating values truncated); null for other kinds.</summary>
    public virtual long? AsLong => null;
}

public sealed class NbtByte(sbyte value) : NbtTag
{
    public sbyte Value { get; set; } = value;
    public override NbtTagType Type => NbtTagType.Byte;
    public override long? AsLong => Value;
}

public sealed class NbtShort(short value) : NbtTag
{
    public short Value { get; set; } = value;
    public override NbtTagType Type => NbtTagType.Short;
    public override long? AsLong => Value;
}

public sealed class NbtInt(int value) : NbtTag
{
    public int Value { get; set; } = value;
    public override NbtTagType Type => NbtTagType.Int;
    public override long? AsLong => Value;
}

public sealed class NbtLong(long value) : NbtTag
{
    public long Value { get; set; } = value;
    public override NbtTagType Type => NbtTagType.Long;
    public override long? AsLong => Value;
}

public sealed class NbtFloat(float value) : NbtTag
{
    public float Value { get; set; } = value;
    public override NbtTagType Type => NbtTagType.Float;
    public override long? AsLong => (long)Value;
}

public sealed class NbtDouble(double value) : NbtTag
{
    public double Value { get; set; } = value;
    public override NbtTagType Type => NbtTagType.Double;
    public override long? AsLong => (long)Value;
}

public sealed class NbtByteArray(ReadOnlyMemory<byte> value) : NbtTag
{
    public ReadOnlyMemory<byte> Value { get; set; } = value;
    public override NbtTagType Type => NbtTagType.ByteArray;
}

public sealed class NbtString(string value) : NbtTag
{
    public string Value { get; set; } = value;
    public override NbtTagType Type => NbtTagType.String;
    public override string ToString() => Value;
}

public sealed class NbtIntArray(ReadOnlyMemory<int> value) : NbtTag
{
    public ReadOnlyMemory<int> Value { get; set; } = value;
    public override NbtTagType Type => NbtTagType.IntArray;
}

public sealed class NbtLongArray(ReadOnlyMemory<long> value) : NbtTag
{
    public ReadOnlyMemory<long> Value { get; set; } = value;
    public override NbtTagType Type => NbtTagType.LongArray;
}

/// <summary>A list of tags that all have <see cref="ElementType"/>.</summary>
public sealed class NbtList : NbtTag, IEnumerable<NbtTag>
{
    private readonly List<NbtTag> _items = [];

    public NbtList(NbtTagType elementType) => ElementType = elementType;

    /// <summary>Element type; <see cref="NbtTagType.End"/> for an empty list written without one.</summary>
    public NbtTagType ElementType { get; private set; }

    public override NbtTagType Type => NbtTagType.List;
    public int Count => _items.Count;
    public NbtTag this[int index] => _items[index];

    public void Add(NbtTag tag)
    {
        if (ElementType == NbtTagType.End && _items.Count == 0)
            ElementType = tag.Type;
        else if (tag.Type != ElementType)
            throw new ArgumentException($"A list of {ElementType} can't hold a {tag.Type}.", nameof(tag));
        _items.Add(tag);
    }

    public void Clear() => _items.Clear();

    public IEnumerator<NbtTag> GetEnumerator() => _items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Named tags in file order.</summary>
public sealed class NbtCompound : NbtTag, IEnumerable<KeyValuePair<string, NbtTag>>
{
    private readonly OrderedDictionary<string, NbtTag> _tags = new(StringComparer.Ordinal);

    public override NbtTagType Type => NbtTagType.Compound;
    public int Count => _tags.Count;

    public NbtTag? this[string name]
    {
        get => _tags.GetValueOrDefault(name);
        set
        {
            if (value is null)
                _tags.Remove(name);
            else
                _tags[name] = value;
        }
    }

    public bool Contains(string name) => _tags.ContainsKey(name);
    public bool Remove(string name) => _tags.Remove(name);

    public NbtCompound? GetCompound(string name) => this[name] as NbtCompound;
    public NbtList? GetList(string name) => this[name] as NbtList;
    public string? GetString(string name) => (this[name] as NbtString)?.Value;
    public long? GetLong(string name) => this[name]?.AsLong;
    public int? GetInt(string name) => GetLong(name) is { } v ? (int)v : null;
    public bool? GetBool(string name) => GetLong(name) is { } v ? v != 0 : null;

    public void Set(string name, string value) => _tags[name] = new NbtString(value);
    public void Set(string name, bool value) => _tags[name] = new NbtByte(value ? (sbyte)1 : (sbyte)0);
    public void Set(string name, int value) => _tags[name] = new NbtInt(value);
    public void Set(string name, long value) => _tags[name] = new NbtLong(value);

    public IEnumerator<KeyValuePair<string, NbtTag>> GetEnumerator() => _tags.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
