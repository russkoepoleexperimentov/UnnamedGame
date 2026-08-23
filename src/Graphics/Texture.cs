using System.Drawing;
using System.Drawing.Imaging;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UnnamedGame.Graphics;

/// <summary>A GPU texture with a full mip chain, decoded from PNG/JPG through GDI+.</summary>
public sealed class Texture : IDisposable
{
    public ID3D11Texture2D Resource { get; }
    public ID3D11ShaderResourceView View { get; }

    private Texture(ID3D11Texture2D resource, ID3D11ShaderResourceView view)
    {
        Resource = resource;
        View = view;
    }

    public static Texture Load(ID3D11Device device, ID3D11DeviceContext context, string path)
    {
        using var bitmap = new Bitmap(path);
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var locked = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            // MipLevels = 0 plus the GenerateMips flag lets the driver allocate the whole chain,
            // which is then filled from level 0 below.
            var texture = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)bitmap.Width,
                Height = (uint)bitmap.Height,
                MipLevels = 0,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                MiscFlags = ResourceOptionFlags.GenerateMips,
            });

            context.UpdateSubresource(texture, 0, null, locked.Scan0, (uint)locked.Stride, 0);

            var view = device.CreateShaderResourceView(texture);
            context.GenerateMips(view);
            return new Texture(texture, view);
        }
        finally
        {
            bitmap.UnlockBits(locked);
        }
    }

    public void Dispose()
    {
        View.Dispose();
        Resource.Dispose();
    }
}
