using System.Numerics;
using UnnamedGame.Graphics;

namespace UnnamedGame.Assets;

public sealed class ModelMaterial
{
    public string Name = "";
    public Vector3 Diffuse = new(0.8f);
    public string TexturePath;      // resolved absolute path, or null for a flat colour
    public bool IsTransparent;      // glass: skipped by the deferred geometry pass
}

/// <summary>One mesh with one material, in node-local space plus that node's transform.</summary>
public sealed class ModelPart
{
    public string NodeName = "";
    public Matrix4x4 Transform = Matrix4x4.Identity;
    public Vertex[] Vertices = [];
    public uint[] Indices = [];
    public int MaterialIndex;
}

public sealed class Model
{
    public List<ModelPart> Parts { get; } = [];
    public List<ModelMaterial> Materials { get; } = [];
    public Vector3 BoundsMin { get; set; }
    public Vector3 BoundsMax { get; set; }
    public Vector3 BoundsSize => BoundsMax - BoundsMin;
}

/// <summary>
/// Turns a parsed FBX tree into meshes grouped by material. Handles the parts of the format
/// this model actually uses: polygon meshes, per-polygon-vertex normals and UVs, per-polygon
/// material assignment, and node transforms (including the geometric offset).
/// </summary>
public static class ModelLoader
{
    /// <param name="unitScale">Multiplied into every node transform; this model is authored in centimetres.</param>
    public static Model Load(string fbxPath, string textureDirectory, float unitScale = 0.01f)
    {
        var root = FbxParser.Parse(fbxPath);
        var objects = root.Child("Objects") ?? throw new InvalidDataException("FBX has no Objects section.");
        var connections = root.Child("Connections");

        var geometries = new Dictionary<long, FbxNode>();
        var modelNodes = new Dictionary<long, FbxNode>();
        var materialNodes = new Dictionary<long, FbxNode>();
        var textureNodes = new Dictionary<long, FbxNode>();

        foreach (var node in objects.Children)
        {
            long id = node.LongProperty(0);
            switch (node.Name)
            {
                case "Geometry": geometries[id] = node; break;
                case "Model": modelNodes[id] = node; break;
                case "Material": materialNodes[id] = node; break;
                case "Texture": textureNodes[id] = node; break;
            }
        }

        // parent id -> [(child id, property name for OP links)]
        var childrenOf = new Dictionary<long, List<(long Child, string Property)>>();
        if (connections is not null)
        {
            foreach (var c in connections.ChildrenNamed("C"))
            {
                long child = c.LongProperty(1);
                long parent = c.LongProperty(2);
                string property = c.StringProperty(3);
                if (!childrenOf.TryGetValue(parent, out var list))
                    childrenOf[parent] = list = [];
                list.Add((child, property));
            }
        }

        var model = new Model();
        var materialIndices = new Dictionary<long, int>();

        foreach (var (id, node) in materialNodes)
        {
            materialIndices[id] = model.Materials.Count;
            model.Materials.Add(BuildMaterial(id, node, childrenOf, textureNodes, textureDirectory));
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var (id, node) in modelNodes)
        {
            if (node.StringProperty(2) != "Mesh") continue;
            if (!childrenOf.TryGetValue(id, out var links)) continue;

            var geometry = links.Select(l => l.Child).FirstOrDefault(geometries.ContainsKey);
            if (geometry == 0 || !geometries.TryGetValue(geometry, out var geometryNode)) continue;

            // Material slots are indexed by the order they are connected to this model.
            var slots = links.Where(l => materialNodes.ContainsKey(l.Child))
                             .Select(l => materialIndices[l.Child])
                             .ToArray();

            var transform = BuildTransform(node, modelNodes, childrenOf, unitScale);
            string name = CleanName(node.StringProperty(1));

            foreach (var part in BuildParts(geometryNode, slots))
            {
                part.NodeName = name;
                part.Transform = transform;
                model.Parts.Add(part);

                foreach (var vertex in part.Vertices)
                {
                    var world = Vector3.Transform(vertex.Position, transform);
                    min = Vector3.Min(min, world);
                    max = Vector3.Max(max, world);
                }
            }
        }

        model.BoundsMin = min;
        model.BoundsMax = max;
        return model;
    }

