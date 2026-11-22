using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Fluorite.App.Services;

/// <summary>
/// Decodes local images (screenshots, world and server icons) straight to pixels at the size they're shown. A
/// <see cref="BitmapImage"/> fed from a stream also keeps the whole encoded file (a 4K screenshot is ~10 MB), which
/// made a screenshot gallery cost hundreds of MB. Results are <see cref="IDisposable"/>: dispose what's no longer
/// shown to free it right away instead of whenever the GC runs.
/// </summary>
public static class ImageLoader
{
    /// <param name="decodeWidth">Width it's shown at, in view pixels (scaled for the display); 0 keeps its own size.</param>
    public static async Task<ImageSource?> LoadAsync(string path, int decodeWidth)
    {
        try
        {
            using var stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read);
            return await DecodeAsync(stream, decodeWidth);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static async Task<ImageSource?> FromBytesAsync(byte[] bytes, int decodeWidth)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        return await DecodeAsync(stream, decodeWidth);
    }

    private static async Task<ImageSource?> DecodeAsync(IRandomAccessStream stream, int decodeWidth)
    {
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var transform = new BitmapTransform();
            var scale = App.Window?.Content?.XamlRoot?.RasterizationScale ?? 1.0;
            var target = (uint)Math.Round(decodeWidth * scale);
            if (decodeWidth > 0 && target < decoder.PixelWidth)
            {
                transform.ScaledWidth = target;
                transform.ScaledHeight = Math.Max(1, (uint)Math.Round((double)decoder.PixelHeight * target / decoder.PixelWidth));
                transform.InterpolationMode = BitmapInterpolationMode.Fant;
            }
            var pixels = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            return await ToSourceAsync(pixels);
        }
        catch (Exception)
        {
            return null; // Not an image Windows can decode.
        }
    }

    /// <summary>
    /// Shows <paramref name="pixels"/>, which are the source's from now on: never dispose them. WinUI doesn't copy them
    /// once and let go; it copies them again whenever it needs the image back (a list row refilled or scrolled back
    /// into view), and pixels closed by then end the app inside WinUI (RO_E_CLOSED in AsyncCopyToSurfaceTask). They're
    /// freed with the source: <see cref="Release"/>, or the GC.
    /// </summary>
    public static async Task<ImageSource> ToSourceAsync(SoftwareBitmap pixels)
    {
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(pixels);
        return source;
    }

    /// <summary>Frees an image returned by this class (no-op for anything else).</summary>
    public static void Release(ImageSource? image) => (image as IDisposable)?.Dispose();
}
