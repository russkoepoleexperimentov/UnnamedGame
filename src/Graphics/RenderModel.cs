using System.Numerics;
using UnnamedGame.Assets;
using Vortice.Direct3D11;

namespace UnnamedGame.Graphics;

/// <summary>GPU-side copy of a loaded model: one mesh per material, textures shared by path.</summary>
public sealed class RenderModel : IDisposable
{
    public sealed record Part(string NodeName, Mesh Mesh, Matrix4x4 Transform, Vector4 Color, Texture Texture);

    private readonly List<Mesh> _meshes = [];
    private readonly Dictionary<string, Texture> _textures = [];

    public List<Part> Parts { get; } = [];

    public static RenderModel Create(ID3D11Device device, ID3D11DeviceContext context, Model model)
    {
        var result = new RenderModel();

        foreach (var part in model.Parts)
        {
            var material = part.MaterialIndex >= 0 && part.MaterialIndex < model.Materials.Count
                ? model.Materials[part.MaterialIndex]
                : null;

            // Deferred shading has no blending, so glass would render as an opaque wall in
            // front of the interior — and the driver's view. Left out until there is a
            // forward transparency pass.
            if (material is { IsTransparent: true }) continue;
            if (part.Vertices.Length == 0 || part.Indices.Length == 0) continue;

            var mesh = new Mesh(device, part.Vertices, part.Indices);
            result._meshes.Add(mesh);

            Texture texture = null;
            if (material?.TexturePath is { } path)
            {
                if (!result._textures.TryGetValue(path, out texture))
                {
                    texture = Texture.Load(device, context, path);
                    result._textures[path] = texture;
                }
            }

            var colour = material is null ? Vector3.One : material.Diffuse;
            result.Parts.Add(new Part(part.NodeName, mesh, part.Transform, new Vector4(colour, 1f), texture));
        }

        return result;
    }

    public void Dispose()
    {
        foreach (var texture in _textures.Values) texture.Dispose();
        foreach (var mesh in _meshes) mesh.Dispose();
    }
}
