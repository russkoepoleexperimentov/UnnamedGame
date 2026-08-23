using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnnamedGame.Platform;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace UnnamedGame.Graphics;

[StructLayout(LayoutKind.Sequential)]
internal struct PerPassData
{
    public Matrix4x4 ViewProjection;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PerObjectData
{
    public Matrix4x4 World;
    public Vector4 Color;
    public Vector3 TexScale;
    public float Checker;
}

[InlineArray(Shaders.MaxPointLights)]
internal struct LightArray
{
    private Vector4 _element;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LightingData
{
    public Matrix4x4 InverseViewProjection;
    public Matrix4x4 SunViewProjection;
    public Matrix4x4 SpotViewProjection;

    public Vector3 CameraPosition; public float PointLightCount;
    public Vector3 SunDirection; public float SunIntensity;
    public Vector3 SunColor; public float SunTexelSize;

    public Vector3 SpotPosition; public float SpotRange;
    public Vector3 SpotDirection; public float SpotInnerCos;
    public Vector3 SpotColor; public float SpotOuterCos;
    public float SpotEnabled; public float SpotIntensity; public float SpotTexelSize; public float Pad;

    public LightArray PointPositionRange;
    public LightArray PointColorIntensity;
}

/// <summary>
/// Deferred renderer: geometry writes albedo + world normal + depth, then a single full-screen
/// pass resolves the sun (shadow mapped), the flashlight (shadow mapped) and the point lights.
/// </summary>
public sealed class Renderer : IDisposable
{
    private const int SunShadowResolution = 2048;
    private const int SpotShadowResolution = 1024;
    private const float SunShadowExtent = 32f;   // half-size of the ortho box around the level

    private static readonly Vector3 SunDirection = Vector3.Normalize(new Vector3(-0.55f, -0.52f, -0.4f));
    private static readonly Vector3 SunColor = new(0.95f, 0.82f, 0.72f);
    private const float SunIntensity = 0.5f;   // dusk: the dynamic lights are meant to carry the scene

    private readonly GameWindow _window;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGISwapChain1 _swapChain;

    private readonly ID3D11VertexShader _gbufferVS;
    private readonly ID3D11PixelShader _gbufferPS;
    private readonly ID3D11VertexShader _shadowVS;
    private readonly ID3D11VertexShader _lightingVS;
    private readonly ID3D11PixelShader _lightingPS;
    private readonly ID3D11VertexShader _unlitVS;
    private readonly ID3D11PixelShader _unlitPS;
    private readonly ID3D11InputLayout _inputLayout;

    private readonly ID3D11Buffer _perPass;
    private readonly ID3D11Buffer _perObject;
    private readonly ID3D11Buffer _lighting;

    private readonly ID3D11RasterizerState _rasterizer;
    private readonly ID3D11RasterizerState _shadowRasterizer;
    private readonly ID3D11DepthStencilState _depthState;
    private readonly ID3D11DepthStencilState _noDepthState;
    private readonly ID3D11SamplerState _shadowSampler;
    private readonly ID3D11SamplerState _albedoSampler;

    private readonly ID3D11Texture2D _sunShadowTexture;
    private readonly ID3D11DepthStencilView _sunShadowView;
    private readonly ID3D11ShaderResourceView _sunShadowSrv;
    private readonly ID3D11Texture2D _spotShadowTexture;
    private readonly ID3D11DepthStencilView _spotShadowView;
    private readonly ID3D11ShaderResourceView _spotShadowSrv;

    private ID3D11RenderTargetView _backBufferView;
    private ID3D11Texture2D _albedoTexture, _normalTexture, _depthTexture;
    private ID3D11RenderTargetView _albedoView, _normalView;
    private ID3D11ShaderResourceView _albedoSrv, _normalSrv, _depthSrv;
    private ID3D11DepthStencilView _depthView;

    public Mesh BoxMesh { get; }
    public Mesh SphereMesh { get; }
    public float AspectRatio => _window.Height == 0 ? 1f : (float)_window.Width / _window.Height;

