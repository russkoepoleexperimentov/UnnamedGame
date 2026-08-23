using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace UnnamedGame.Assets;

/// <summary>One record in an FBX node tree: a name, a property list and nested records.</summary>
public sealed class FbxNode(string name)
{
    public string Name { get; } = name;
    public List<object> Properties { get; } = [];
    public List<FbxNode> Children { get; } = [];

    public FbxNode Child(string name)
    {
        foreach (var child in Children)
            if (child.Name == name)
                return child;
        return null;
    }

    public IEnumerable<FbxNode> ChildrenNamed(string name)
    {
        foreach (var child in Children)
            if (child.Name == name)
                yield return child;
    }

    public string StringProperty(int index) => index < Properties.Count ? Properties[index] as string : null;

    public long LongProperty(int index) => index < Properties.Count && Properties[index] is { } value
        ? Convert.ToInt64(value)
        : 0;

    /// <summary>Reads a "Properties70" entry, whose values start at property index 4.</summary>
    public double[] FindProperty70(string name)
    {
        var properties = Child("Properties70");
        if (properties is null) return null;

        foreach (var p in properties.ChildrenNamed("P"))
        {
            if (p.StringProperty(0) != name) continue;
            var values = new double[Math.Max(0, p.Properties.Count - 4)];
            for (int i = 0; i < values.Length; i++)
                values[i] = Convert.ToDouble(p.Properties[i + 4]);
            return values;
        }
        return null;
    }
}

/// <summary>
/// Reader for binary FBX (the "Kaydara FBX Binary" container, versions 7100-7700).
/// Only the container is parsed here; interpreting the scene is <see cref="ModelLoader"/>'s job.
/// </summary>
public static class FbxParser
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("Kaydara FBX Binary  ");

    public static FbxNode Parse(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 27 || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException($"{path} is not a binary FBX file (ASCII FBX is not supported).");

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(23));
        bool wide = version >= 7500;   // 7.5 switched the record offsets to 64-bit

        var root = new FbxNode("Root");
        int position = 27;
        while (position < data.Length - 16)
        {
            var node = ReadNode(data, ref position, wide);
            if (node is null) break;
            root.Children.Add(node);
        }
        return root;
    }

    private static FbxNode ReadNode(byte[] data, ref int position, bool wide)
    {
        long endOffset, propertyCount;
        if (wide)
        {
            endOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(position));
            propertyCount = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(position + 8));
            position += 24;   // end offset, property count, property list length
        }
        else
        {
            endOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
            propertyCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4));
            position += 12;
        }

        int nameLength = data[position++];
        string name = Encoding.UTF8.GetString(data, position, nameLength);
        position += nameLength;

        if (endOffset == 0) return null;   // the null record that terminates a nested list

        var node = new FbxNode(name);
        for (long i = 0; i < propertyCount; i++)
            node.Properties.Add(ReadProperty(data, ref position));

        // Anything left before the end offset is a nested list closed by a null record.
        int terminatorSize = wide ? 25 : 13;
        while (position < endOffset - terminatorSize)
        {
            var child = ReadNode(data, ref position, wide);
            if (child is null) break;
            node.Children.Add(child);
        }

        position = (int)endOffset;
        return node;
    }

    private static object ReadProperty(byte[] data, ref int position)
    {
        char type = (char)data[position++];
        switch (type)
        {
            case 'Y': { var v = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(position)); position += 2; return v; }
            case 'C': { var v = data[position] != 0; position += 1; return v; }
            case 'I': { var v = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position)); position += 4; return v; }
            case 'F': { var v = BitConverter.ToSingle(data, position); position += 4; return v; }
            case 'D': { var v = BitConverter.ToDouble(data, position); position += 8; return v; }
            case 'L': { var v = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(position)); position += 8; return v; }

            case 'S':
            case 'R':
            {
                int length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position));
                position += 4;
                object value = type == 'S'
                    ? Encoding.UTF8.GetString(data, position, length)
                    : data.AsSpan(position, length).ToArray();
                position += length;
                return value;
            }

            case 'f': return ReadArray<float>(data, ref position, 4);
            case 'd': return ReadArray<double>(data, ref position, 8);
            case 'i': return ReadArray<int>(data, ref position, 4);
            case 'l': return ReadArray<long>(data, ref position, 8);
            case 'b': return ReadArray<byte>(data, ref position, 1);

            default:
                throw new InvalidDataException($"Unknown FBX property type '{type}' at offset {position - 1}.");
        }
    }

    /// <summary>Array properties are optionally deflate-compressed as a zlib stream.</summary>
    private static T[] ReadArray<T>(byte[] data, ref int position, int elementSize) where T : unmanaged
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position));
        uint encoding = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4));
        int compressedLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position + 8));
        position += 12;

        var result = new T[count];
        var destination = System.Runtime.InteropServices.MemoryMarshal.AsBytes(result.AsSpan());

        if (encoding == 0)
        {
            data.AsSpan(position, count * elementSize).CopyTo(destination);
        }
        else
        {
            using var source = new MemoryStream(data, position, compressedLength);
            using var inflate = new ZLibStream(source, CompressionMode.Decompress);
            int read = 0;
            while (read < destination.Length)
            {
                int chunk = inflate.Read(destination[read..]);
                if (chunk == 0) break;
                read += chunk;
            }
        }

        position += compressedLength;
        return result;
    }
}
