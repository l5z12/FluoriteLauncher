using System.Runtime.InteropServices.WindowsRuntime;
using Fluorite.Core.Skins;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Fluorite.App.Services;

/// <summary>Skin textures to pixels and renders to images. Results are disposable (<see cref="ImageLoader.Release"/>).</summary>
public static class SkinImages
{
    /// <summary>Decodes a PNG to straight-alpha BGRA pixels; null when it isn't an image.</summary>
    public static async Task<SkinImage?> DecodeAsync(byte[] png)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(png.AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            return new SkinImage(data.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Shows pixels enlarged with hard edges to about <paramref name="height"/> view pixels (scaled for the display),
    /// so the image is only ever scaled down a little and stays crisp.
    /// </summary>
    public static async Task<ImageSource> ToSourceAsync(SkinImage image, double height)
    {
        var scale = App.Window?.Content?.XamlRoot?.RasterizationScale ?? 1.0;
        var factor = Math.Max(1, (int)Math.Ceiling(height * scale / image.Height));
        var big = SkinRenderer.Scale(image, factor);
        Premultiply(big.Pixels);
        var bitmap = SoftwareBitmap.CreateCopyFromBuffer(big.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8, big.Width, big.Height, BitmapAlphaMode.Premultiplied);
        return await ImageLoader.ToSourceAsync(bitmap); // Not disposed: see there.
    }

    private static void Premultiply(byte[] bgra)
    {
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            if (a == 255)
                continue;
            bgra[i] = (byte)(bgra[i] * a / 255);
            bgra[i + 1] = (byte)(bgra[i + 1] * a / 255);
            bgra[i + 2] = (byte)(bgra[i + 2] * a / 255);
        }
    }

    /// <summary>The face (with hat) of a skin texture at <paramref name="size"/> view pixels; null when it isn't a skin.</summary>
    public static async Task<ImageSource?> HeadAsync(byte[] png, double size)
    {
        if (await DecodeAsync(png) is not { IsSkin: true } skin)
            return null;
        return await ToSourceAsync(SkinRenderer.RenderHead(skin), size);
    }
}