    private static string CleanName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        // FBX object names are "name\0\1ClassName".
        int separator = raw.IndexOf('\0');
        return separator < 0 ? raw : raw[..separator];
    }

    private static ModelMaterial BuildMaterial(long id, FbxNode node,
        Dictionary<long, List<(long Child, string Property)>> childrenOf,
        Dictionary<long, FbxNode> textureNodes, string textureDirectory)
    {
        var material = new ModelMaterial { Name = CleanName(node.StringProperty(1)) };

        var diffuse = node.FindProperty70("DiffuseColor") ?? node.FindProperty70("Diffuse");
        if (diffuse is { Length: >= 3 })
            material.Diffuse = new Vector3((float)diffuse[0], (float)diffuse[1], (float)diffuse[2]);

        if (childrenOf.TryGetValue(id, out var links))
        {
            foreach (var (child, property) in links)
            {
                if (!textureNodes.TryGetValue(child, out var texture)) continue;

                string file = texture.Child("RelativeFilename")?.StringProperty(0)
                              ?? texture.Child("FileName")?.StringProperty(0);
                if (string.IsNullOrEmpty(file)) continue;

                // Paths in the file point at the artist's machine; only the file name is usable.
                string resolved = Path.Combine(textureDirectory, Path.GetFileName(file.Replace('\\', '/')));
                if (!File.Exists(resolved)) continue;

                if (property is "DiffuseColor" or "Maya|baseColor")
                    material.TexturePath = resolved;
                else if (property is "TransparencyFactor" or "TransparentColor")
                    material.IsTransparent = true;
            }
        }

        if (material.Name.Contains("glass", StringComparison.OrdinalIgnoreCase))
            material.IsTransparent = true;

        return material;
    }

    /// <summary>Local transform: geometric offset, then the node's own TRS, then any parents.</summary>
    private static Matrix4x4 BuildTransform(FbxNode node, Dictionary<long, FbxNode> modelNodes,
        Dictionary<long, List<(long Child, string Property)>> childrenOf, float unitScale)
    {
        var transform = NodeMatrix(node, geometric: true) * NodeMatrix(node, geometric: false);

        // Walk up the parent chain (this model is flat, but nested rigs are cheap to support).
        long current = node.LongProperty(0);
        for (int depth = 0; depth < 16; depth++)
        {
            long parent = 0;
            foreach (var (id, candidates) in childrenOf)
            {
                if (id == 0 || !modelNodes.ContainsKey(id)) continue;
                if (candidates.Any(c => c.Child == current)) { parent = id; break; }
            }
            if (parent == 0) break;

            transform *= NodeMatrix(modelNodes[parent], geometric: false);
            current = parent;
        }

        return transform * Matrix4x4.CreateScale(unitScale);
    }

    private static Matrix4x4 NodeMatrix(FbxNode node, bool geometric)
    {
        var translation = node.FindProperty70(geometric ? "GeometricTranslation" : "Lcl Translation");
        var rotation = node.FindProperty70(geometric ? "GeometricRotation" : "Lcl Rotation");
        var scale = node.FindProperty70(geometric ? "GeometricScaling" : "Lcl Scaling");

        var s = scale is { Length: >= 3 } ? new Vector3((float)scale[0], (float)scale[1], (float)scale[2]) : Vector3.One;
        var t = translation is { Length: >= 3 }
            ? new Vector3((float)translation[0], (float)translation[1], (float)translation[2])
            : Vector3.Zero;

        float rx = 0, ry = 0, rz = 0;
        if (rotation is { Length: >= 3 })
        {
            const float toRadians = MathF.PI / 180f;
            rx = (float)rotation[0] * toRadians;
            ry = (float)rotation[1] * toRadians;
            rz = (float)rotation[2] * toRadians;
        }

        // FBX default euler order is XYZ applied to a column vector (R = Rz*Ry*Rx), which is
        // this order once transposed for System.Numerics' row-vector convention.
        return Matrix4x4.CreateScale(s)
            * Matrix4x4.CreateRotationX(rx)
            * Matrix4x4.CreateRotationY(ry)
            * Matrix4x4.CreateRotationZ(rz)
            * Matrix4x4.CreateTranslation(t);
    }

    private static IEnumerable<ModelPart> BuildParts(FbxNode geometry, int[] materialSlots)
    {
        var positions = geometry.Child("Vertices")?.Properties[0] as double[];
        var polygonIndices = geometry.Child("PolygonVertexIndex")?.Properties[0] as int[];
        if (positions is null || polygonIndices is null) yield break;

        var (normals, normalIndices, normalMapping, normalReference) = ReadLayer(geometry, "LayerElementNormal", "Normals");
        var (uvs, uvIndices, uvMapping, uvReference) = ReadLayer(geometry, "LayerElementUV", "UV");

        int[] materials = null;
        string materialMapping = "AllSame";
        var materialLayer = geometry.Child("LayerElementMaterial");
        if (materialLayer is not null)
        {
            materials = materialLayer.Child("Materials")?.Properties[0] as int[];
            materialMapping = materialLayer.Child("MappingInformationType")?.StringProperty(0) ?? "AllSame";
        }

        // One builder per material slot; most parts only touch a handful.
        var builders = new Dictionary<int, PartBuilder>();
        int polygon = 0;
        int polygonStart = 0;

        for (int i = 0; i < polygonIndices.Length; i++)
        {
            bool last = polygonIndices[i] < 0;
            if (!last) continue;

            int slot = 0;
            if (materials is { Length: > 0 })
                slot = materialMapping == "AllSame" ? materials[0] : materials[Math.Min(polygon, materials.Length - 1)];

            int materialIndex = slot >= 0 && slot < materialSlots.Length ? materialSlots[slot] : -1;
            if (!builders.TryGetValue(materialIndex, out var builder))
                builders[materialIndex] = builder = new PartBuilder();

            // Triangle fan over the polygon's corners. The last two are swapped: FBX winds
            // its polygons the opposite way round from the meshes generated in Mesh.cs, and
            // the rasterizer culls by winding, so without this the car shows its back faces.
            for (int corner = polygonStart + 2; corner <= i; corner++)
            {
                builder.Add(Corner(polygonStart), Corner(corner), Corner(corner - 1));
            }

            polygon++;
            polygonStart = i + 1;

            Vertex Corner(int pv)
            {
                int vertexIndex = polygonIndices[pv];
                if (vertexIndex < 0) vertexIndex = ~vertexIndex;

                var position = new Vector3(
                    (float)positions[vertexIndex * 3],
                    (float)positions[vertexIndex * 3 + 1],
                    (float)positions[vertexIndex * 3 + 2]);

                var normal = Vector3.UnitY;
                if (normals is not null)
                {
                    int index = ResolveIndex(pv, vertexIndex, normalMapping, normalReference, normalIndices);
                    if (index >= 0 && (index + 1) * 3 <= normals.Length)
                        normal = new Vector3((float)normals[index * 3], (float)normals[index * 3 + 1], (float)normals[index * 3 + 2]);
                }

                var uv = Vector2.Zero;
                if (uvs is not null)
                {
                    int index = ResolveIndex(pv, vertexIndex, uvMapping, uvReference, uvIndices);
                    if (index >= 0 && (index + 1) * 2 <= uvs.Length)
                        uv = new Vector2((float)uvs[index * 2], 1f - (float)uvs[index * 2 + 1]);   // FBX UVs are bottom-up
                }

                return new Vertex(position, normal, uv);
            }
        }

        foreach (var (materialIndex, builder) in builders)
        {
            if (builder.Indices.Count == 0) continue;
            yield return new ModelPart
            {
                MaterialIndex = materialIndex,
                Vertices = [.. builder.Vertices],
                Indices = [.. builder.Indices],
            };
        }
    }

    private static int ResolveIndex(int polygonVertex, int vertexIndex, string mapping, string reference, int[] indices)
    {
        int direct = mapping.StartsWith("ByVert", StringComparison.Ordinal) ? vertexIndex : polygonVertex;
        if (reference == "IndexToDirect" && indices is not null)
            return direct < indices.Length ? indices[direct] : -1;
        return direct;
    }

    private static (double[] Data, int[] Indices, string Mapping, string Reference) ReadLayer(
        FbxNode geometry, string layerName, string dataName)
    {
        var layer = geometry.Child(layerName);
        if (layer is null) return (null, null, "ByPolygonVertex", "Direct");

        var data = layer.Child(dataName)?.Properties[0] as double[];
        var indices = layer.Child(dataName + "Index")?.Properties[0] as int[];
        string mapping = layer.Child("MappingInformationType")?.StringProperty(0) ?? "ByPolygonVertex";
        string reference = layer.Child("ReferenceInformationType")?.StringProperty(0) ?? "Direct";
        return (data, indices, mapping, reference);
    }

    /// <summary>Welds identical corners so the buffers stay close to the source vertex count.</summary>
    private sealed class PartBuilder
    {
        public readonly List<Vertex> Vertices = [];
        public readonly List<uint> Indices = [];
        private readonly Dictionary<Vertex, uint> _lookup = [];

        public void Add(Vertex a, Vertex b, Vertex c)
        {
            Indices.Add(Intern(a));
            Indices.Add(Intern(b));
            Indices.Add(Intern(c));
        }

        private uint Intern(Vertex vertex)
        {
            if (_lookup.TryGetValue(vertex, out uint index)) return index;
            index = (uint)Vertices.Count;
            Vertices.Add(vertex);
            _lookup[vertex] = index;
            return index;
        }
    }
}