    public Renderer(GameWindow window)
    {
        _window = window;

        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels,
            out var device, out _, out var context);
        if (result.Failure)
        {
            // No hardware device (headless / RDP session): fall back to WARP.
            D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels,
                out device, out _, out context).CheckError();
        }
        _device = device;
        _context = context;

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        _swapChain = factory.CreateSwapChainForHwnd(_device, window.Handle, new SwapChainDescription1
        {
            Width = (uint)window.Width,
            Height = (uint)window.Height,
            Format = Format.R8G8B8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            Scaling = Scaling.Stretch,
        });
        factory.MakeWindowAssociation(window.Handle, WindowAssociationFlags.IgnoreAltEnter);

        CreateSizeDependentResources();

        var gbufferVSBlob = Compile(Shaders.GBuffer, "VSMain", "vs_5_0");
        _gbufferVS = _device.CreateVertexShader(gbufferVSBlob.AsSpan());
        using (var blob = Compile(Shaders.GBuffer, "PSMain", "ps_5_0")) _gbufferPS = _device.CreatePixelShader(blob.AsSpan());
        using (var blob = Compile(Shaders.Shadow, "VSMain", "vs_5_0")) _shadowVS = _device.CreateVertexShader(blob.AsSpan());
        using (var blob = Compile(Shaders.Lighting, "VSMain", "vs_5_0")) _lightingVS = _device.CreateVertexShader(blob.AsSpan());
        using (var blob = Compile(Shaders.Lighting, "PSMain", "ps_5_0")) _lightingPS = _device.CreatePixelShader(blob.AsSpan());
        using (var blob = Compile(Shaders.Unlit, "VSMain", "vs_5_0")) _unlitVS = _device.CreateVertexShader(blob.AsSpan());
        using (var blob = Compile(Shaders.Unlit, "PSMain", "ps_5_0")) _unlitPS = _device.CreatePixelShader(blob.AsSpan());

        InputElementDescription[] elements =
        [
            new("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
        ];
        _inputLayout = _device.CreateInputLayout(elements, gbufferVSBlob.AsSpan());
        gbufferVSBlob.Dispose();

        _perPass = CreateConstantBuffer<PerPassData>();
        _perObject = CreateConstantBuffer<PerObjectData>();
        _lighting = CreateConstantBuffer<LightingData>();

        _rasterizer = _device.CreateRasterizerState(new RasterizerDescription
        {
            CullMode = CullMode.Back,
            FillMode = FillMode.Solid,
            DepthClipEnable = true,
        });
        // Front-face culling plus a slope-scaled bias: two independent ways of pushing
        // shadow-map depth away from the lit surface, which together kill most acne.
        _shadowRasterizer = _device.CreateRasterizerState(new RasterizerDescription
        {
            CullMode = CullMode.Front,
            FillMode = FillMode.Solid,
            DepthClipEnable = true,
            DepthBias = 1200,
            SlopeScaledDepthBias = 2.5f,
            DepthBiasClamp = 0.01f,
        });
        _depthState = _device.CreateDepthStencilState(
            new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.LessEqual));
        _noDepthState = _device.CreateDepthStencilState(
            new DepthStencilDescription(false, DepthWriteMask.Zero, ComparisonFunction.Always));

