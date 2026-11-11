using System.Buffers.Binary;
using System.Text;

namespace PrintAgent.Linux;

public sealed record IppAttribute(byte Tag, string Name, IReadOnlyList<byte[]> Values)
{
    public static IppAttribute Text(byte tag, string name, params string[] values) =>
        new(tag, name, values.Select(Encoding.UTF8.GetBytes).ToArray());
}

public sealed class IppGroup(byte tag)
{
    public byte Tag { get; } = tag;
    public Dictionary<string, List<object?>> Attributes { get; } = new(StringComparer.Ordinal);
    public string? Text(string name) => Values(name).OfType<string>().FirstOrDefault();
    public int? Integer(string name) => Values(name).OfType<int>().Cast<int?>().FirstOrDefault();
    public bool? Boolean(string name) => Values(name).OfType<bool>().Cast<bool?>().FirstOrDefault();
    public IEnumerable<object?> Values(string name) => Attributes.TryGetValue(name, out var values) ? values : [];
    internal void Add(string name, object? value)
    {
        if (!Attributes.TryGetValue(name, out var values)) Attributes[name] = values = [];
        values.Add(value);
    }
}

public sealed record IppResponse(ushort Status, int RequestId, IReadOnlyList<IppGroup> Groups);
public sealed record IppRange(int Lower, int Upper);
public sealed record IppResolution(int X, int Y, byte Units);

public static class IppCodec
{
    public static byte[] Encode(ushort operation, int requestId, IEnumerable<IppAttribute> attributes)
    {
        using var stream = new MemoryStream();
        stream.Write([2, 0]);
        WriteShort(stream, operation);
        Span<byte> id = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(id, requestId);
        stream.Write(id);
        stream.WriteByte(1);
        Write(stream, IppAttribute.Text(0x47, "attributes-charset", "utf-8"));
        Write(stream, IppAttribute.Text(0x48, "attributes-natural-language", "en"));
        foreach (var attribute in attributes) Write(stream, attribute);
        stream.WriteByte(3);
        return stream.ToArray();
    }

    private static void Write(Stream stream, IppAttribute attribute)
    {
        var first = true;
        foreach (var value in attribute.Values)
        {
            stream.WriteByte(attribute.Tag);
            var name = first ? Encoding.UTF8.GetBytes(attribute.Name) : [];
            WriteShort(stream, checked((ushort)name.Length));
            stream.Write(name);
            WriteShort(stream, checked((ushort)value.Length));
            stream.Write(value);
            first = false;
        }
    }

    private static void WriteShort(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    public static IppResponse Decode(byte[] bytes)
    {
        var reader = new Reader(bytes);
        var version = reader.Take(2);
        if (version[0] is not (1 or 2)) throw new InvalidDataException("Unsupported IPP version");
        var status = reader.Short();
        var id = BinaryPrimitives.ReadInt32BigEndian(reader.Take(4));
        var groups = new List<IppGroup>();
        IppGroup? group = null;
        string? previousName = null;
        while (true)
        {
            var tag = reader.Byte();
            if (tag == 3) return new(status, id, groups);
            if (tag < 0x10)
            {
                group = new IppGroup(tag);
                groups.Add(group);
                previousName = null;
                continue;
            }
            if (group == null) throw new InvalidDataException("IPP value without group");
            var name = Encoding.UTF8.GetString(reader.Take(reader.Short()));
            if (name.Length == 0) name = previousName ?? throw new InvalidDataException("IPP continuation without name");
            previousName = name;
            var raw = reader.Take(reader.Short());
            group.Add(name, ReadValue(tag, raw, reader, 0));
        }
    }

    private static object? ReadValue(byte tag, byte[] raw, Reader reader, int depth) => tag switch
    {
        0x21 or 0x23 when raw.Length == 4 => BinaryPrimitives.ReadInt32BigEndian(raw),
        0x22 when raw.Length == 1 => raw[0] != 0,
        0x33 when raw.Length == 8 => new IppRange(BinaryPrimitives.ReadInt32BigEndian(raw), BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(4))),
        0x32 when raw.Length == 9 => new IppResolution(BinaryPrimitives.ReadInt32BigEndian(raw), BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(4)), raw[8]),
        0x34 => ReadCollection(reader, depth + 1),
        >= 0x41 and <= 0x49 => Encoding.UTF8.GetString(raw),
        >= 0x10 and <= 0x1f => null, // unknown/no-value/unsupported are not fabricated values.
        _ => raw
    };

    private static IppGroup ReadCollection(Reader reader, int depth)
    {
        if (depth > 32) throw new InvalidDataException("IPP collection nesting too deep");
        var collection = new IppGroup(0x34);
        string? member = null;
        while (true)
        {
            var tag = reader.Byte();
            _ = reader.Take(reader.Short());
            var raw = reader.Take(reader.Short());
            if (tag == 0x37) return collection;
            if (tag == 0x4a) { member = Encoding.UTF8.GetString(raw); continue; }
            if (member == null) throw new InvalidDataException("IPP collection value without member");
            collection.Add(member, ReadValue(tag, raw, reader, depth));
        }
    }

    private sealed class Reader(byte[] data)
    {
        private int _position;
        public byte Byte() => Take(1)[0];
        public ushort Short() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
        public byte[] Take(int count)
        {
            if (count > data.Length - _position) throw new InvalidDataException("Truncated IPP response");
            var value = data.AsSpan(_position, count).ToArray();
            _position += count;
            return value;
        }
    }
}
