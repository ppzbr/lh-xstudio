using System.Text;

namespace VocalPatch.XStudio;

/// <summary>
/// Protocol-buffers wire types, as they appear in a tag's low three bits.
/// </summary>
internal static class WireType
{
    public const int Varint = 0;
    public const int Fixed64 = 1;
    public const int LengthDelimited = 2;
    public const int StartGroup = 3;
    public const int EndGroup = 4;
    public const int Fixed32 = 5;
}

/// <summary>
/// A minimal proto3 wire reader: tags, varints, fixed widths and length-delimited slices.
///
/// Deliberately hand-rolled rather than taking a <c>Google.Protobuf</c> dependency. The
/// point of this codec is to read a format whose schema lives in someone else's binary;
/// the generated-code route would need the <c>.proto</c> file and a code generator in the
/// build, and it would not tolerate the unknown fields a newer editor version adds. Here
/// unknown fields are skipped by wire type, which is exactly proto3's own rule.
///
/// No <see cref="Span{T}"/>: this library targets netstandard2.0, where it would pull in
/// System.Memory for no benefit at these file sizes.
/// </summary>
internal sealed class ProtoReader
{
    private readonly byte[] _buf;
    private readonly int _end;
    private int _pos;

    public ProtoReader(byte[] buffer, int start = 0, int? end = null)
    {
        _buf = buffer;
        _pos = start;
        _end = end ?? buffer.Length;
        if (_pos < 0 || _end > buffer.Length || _pos > _end)
            throw new FormatException("Truncated protobuf message.");
    }

    public long Remaining => _end - _pos;

    /// <summary>The next tag, or false at the end of this message.</summary>
    public bool TryReadTag(out int field, out int wireType)
    {
        if (_pos >= _end)
        {
            field = 0;
            wireType = 0;
            return false;
        }
        ulong tag = ReadVarint();
        field = (int)(tag >> 3);
        wireType = (int)(tag & 7);
        if (field == 0) throw new FormatException("Field number 0 is not valid in a protobuf stream.");
        return true;
    }

    public ulong ReadVarint()
    {
        ulong value = 0;
        int shift = 0;
        while (true)
        {
            if (_pos >= _end) throw new FormatException("Truncated varint.");
            byte b = _buf[_pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
            shift += 7;
            if (shift > 63) throw new FormatException("Varint longer than 10 bytes.");
        }
    }

    /// <summary>proto3 <c>int32</c>: a negative value arrives sign-extended to 64 bits.</summary>
    public int ReadInt32() => unchecked((int)ReadVarint());

    /// <summary>proto3 <c>sint32</c>: zig-zag encoded.</summary>
    public int ReadSInt32()
    {
        uint v = unchecked((uint)ReadVarint());
        return unchecked((int)(v >> 1) ^ -(int)(v & 1));
    }

    public bool ReadBool() => ReadVarint() != 0;

    public float ReadFloat()
    {
        Need(4);
        float f = BitConverter.ToSingle(_buf, _pos);
        _pos += 4;
        return f;
    }

    public double ReadDouble()
    {
        Need(8);
        double d = BitConverter.ToDouble(_buf, _pos);
        _pos += 8;
        return d;
    }

    public string ReadString()
    {
        int len = ReadLength();
        Need(len);
        string s = Encoding.UTF8.GetString(_buf, _pos, len);
        _pos += len;
        return s;
    }

    public byte[] ReadBytes()
    {
        int len = ReadLength();
        Need(len);
        var slice = new byte[len];
        Array.Copy(_buf, _pos, slice, 0, len);
        _pos += len;
        return slice;
    }

    /// <summary>A nested message: a reader bounded to this field's slice, sharing the buffer.</summary>
    public ProtoReader ReadMessage()
    {
        int len = ReadLength();
        Need(len);
        var sub = new ProtoReader(_buf, _pos, _pos + len);
        _pos += len;
        return sub;
    }

    public void SkipField(int wireType)
    {
        switch (wireType)
        {
            case WireType.Varint:
                ReadVarint();
                break;
            case WireType.Fixed64:
                Need(8);
                _pos += 8;
                break;
            case WireType.LengthDelimited:
                int len = ReadLength();
                Need(len);
                _pos += len;
                break;
            case WireType.Fixed32:
                Need(4);
                _pos += 4;
                break;
            // Groups (3/4) were removed in proto3 and no descriptor in this application
            // uses them; treating one as fatal is better than silently mis-parsing.
            default:
                throw new FormatException($"Unsupported wire type {wireType}.");
        }
    }

    private int ReadLength()
    {
        ulong len = ReadVarint();
        if (len > (ulong)Remaining) throw new FormatException("Length-delimited field runs past the end of the message.");
        return (int)len;
    }

    private void Need(int count)
    {
        if (_pos + count > _end) throw new FormatException("Truncated field.");
    }
}

/// <summary>
/// A minimal proto3 wire writer. Zero and empty values are omitted, which is what makes a
/// protobuf file small and what the editor itself does: a fresh X Studio project leaves
/// every field that equals its default out of the file entirely.
/// </summary>
internal sealed class ProtoWriter
{
    private readonly MemoryStream _ms = new();

    public byte[] ToArray() => _ms.ToArray();

    public void WriteInt32(int field, int value)
    {
        if (value == 0) return;
        WriteTag(field, WireType.Varint);
        // Negative int32 values are sign-extended to 64 bits on the wire.
        WriteVarint(unchecked((ulong)(long)value));
    }

    public void WriteSInt32(int field, int value)
    {
        if (value == 0) return;
        WriteTag(field, WireType.Varint);
        WriteVarint(unchecked((uint)((value << 1) ^ (value >> 31))));
    }

    public void WriteBool(int field, bool value)
    {
        if (!value) return;
        WriteTag(field, WireType.Varint);
        WriteVarint(1);
    }

    /// <summary>Little-endian, as protobuf's fixed32 requires (and as every .NET host is).</summary>
    public void WriteFloat(int field, float value)
    {
        if (value == 0f) return;
        WriteTag(field, WireType.Fixed32);
        var b = BitConverter.GetBytes(value);
        _ms.Write(b, 0, b.Length);
    }

    public void WriteString(int field, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        WriteTag(field, WireType.LengthDelimited);
        var b = Encoding.UTF8.GetBytes(value);
        WriteVarint((ulong)b.Length);
        _ms.Write(b, 0, b.Length);
    }

    public void WriteMessage(int field, Action<ProtoWriter> body)
    {
        var inner = new ProtoWriter();
        body(inner);
        var b = inner.ToArray();
        WriteTag(field, WireType.LengthDelimited);
        WriteVarint((ulong)b.Length);
        _ms.Write(b, 0, b.Length);
    }

    private void WriteTag(int field, int wireType) => WriteVarint((ulong)(((long)field << 3) | (uint)wireType));

    private void WriteVarint(ulong value)
    {
        while (value >= 0x80)
        {
            _ms.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        _ms.WriteByte((byte)value);
    }
}
