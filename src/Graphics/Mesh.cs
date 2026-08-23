using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace UnnamedGame.Graphics;

[StructLayout(LayoutKind.Sequential)]
public record struct Vertex(Vector3 Position, Vector3 Normal, Vector2 Uv)
{
    public Vertex(Vector3 position, Vector3 normal) : this(position, normal, Vector2.Zero) { }
}

public sealed class Mesh : IDisposable
{
    public ID3D11Buffer VertexBuffer { get; }
    public ID3D11Buffer IndexBuffer { get; }
    public int IndexCount { get; }

    public Mesh(ID3D11Device device, Vertex[] vertices, uint[] indices)
    {
        VertexBuffer = device.CreateBuffer(vertices, BindFlags.VertexBuffer);
        IndexBuffer = device.CreateBuffer(indices, BindFlags.IndexBuffer);
        IndexCount = indices.Length;
    }

    public void Dispose()
    {
        VertexBuffer.Dispose();
        IndexBuffer.Dispose();
    }

    /// <summary>Unit cube centred on the origin, one flat normal per face.</summary>
    public static Mesh CreateBox(ID3D11Device device)
    {
        Span<Vector3> normals =
        [
            new(0, 0, -1), new(0, 0, 1), new(-1, 0, 0),
            new(1, 0, 0), new(0, 1, 0), new(0, -1, 0),
        ];

        var vertices = new List<Vertex>(24);
        var indices = new List<uint>(36);

        foreach (var n in normals)
        {
            // Build an orthonormal basis around the face normal.
            var up = MathF.Abs(n.Y) > 0.9f ? new Vector3(0, 0, 1) : new Vector3(0, 1, 0);
            var tangent = Vector3.Normalize(Vector3.Cross(up, n));
            var bitangent = Vector3.Cross(n, tangent);
            var center = n * 0.5f;

            uint b = (uint)vertices.Count;
            vertices.Add(new Vertex(center - tangent * 0.5f - bitangent * 0.5f, n, new Vector2(0, 0)));
            vertices.Add(new Vertex(center - tangent * 0.5f + bitangent * 0.5f, n, new Vector2(0, 1)));
            vertices.Add(new Vertex(center + tangent * 0.5f + bitangent * 0.5f, n, new Vector2(1, 1)));
            vertices.Add(new Vertex(center + tangent * 0.5f - bitangent * 0.5f, n, new Vector2(1, 0)));
            indices.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
        }

        return new Mesh(device, [.. vertices], [.. indices]);
    }

    /// <summary>Unit-radius UV sphere.</summary>
    public static Mesh CreateSphere(ID3D11Device device, int slices = 20, int stacks = 12)
    {
        var vertices = new List<Vertex>();
        var indices = new List<uint>();

        for (int stack = 0; stack <= stacks; stack++)
        {
            float phi = MathF.PI * stack / stacks;
            for (int slice = 0; slice <= slices; slice++)
            {
                float theta = MathF.Tau * slice / slices;
                var n = new Vector3(
                    MathF.Sin(phi) * MathF.Cos(theta),
                    MathF.Cos(phi),
                    MathF.Sin(phi) * MathF.Sin(theta));
                vertices.Add(new Vertex(n, n, new Vector2((float)slice / slices, (float)stack / stacks)));
            }
        }

        for (int stack = 0; stack < stacks; stack++)
        {
            for (int slice = 0; slice < slices; slice++)
            {
                uint a = (uint)(stack * (slices + 1) + slice);
                uint b = a + (uint)slices + 1;
                indices.AddRange([a, a + 1, b, a + 1, b + 1, b]);
            }
        }

        return new Mesh(device, [.. vertices], [.. indices]);
    }
}
