using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Fluorite.Core.Nbt;

public enum NbtCompression
{
    None,
    GZip,
    Zlib,
}

/// <summary>
/// Reads and writes Java Edition NBT files (big-endian, Java "modified UTF-8" strings). <c>level.dat</c> is gzipped,
/// <c>servers.dat</c> is not; reading detects either.
/// </summary>
public static class NbtFile
{
    private const int MaxDepth = 512;

    /// <summary>Largest array or list accepted, so a corrupt length can't exhaust memory.</summary>
    private const int MaxElements = 64 * 1024 * 1024;

    public static NbtCompound Read(string path, out NbtCompression compression)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Read(fs, out compression);
    }

    public static NbtCompound Read(string path) => Read(path, out _);

    public static NbtCompound Read(Stream stream, out NbtCompression compression)
    {
        var buffered = new BufferedStream(stream);
        Span<byte> head = stackalloc byte[2];
        var n = buffered.Read(head);
        if (n == 0)
            throw new InvalidDataException("The NBT file is empty.");
        compression = n == 2 && head[0] == 0x1F && head[1] == 0x8B ? NbtCompression.GZip
            : n == 2 && head[0] == 0x78 && (head[0] << 8 | head[1]) % 31 == 0 ? NbtCompression.Zlib
            : NbtCompression.None;

        var prefixed = new PrefixStream(head[..n].ToArray(), buffered);
        Stream source = compression switch
        {
            NbtCompression.GZip => new GZipStream(prefixed, CompressionMode.Decompress),
            NbtCompression.Zlib => new ZLibStream(prefixed, CompressionMode.Decompress),
            _ => prefixed,
        };
        using (source)
        {
            var reader = new Reader(source);
            var type = (NbtTagType)reader.ReadByte();
            if (type > NbtTagType.LongArray)
                throw new InvalidDataException("This is not an NBT file.");
            if (type != NbtTagType.Compound)
                throw new InvalidDataException($"The NBT root is a {type}, not a compound.");
            reader.ReadString(); // Root name, normally empty.
            return (NbtCompound)reader.ReadPayload(type, 0);
        }
    }

    public static void Write(string path, NbtCompound root, NbtCompression compression)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Write(fs, root, compression);
    }

    public static void Write(Stream stream, NbtCompound root, NbtCompression compression, string rootName = "")
    {
        Stream target = compression switch
        {
            NbtCompression.GZip => new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true),
            NbtCompression.Zlib => new ZLibStream(stream, CompressionLevel.Optimal, leaveOpen: true),
            _ => new NonClosingStream(stream),
        };
        using (target)
        {
            var w = new Writer(new BufferedStream(target));
            w.WriteByte((byte)NbtTagType.Compound);
            w.WriteString(rootName);
            w.WritePayload(root, 0);
            w.Flush();
        }
    }

    /// <summary>
    /// Replaces <paramref name="path"/> the way Minecraft does: write <c>&lt;name&gt;_new</c>, keep the previous file as
    /// <c>&lt;name&gt;_old</c>, then move the new one in place. A crash at any point leaves a readable file.
    /// </summary>
    public static void WriteSafely(string path, NbtCompound root, NbtCompression compression)
    {
        var temp = path + "_new";
        var old = path + "_old";
        Write(temp, root, compression);
        if (File.Exists(path))
            File.Replace(temp, path, old, ignoreMetadataErrors: true);
        else
            File.Move(temp, path);
    }

    // ── Modified UTF-8 (java.io.DataInput) ───────────────────────────────

    internal static string DecodeModifiedUtf8(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        for (var i = 0; i < bytes.Length;)
        {
            var b = bytes[i];
            if (b < 0x80)
            {
                sb.Append((char)b);
                i++;
            }
            else if ((b & 0xE0) == 0xC0 && i + 1 < bytes.Length && (bytes[i + 1] & 0xC0) == 0x80)
            {
                sb.Append((char)(((b & 0x1F) << 6) | (bytes[i + 1] & 0x3F)));
                i += 2;
            }
            else if ((b & 0xF0) == 0xE0 && i + 2 < bytes.Length && (bytes[i + 1] & 0xC0) == 0x80 && (bytes[i + 2] & 0xC0) == 0x80)
            {
                // Surrogate halves are encoded one at a time, so a supplementary character is two of these.
                sb.Append((char)(((b & 0x0F) << 12) | ((bytes[i + 1] & 0x3F) << 6) | (bytes[i + 2] & 0x3F)));
                i += 3;
            }
            else
            {
                // Standard UTF-8 4-byte sequences (written by some tools) and garbage: decode leniently.
                var len = (b & 0xF8) == 0xF0 ? 4 : 1;
                len = Math.Min(len, bytes.Length - i);
                sb.Append(Encoding.UTF8.GetString(bytes.Slice(i, len)));
                i += len;
            }
        }
        return sb.ToString();
    }

    internal static byte[] EncodeModifiedUtf8(string value)
    {
        var length = 0;
        foreach (var c in value)
            length += c is >= '\u0001' and <= '\u007F' ? 1 : c <= '߿' ? 2 : 3;
        if (length > ushort.MaxValue)
            throw new ArgumentException("NBT strings are limited to 65535 bytes.", nameof(value));

        var bytes = new byte[length];
        var i = 0;
        foreach (var c in value)
        {
            if (c is >= '\u0001' and <= '\u007F')
            {
                bytes[i++] = (byte)c;
            }
            else if (c <= '߿')
            {
                bytes[i++] = (byte)(0xC0 | (c >> 6));
                bytes[i++] = (byte)(0x80 | (c & 0x3F));
            }
            else
            {
                bytes[i++] = (byte)(0xE0 | (c >> 12));
                bytes[i++] = (byte)(0x80 | ((c >> 6) & 0x3F));
                bytes[i++] = (byte)(0x80 | (c & 0x3F));
            }
        }
        return bytes;
    }

    private sealed class Reader(Stream s)
    {
        private readonly byte[] _buf = new byte[8];

        public byte ReadByte()
        {
            var b = s.ReadByte();
            return b < 0 ? throw new EndOfStreamException("The NBT data ends early.") : (byte)b;
        }

        private ReadOnlySpan<byte> Read(int count)
        {
            s.ReadExactly(_buf, 0, count);
            return _buf.AsSpan(0, count);
        }

        public short ReadShort() => BinaryPrimitives.ReadInt16BigEndian(Read(2));
        public int ReadInt() => BinaryPrimitives.ReadInt32BigEndian(Read(4));
        public long ReadLong() => BinaryPrimitives.ReadInt64BigEndian(Read(8));

        public string ReadString()
        {
            var length = (ushort)ReadShort();
            if (length == 0)
                return "";
            var bytes = new byte[length];
            s.ReadExactly(bytes);
            return DecodeModifiedUtf8(bytes);
        }

        private int ReadLength()
        {
            var n = ReadInt();
            return n is < 0 or > MaxElements ? throw new InvalidDataException($"Bad NBT length {n}.") : n;
        }

        public NbtTag ReadPayload(NbtTagType type, int depth)
        {
            if (depth > MaxDepth)
                throw new InvalidDataException("The NBT data is nested too deeply.");
            switch (type)
            {
                case NbtTagType.Byte: return new NbtByte((sbyte)ReadByte());
                case NbtTagType.Short: return new NbtShort(ReadShort());
                case NbtTagType.Int: return new NbtInt(ReadInt());
                case NbtTagType.Long: return new NbtLong(ReadLong());
                case NbtTagType.Float: return new NbtFloat(BitConverter.Int32BitsToSingle(ReadInt()));
                case NbtTagType.Double: return new NbtDouble(BitConverter.Int64BitsToDouble(ReadLong()));
                case NbtTagType.String: return new NbtString(ReadString());
                case NbtTagType.ByteArray:
                {
                    var bytes = new byte[ReadLength()];
                    s.ReadExactly(bytes);
                    return new NbtByteArray(bytes);
                }
                case NbtTagType.IntArray:
                {
                    var values = new int[ReadLength()];
                    for (var i = 0; i < values.Length; i++)
                        values[i] = ReadInt();
                    return new NbtIntArray(values);
                }
                case NbtTagType.LongArray:
                {
                    var values = new long[ReadLength()];
                    for (var i = 0; i < values.Length; i++)
                        values[i] = ReadLong();
                    return new NbtLongArray(values);
                }
                case NbtTagType.List:
                {
                    var elementType = (NbtTagType)ReadByte();
                    var count = ReadLength();
                    var list = new NbtList(count == 0 ? elementType : elementType is NbtTagType.End or > NbtTagType.LongArray
                        ? throw new InvalidDataException($"Bad NBT list element type {(byte)elementType}.")
                        : elementType);
                    for (var i = 0; i < count; i++)
                        list.Add(ReadPayload(elementType, depth + 1));
                    return list;
                }
                case NbtTagType.Compound:
                {
                    var compound = new NbtCompound();
                    while (true)
                    {
                        var childType = (NbtTagType)ReadByte();
                        if (childType == NbtTagType.End)
                            return compound;
                        if (childType > NbtTagType.LongArray)
                            throw new InvalidDataException($"Bad NBT tag type {(byte)childType}.");
                        var name = ReadString();
                        compound[name] = ReadPayload(childType, depth + 1);
                    }
                }
                default:
                    throw new InvalidDataException($"Bad NBT tag type {(byte)type}.");
            }
        }
    }

    private sealed class Writer(Stream s)
    {
        private readonly byte[] _buf = new byte[8];

        public void Flush() => s.Flush();
        public void WriteByte(byte b) => s.WriteByte(b);

        public void WriteShort(short v)
        {
            BinaryPrimitives.WriteInt16BigEndian(_buf, v);
            s.Write(_buf, 0, 2);
        }

        public void WriteInt(int v)
        {
            BinaryPrimitives.WriteInt32BigEndian(_buf, v);
            s.Write(_buf, 0, 4);
        }

        public void WriteLong(long v)
        {
            BinaryPrimitives.WriteInt64BigEndian(_buf, v);
            s.Write(_buf, 0, 8);
        }

        public void WriteString(string v)
        {
            var bytes = EncodeModifiedUtf8(v);
            WriteShort((short)(ushort)bytes.Length);
            s.Write(bytes);
        }

        public void WritePayload(NbtTag tag, int depth)
        {
            if (depth > MaxDepth)
                throw new InvalidDataException("The NBT data is nested too deeply.");
            switch (tag)
            {
                case NbtByte b: WriteByte((byte)b.Value); break;
                case NbtShort sh: WriteShort(sh.Value); break;
                case NbtInt i: WriteInt(i.Value); break;
                case NbtLong l: WriteLong(l.Value); break;
                case NbtFloat f: WriteInt(BitConverter.SingleToInt32Bits(f.Value)); break;
                case NbtDouble d: WriteLong(BitConverter.DoubleToInt64Bits(d.Value)); break;
                case NbtString str: WriteString(str.Value); break;
                case NbtByteArray ba:
                    WriteInt(ba.Value.Length);
                    s.Write(ba.Value.Span);
                    break;
                case NbtIntArray ia:
                    WriteInt(ia.Value.Length);
                    foreach (var v in ia.Value.Span)
                        WriteInt(v);
                    break;
                case NbtLongArray la:
                    WriteInt(la.Value.Length);
                    foreach (var v in la.Value.Span)
                        WriteLong(v);
                    break;
                case NbtList list:
                    WriteByte((byte)list.ElementType);
                    WriteInt(list.Count);
                    foreach (var item in list)
                        WritePayload(item, depth + 1);
                    break;
                case NbtCompound compound:
                    foreach (var (name, child) in compound)
                    {
                        WriteByte((byte)child.Type);
                        WriteString(name);
                        WritePayload(child, depth + 1);
                    }
                    WriteByte((byte)NbtTagType.End);
                    break;
                default:
                    throw new ArgumentException($"Unknown NBT tag {tag.GetType().Name}.", nameof(tag));
            }
        }
    }

    /// <summary>Replays bytes already consumed for format detection, then continues with the inner stream.</summary>
    private sealed class PrefixStream(byte[] prefix, Stream inner) : Stream
    {
        private int _pos;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_pos < prefix.Length)
            {
                var n = Math.Min(buffer.Length, prefix.Length - _pos);
                prefix.AsSpan(_pos, n).CopyTo(buffer);
                _pos += n;
                return n;
            }
            return inner.Read(buffer);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Lets an uncompressed write dispose its wrapper without closing the caller's stream.</summary>
    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    }
}
