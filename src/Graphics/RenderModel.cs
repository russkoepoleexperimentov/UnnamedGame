using System.Numerics;
using UnnamedGame.Assets;
using Vortice.Direct3D11;

namespace UnnamedGame.Graphics;

/// <summary>GPU-side copy of a loaded model: one mesh per material, textures shared by path.</summary>
public sealed class RenderModel : IDisposable
{
    /// <param name="Color">RGB is the material colour; A is its opacity (1 for solid parts).</param>
    /// <param name="IsGlass">Drawn by the forward transparency pass instead of the G-buffer.</param>
    public sealed record Part(string NodeName, Mesh Mesh, Matrix4x4 Transform, Vector4 Color, Texture Texture, bool IsGlass);

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

            if (part.Vertices.Length == 0 || part.Indices.Length == 0) continue;
            bool glass = material is { IsTransparent: true };

            var mesh = new Mesh(device, part.Vertices, part.Indices);
            result._meshes.Add(mesh);

            // Glass takes its transparency map; everything else takes its albedo map.
            string path = glass ? material.AlphaTexturePath : material?.TexturePath;
            Texture texture = null;
            if (path is not null && !result._textures.TryGetValue(path, out texture))
            {
                texture = Texture.Load(device, context, path);
                result._textures[path] = texture;
            }

            var colour = material is null ? Vector3.One : material.Diffuse;
            float opacity = material?.Opacity ?? 1f;
            result.Parts.Add(new Part(part.NodeName, mesh, part.Transform, new Vector4(colour, opacity), texture, glass));
        }

        return result;
    }

    public void Dispose()
    {
        foreach (var texture in _textures.Values) texture.Dispose();
        foreach (var mesh in _meshes) mesh.Dispose();
    }
}
