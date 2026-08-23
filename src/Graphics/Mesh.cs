using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace UnnamedGame.Graphics;

[StructLayout(LayoutKind.Sequential)]
public struct Vertex(Vector3 position, Vector3 normal)
{
    public Vector3 Position = position;
    public Vector3 Normal = normal;
}

public sealed class Mesh : IDisposable
{
    public ID3D11Buffer VertexBuffer { get; }
    public ID3D11Buffer IndexBuffer { get; }
    public int IndexCount { get; }

    public Mesh(ID3D11Device device, Vertex[] vertices, ushort[] indices)
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
        var indices = new List<ushort>(36);

        foreach (var n in normals)
        {
            // Build an orthonormal basis around the face normal.
            var up = MathF.Abs(n.Y) > 0.9f ? new Vector3(0, 0, 1) : new Vector3(0, 1, 0);
            var tangent = Vector3.Normalize(Vector3.Cross(up, n));
            var bitangent = Vector3.Cross(n, tangent);
            var center = n * 0.5f;

            ushort b = (ushort)vertices.Count;
            vertices.Add(new Vertex(center - tangent * 0.5f - bitangent * 0.5f, n));
            vertices.Add(new Vertex(center - tangent * 0.5f + bitangent * 0.5f, n));
            vertices.Add(new Vertex(center + tangent * 0.5f + bitangent * 0.5f, n));
            vertices.Add(new Vertex(center + tangent * 0.5f - bitangent * 0.5f, n));
            indices.AddRange([b, (ushort)(b + 1), (ushort)(b + 2), b, (ushort)(b + 2), (ushort)(b + 3)]);
        }

        return new Mesh(device, [.. vertices], [.. indices]);
    }

    /// <summary>Unit-radius UV sphere.</summary>
    public static Mesh CreateSphere(ID3D11Device device, int slices = 20, int stacks = 12)
    {
        var vertices = new List<Vertex>();
        var indices = new List<ushort>();

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
                vertices.Add(new Vertex(n, n));
            }
        }

        for (int stack = 0; stack < stacks; stack++)
        {
            for (int slice = 0; slice < slices; slice++)
            {
                ushort a = (ushort)(stack * (slices + 1) + slice);
                ushort b = (ushort)(a + slices + 1);
                indices.AddRange([a, (ushort)(a + 1), b, (ushort)(a + 1), (ushort)(b + 1), b]);
            }
        }

        return new Mesh(device, [.. vertices], [.. indices]);
    }
}
