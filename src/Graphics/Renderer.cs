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
internal struct PerFrameData
{
    public Matrix4x4 ViewProjection;
    public Vector3 CameraPosition;
    public float Pad0;
    public Vector3 LightDirection;
    public float Pad1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PerObjectData
{
    public Matrix4x4 World;
    public Vector4 Color;
    public Vector3 TexScale;
    public float Checker;
}

public sealed class Renderer : IDisposable
{
    private readonly GameWindow _window;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11Buffer _perFrame;
    private readonly ID3D11Buffer _perObject;
    private readonly ID3D11RasterizerState _rasterizer;
    private readonly ID3D11DepthStencilState _depthState;

    private ID3D11RenderTargetView _backBufferView;
    private ID3D11Texture2D _depthTexture;
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

        var swapChainDesc = new SwapChainDescription1
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
        };
        _swapChain = factory.CreateSwapChainForHwnd(_device, window.Handle, swapChainDesc);
        factory.MakeWindowAssociation(window.Handle, WindowAssociationFlags.IgnoreAltEnter);

        CreateSizeDependentResources();

        var vsBlob = Compile("VSMain", "vs_5_0");
        var psBlob = Compile("PSMain", "ps_5_0");
        _vs = _device.CreateVertexShader(vsBlob.AsSpan());
        _ps = _device.CreatePixelShader(psBlob.AsSpan());

        InputElementDescription[] elements =
        [
            new("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
        ];
        _inputLayout = _device.CreateInputLayout(elements, vsBlob.AsSpan());
        vsBlob.Dispose();
        psBlob.Dispose();

        _perFrame = CreateConstantBuffer<PerFrameData>();
        _perObject = CreateConstantBuffer<PerObjectData>();

        _rasterizer = _device.CreateRasterizerState(new RasterizerDescription
        {
            CullMode = CullMode.Back,
            FillMode = FillMode.Solid,
            FrontCounterClockwise = false,
            DepthClipEnable = true,
        });
        _depthState = _device.CreateDepthStencilState(
            new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.LessEqual));

        BoxMesh = Mesh.CreateBox(_device);
        SphereMesh = Mesh.CreateSphere(_device);
    }

    private static Blob Compile(string entryPoint, string profile)
    {
        var result = Compiler.Compile(Shaders.Hlsl, entryPoint, "shaders.hlsl", profile, out var blob, out var errors);
        if (result.Failure)
            throw new InvalidOperationException($"Shader '{entryPoint}' failed to compile: {errors?.AsString()}");
        errors?.Dispose();
        return blob;
    }

    private ID3D11Buffer CreateConstantBuffer<T>() where T : unmanaged
    {
        int size = (Unsafe.SizeOf<T>() + 15) / 16 * 16;   // cbuffers must be 16-byte aligned
        return _device.CreateBuffer(new BufferDescription(
            (uint)size, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
    }

    private void CreateSizeDependentResources()
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _backBufferView = _device.CreateRenderTargetView(backBuffer);

        _depthTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_window.Width,
            Height = (uint)_window.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
        });
        _depthView = _device.CreateDepthStencilView(_depthTexture);
    }

    public void Resize()
    {
        _context.UnsetRenderTargets();
        _backBufferView.Dispose();
        _depthView.Dispose();
        _depthTexture.Dispose();
        _swapChain.ResizeBuffers(2, (uint)_window.Width, (uint)_window.Height, Format.R8G8B8A8_UNorm, SwapChainFlags.None);
        CreateSizeDependentResources();
    }

    public void BeginFrame(in Matrix4x4 viewProjection, Vector3 cameraPosition)
    {
        _context.RSSetViewport(0, 0, _window.Width, _window.Height);
        _context.OMSetRenderTargets(_backBufferView, _depthView);
        _context.ClearRenderTargetView(_backBufferView, new Color4(0.55f, 0.62f, 0.72f, 1f));
        _context.ClearDepthStencilView(_depthView, DepthStencilClearFlags.Depth, 1f, 0);

        Write(_perFrame, new PerFrameData
        {
            ViewProjection = viewProjection,
            CameraPosition = cameraPosition,
            LightDirection = Vector3.Normalize(new Vector3(-0.45f, -0.8f, -0.35f)),
        });

        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetInputLayout(_inputLayout);
        _context.VSSetShader(_vs);
        _context.PSSetShader(_ps);
        _context.VSSetConstantBuffer(0, _perFrame);
        _context.PSSetConstantBuffer(0, _perFrame);
        _context.VSSetConstantBuffer(1, _perObject);
        _context.PSSetConstantBuffer(1, _perObject);
        _context.RSSetState(_rasterizer);
        _context.OMSetDepthStencilState(_depthState);
    }

    public void Draw(Mesh mesh, in Matrix4x4 world, Vector4 color, Vector3 texScale, bool checker)
    {
        Write(_perObject, new PerObjectData
        {
            World = world,
            Color = color,
            TexScale = texScale,
            Checker = checker ? 1f : 0f,
        });

        _context.IASetVertexBuffer(0, mesh.VertexBuffer, (uint)Unsafe.SizeOf<Vertex>());
        _context.IASetIndexBuffer(mesh.IndexBuffer, Format.R16_UInt, 0u);
        _context.DrawIndexed((uint)mesh.IndexCount, 0, 0);
    }

    public void EndFrame(bool vsync = true) => _swapChain.Present(vsync ? 1u : 0u, PresentFlags.None);

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
        _depthState.Dispose();
        _rasterizer.Dispose();
        _perObject.Dispose();
        _perFrame.Dispose();
        _inputLayout.Dispose();
        _ps.Dispose();
        _vs.Dispose();
        _depthView.Dispose();
        _depthTexture.Dispose();
        _backBufferView.Dispose();
        _swapChain.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