        _shadowSampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.ComparisonMinMagLinearMipPoint,
            AddressU = TextureAddressMode.Border,
            AddressV = TextureAddressMode.Border,
            AddressW = TextureAddressMode.Border,
            BorderColor = new Color4(1f, 1f, 1f, 1f),   // outside the map = fully lit
            ComparisonFunc = ComparisonFunction.LessEqual,
            MaxAnisotropy = 1,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });

        _albedoSampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.Anisotropic,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            MaxAnisotropy = 8,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });

        (_sunShadowTexture, _sunShadowView, _sunShadowSrv) = CreateDepthTarget(SunShadowResolution, SunShadowResolution);
        (_spotShadowTexture, _spotShadowView, _spotShadowSrv) = CreateDepthTarget(SpotShadowResolution, SpotShadowResolution);

        BoxMesh = Mesh.CreateBox(_device);
        SphereMesh = Mesh.CreateSphere(_device);
    }

    /// <summary>Uploads a loaded model (meshes and textures) to the GPU.</summary>
    public RenderModel CreateModel(Assets.Model model) => RenderModel.Create(_device, _context, model);

    private static Blob Compile(string source, string entryPoint, string profile)
    {
        var result = Compiler.Compile(source, entryPoint, "shaders.hlsl", profile, out var blob, out var errors);
        if (result.Failure)
            throw new InvalidOperationException($"Shader '{entryPoint}' ({profile}) failed to compile: {errors?.AsString()}");
        errors?.Dispose();
        return blob;
    }

    private ID3D11Buffer CreateConstantBuffer<T>() where T : unmanaged
    {
        int size = (Unsafe.SizeOf<T>() + 15) / 16 * 16;   // cbuffers must be 16-byte aligned
        return _device.CreateBuffer(new BufferDescription(
            (uint)size, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
    }

    /// <summary>Depth texture that is both rendered into and sampled, so it is created typeless.</summary>
    private (ID3D11Texture2D, ID3D11DepthStencilView, ID3D11ShaderResourceView) CreateDepthTarget(int width, int height)
    {
        var texture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R32_Typeless,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
        });
        var view = _device.CreateDepthStencilView(texture,
            new DepthStencilViewDescription(texture, DepthStencilViewDimension.Texture2D, Format.D32_Float));
        var srv = _device.CreateShaderResourceView(texture,
            new ShaderResourceViewDescription(texture, ShaderResourceViewDimension.Texture2D, Format.R32_Float));
        return (texture, view, srv);
    }

    private ID3D11Texture2D CreateRenderTarget(Format format)
    {
        return _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_window.Width,
            Height = (uint)_window.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
    }

    private void CreateSizeDependentResources()
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _backBufferView = _device.CreateRenderTargetView(backBuffer);

        _albedoTexture = CreateRenderTarget(Format.R8G8B8A8_UNorm);
        _normalTexture = CreateRenderTarget(Format.R10G10B10A2_UNorm);
        _albedoView = _device.CreateRenderTargetView(_albedoTexture);
        _normalView = _device.CreateRenderTargetView(_normalTexture);
        _albedoSrv = _device.CreateShaderResourceView(_albedoTexture);
        _normalSrv = _device.CreateShaderResourceView(_normalTexture);

        (_depthTexture, _depthView, _depthSrv) = CreateDepthTarget(_window.Width, _window.Height);
    }

    private void ReleaseSizeDependentResources()
    {
        _depthSrv.Dispose();
        _depthView.Dispose();
        _depthTexture.Dispose();
        _normalSrv.Dispose();
        _albedoSrv.Dispose();
        _normalView.Dispose();
        _albedoView.Dispose();
        _normalTexture.Dispose();
        _albedoTexture.Dispose();
        _backBufferView.Dispose();
    }

    public void Resize()
    {
        _context.UnsetRenderTargets();
        ReleaseSizeDependentResources();
        _swapChain.ResizeBuffers(2, (uint)_window.Width, (uint)_window.Height, Format.R8G8B8A8_UNorm, SwapChainFlags.None);
        CreateSizeDependentResources();
    }

    /// <summary>Runs every pass for one frame and presents.</summary>
    public void RenderFrame(
        IReadOnlyList<DrawCommand> scene,
        IReadOnlyList<DrawCommand> overlay,
        IReadOnlyList<PointLight> pointLights,
        Spotlight? flashlight,
        in Matrix4x4 view,
        in Matrix4x4 projection,
        Vector3 cameraPosition)
    {
        var viewProjection = view * projection;
        var sunViewProjection = BuildSunMatrix(cameraPosition);
        var spotViewProjection = flashlight is { } spot ? BuildSpotMatrix(spot) : Matrix4x4.Identity;

        // Unbind the G-buffer from the pixel stage before it is rendered into again.
        _context.PSSetShaderResources(0, [null, null, null, null, null]);

        ShadowPass(_sunShadowView, SunShadowResolution, sunViewProjection, scene);
        if (flashlight is not null)
            ShadowPass(_spotShadowView, SpotShadowResolution, spotViewProjection, scene);

        GeometryPass(viewProjection, scene);
        LightingPass(viewProjection, sunViewProjection, spotViewProjection, pointLights, flashlight, cameraPosition);
        OverlayPass(viewProjection, overlay);

        _swapChain.Present(1, PresentFlags.None);
    }

    private void ShadowPass(ID3D11DepthStencilView target, int resolution, in Matrix4x4 lightViewProjection,
        IReadOnlyList<DrawCommand> scene)
    {
        _context.ClearDepthStencilView(target, DepthStencilClearFlags.Depth, 1f, 0);
        _context.OMSetRenderTargets((ID3D11RenderTargetView)null, target);
        _context.RSSetViewport(0, 0, resolution, resolution);
        _context.RSSetState(_shadowRasterizer);
        _context.OMSetDepthStencilState(_depthState);

        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetInputLayout(_inputLayout);
        _context.VSSetShader(_shadowVS);
        _context.PSSetShader(null);
        _context.VSSetConstantBuffer(0, _perPass);
        _context.VSSetConstantBuffer(1, _perObject);

        Write(_perPass, new PerPassData { ViewProjection = lightViewProjection });
        DrawAll(scene);
    }

    private void GeometryPass(in Matrix4x4 viewProjection, IReadOnlyList<DrawCommand> scene)
    {
        _context.ClearRenderTargetView(_albedoView, new Color4(0f, 0f, 0f, 1f));
        _context.ClearRenderTargetView(_normalView, new Color4(0.5f, 0.5f, 0.5f, 1f));
        _context.ClearDepthStencilView(_depthView, DepthStencilClearFlags.Depth, 1f, 0);
        _context.OMSetRenderTargets([_albedoView, _normalView], _depthView);
        _context.RSSetViewport(0, 0, _window.Width, _window.Height);
        _context.RSSetState(_rasterizer);
        _context.OMSetDepthStencilState(_depthState);

        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetInputLayout(_inputLayout);
        _context.VSSetShader(_gbufferVS);
        _context.PSSetShader(_gbufferPS);
        _context.VSSetConstantBuffer(0, _perPass);
        _context.VSSetConstantBuffer(1, _perObject);
        _context.PSSetConstantBuffer(1, _perObject);
        _context.PSSetSampler(0, _albedoSampler);

        Write(_perPass, new PerPassData { ViewProjection = viewProjection });
        DrawAll(scene, bindTextures: true);
    }

    private void LightingPass(in Matrix4x4 viewProjection, in Matrix4x4 sunViewProjection, in Matrix4x4 spotViewProjection,
        IReadOnlyList<PointLight> pointLights, Spotlight? flashlight, Vector3 cameraPosition)
    {
        Matrix4x4.Invert(viewProjection, out var inverseViewProjection);

        var data = new LightingData
        {
            InverseViewProjection = inverseViewProjection,
            SunViewProjection = sunViewProjection,
            SpotViewProjection = spotViewProjection,
            CameraPosition = cameraPosition,
            PointLightCount = Math.Min(pointLights.Count, Shaders.MaxPointLights),
            SunDirection = SunDirection,
            SunIntensity = SunIntensity,
            SunColor = SunColor,
            SunTexelSize = 1f / SunShadowResolution,
            SpotTexelSize = 1f / SpotShadowResolution,
        };

        if (flashlight is { } spot)
        {
            data.SpotEnabled = 1f;
            data.SpotPosition = spot.Position;
            data.SpotDirection = Vector3.Normalize(spot.Direction);
            data.SpotColor = spot.Color;
            data.SpotIntensity = spot.Intensity;
            data.SpotRange = spot.Range;
            data.SpotInnerCos = MathF.Cos(spot.InnerAngle);
            data.SpotOuterCos = MathF.Cos(spot.OuterAngle);
        }

        for (int i = 0; i < (int)data.PointLightCount; i++)
        {
            var light = pointLights[i];
            data.PointPositionRange[i] = new Vector4(light.Position, light.Range);
            data.PointColorIntensity[i] = new Vector4(light.Color, light.Intensity);
        }

        Write(_lighting, data);

        _context.OMSetRenderTargets(_backBufferView, null);
        _context.RSSetViewport(0, 0, _window.Width, _window.Height);
        _context.RSSetState(_rasterizer);
        _context.OMSetDepthStencilState(_noDepthState);

        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_lightingVS);
        _context.PSSetShader(_lightingPS);
        _context.PSSetConstantBuffer(0, _lighting);
        _context.PSSetShaderResources(0, [_albedoSrv, _normalSrv, _depthSrv, _sunShadowSrv, _spotShadowSrv]);
        _context.PSSetSampler(0, _shadowSampler);
        _context.Draw(3, 0);
    }

    private void OverlayPass(in Matrix4x4 viewProjection, IReadOnlyList<DrawCommand> overlay)
    {
        if (overlay.Count == 0) return;

        _context.PSSetShaderResources(0, [null, null, null, null, null]);
        _context.OMSetRenderTargets(_backBufferView, null);
        _context.OMSetDepthStencilState(_noDepthState);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetInputLayout(_inputLayout);
        _context.VSSetShader(_unlitVS);
        _context.PSSetShader(_unlitPS);
        _context.VSSetConstantBuffer(0, _perPass);
        _context.VSSetConstantBuffer(1, _perObject);
        _context.PSSetConstantBuffer(1, _perObject);

        Write(_perPass, new PerPassData { ViewProjection = viewProjection });
        DrawAll(overlay);
    }

    private void DrawAll(IReadOnlyList<DrawCommand> commands, bool bindTextures = false)
    {
        for (int i = 0; i < commands.Count; i++)
        {
            var command = commands[i];
            bool textured = bindTextures && command.Texture is not null;
            Write(_perObject, new PerObjectData
            {
                World = command.World,
                Color = command.Color,
                TexScale = command.TexScale,
                Checker = textured ? 2f : command.Checker ? 1f : 0f,
            });

            if (bindTextures)
                _context.PSSetShaderResource(0, textured ? command.Texture.View : null);

            _context.IASetVertexBuffer(0, command.Mesh.VertexBuffer, (uint)Unsafe.SizeOf<Vertex>());
            _context.IASetIndexBuffer(command.Mesh.IndexBuffer, Format.R32_UInt, 0u);
            _context.DrawIndexed((uint)command.Mesh.IndexCount, 0, 0);
        }
    }

    /// <summary>Orthographic sun frustum, snapped to texels so shadows do not crawl when walking.</summary>
    private static Matrix4x4 BuildSunMatrix(Vector3 cameraPosition)
    {
        var center = new Vector3(MathF.Round(cameraPosition.X), 0f, MathF.Round(cameraPosition.Z));
        var eye = center - SunDirection * 60f;
        var view = Matrix4x4.CreateLookAt(eye, center, Vector3.UnitY);
        var projection = Matrix4x4.CreateOrthographic(SunShadowExtent * 2f, SunShadowExtent * 2f, 1f, 130f);
        var viewProjection = view * projection;

        // Snap the origin to whole shadow texels.
        var origin = Vector4.Transform(new Vector4(0, 0, 0, 1), viewProjection);
        float scale = SunShadowResolution * 0.5f;
        float offsetX = (MathF.Round(origin.X * scale) - origin.X * scale) / scale;
        float offsetY = (MathF.Round(origin.Y * scale) - origin.Y * scale) / scale;
        return viewProjection * Matrix4x4.CreateTranslation(offsetX, offsetY, 0);
    }

    private static Matrix4x4 BuildSpotMatrix(in Spotlight spot)
    {
        var direction = Vector3.Normalize(spot.Direction);
        var up = MathF.Abs(direction.Y) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;
        var view = Matrix4x4.CreateLookAt(spot.Position, spot.Position + direction, up);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.Min(spot.OuterAngle * 2.2f, 3.0f), 1f, 0.15f, spot.Range);
        return view * projection;
    }

    private unsafe void Write<T>(ID3D11Buffer buffer, in T data) where T : unmanaged
    {
        var mapped = _context.Map(buffer, 0u, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        Unsafe.Write((void*)mapped.DataPointer, data);
        _context.Unmap(buffer, 0u);
    }

    public void Dispose()
    {
        BoxMesh.Dispose();
        SphereMesh.Dispose();

        _spotShadowSrv.Dispose();
        _spotShadowView.Dispose();
        _spotShadowTexture.Dispose();
        _sunShadowSrv.Dispose();
        _sunShadowView.Dispose();
        _sunShadowTexture.Dispose();

        _albedoSampler.Dispose();
        _shadowSampler.Dispose();
        _noDepthState.Dispose();
        _depthState.Dispose();
        _shadowRasterizer.Dispose();
        _rasterizer.Dispose();

        _lighting.Dispose();
        _perObject.Dispose();
        _perPass.Dispose();

        _inputLayout.Dispose();
        _unlitPS.Dispose();
        _unlitVS.Dispose();
        _lightingPS.Dispose();
        _lightingVS.Dispose();
        _shadowVS.Dispose();
        _gbufferPS.Dispose();
        _gbufferVS.Dispose();

        ReleaseSizeDependentResources();
        _swapChain.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
