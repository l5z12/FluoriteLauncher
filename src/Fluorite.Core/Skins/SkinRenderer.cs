namespace Fluorite.Core.Skins;

/// <summary>A decoded image: 4 bytes per pixel with alpha last (BGRA or RGBA; channel order is passed through).</summary>
public readonly record struct SkinImage(byte[] Pixels, int Width, int Height)
{
    /// <summary>Skins are 64×64 (since 1.8) or legacy 64×32, possibly HD multiples of those.</summary>
    public bool IsSkin => Width >= 64 && Width % 64 == 0 && (Height == Width || Height * 2 == Width);

    public bool IsLegacy => Height * 2 == Width;
}

/// <summary>
/// Flat front and back views of a player from a skin texture (and cape), plus the head avatar. Pure pixel copies
/// following the vanilla UV layout, so previews need no 3D view.
/// </summary>
public static class SkinRenderer
{
    /// <summary>Size of a body render in skin pixels (times the skin's scale for HD skins).</summary>
    public const int BodyWidth = 16, BodyHeight = 32;

    /// <summary>The front (or back) of the player. The result is (16×32)·scale, transparent around the figure.</summary>
    public static SkinImage RenderBody(SkinImage skin, bool slim, bool back, SkinImage? cape = null)
    {
        var s = skin.Width / 64;
        var canvas = new Canvas(BodyWidth * s, BodyHeight * s);
        var aw = slim ? 3 : 4;
        var legacy = skin.IsLegacy;

        if (!back)
        {
            Part(canvas, skin, s, 8, 8, 8, 8, 4, 0);                  // head
            Part(canvas, skin, s, 20, 20, 8, 12, 4, 8);               // body
            Part(canvas, skin, s, 44, 20, aw, 12, 4 - aw, 8);         // right arm (viewer's left)
            if (legacy)
                Part(canvas, skin, s, 44, 20, aw, 12, 12, 8, mirror: true);
            else
                Part(canvas, skin, s, 36, 52, aw, 12, 12, 8);         // left arm
            Part(canvas, skin, s, 4, 20, 4, 12, 4, 20);               // right leg
            if (legacy)
                Part(canvas, skin, s, 4, 20, 4, 12, 8, 20, mirror: true);
            else
                Part(canvas, skin, s, 20, 52, 4, 12, 8, 20);          // left leg

            if (!legacy)
            {
                Part(canvas, skin, s, 20, 36, 8, 12, 4, 8, overlay: true);       // jacket
                Part(canvas, skin, s, 44, 36, aw, 12, 4 - aw, 8, overlay: true); // right sleeve
                Part(canvas, skin, s, 52, 52, aw, 12, 12, 8, overlay: true);     // left sleeve
                Part(canvas, skin, s, 4, 36, 4, 12, 4, 20, overlay: true);       // right pants
                Part(canvas, skin, s, 4, 52, 4, 12, 8, 20, overlay: true);       // left pants
            }
            if (ShowHat(skin, s))
                Part(canvas, skin, s, 40, 8, 8, 8, 4, 0, overlay: true);
        }
        else
        {
            // Seen from behind, the left limbs are on the viewer's left.
            Part(canvas, skin, s, 24, 8, 8, 8, 4, 0);
            Part(canvas, skin, s, 32, 20, 8, 12, 4, 8);
            Part(canvas, skin, s, 48 + aw, 20, aw, 12, 12, 8);        // right arm back
            if (legacy)
                Part(canvas, skin, s, 48 + aw, 20, aw, 12, 4 - aw, 8, mirror: true);
            else
                Part(canvas, skin, s, 40 + aw, 52, aw, 12, 4 - aw, 8); // left arm back
            Part(canvas, skin, s, 12, 20, 4, 12, 8, 20);              // right leg back
            if (legacy)
                Part(canvas, skin, s, 12, 20, 4, 12, 4, 20, mirror: true);
            else
                Part(canvas, skin, s, 28, 52, 4, 12, 4, 20);          // left leg back

            if (!legacy)
            {
                Part(canvas, skin, s, 32, 36, 8, 12, 4, 8, overlay: true);
                Part(canvas, skin, s, 48 + aw, 36, aw, 12, 12, 8, overlay: true);
                Part(canvas, skin, s, 56 + aw, 52, aw, 12, 4 - aw, 8, overlay: true);
                Part(canvas, skin, s, 12, 36, 4, 12, 8, 20, overlay: true);
                Part(canvas, skin, s, 12, 52, 4, 12, 4, 20, overlay: true);
            }
            if (ShowHat(skin, s))
                Part(canvas, skin, s, 56, 8, 8, 8, 4, 0, overlay: true);

            if (cape is { Width: >= 64 } c && c.Width % 64 == 0)
                DrawCape(canvas, c, s);
        }
        return canvas.ToImage();
    }

    /// <summary>The face with the hat layer: (8×8)·scale.</summary>
    public static SkinImage RenderHead(SkinImage skin)
    {
        var s = skin.Width / 64;
        var canvas = new Canvas(8 * s, 8 * s);
        Part(canvas, skin, s, 8, 8, 8, 8, 0, 0);
        if (ShowHat(skin, s))
            Part(canvas, skin, s, 40, 8, 8, 8, 0, 0, overlay: true);
        return canvas.ToImage();
    }

    /// <summary>
    /// The skin as the game uses it: always the 64×64 layout (legacy 64×32 skins get mirrored copies of the right
    /// arm and leg as the left ones), inner layers opaque, and a fully opaque legacy hat layer cleared.
    /// </summary>
    public static SkinImage Normalize(SkinImage skin)
    {
        var k = Math.Max(1, skin.Width / 64);
        var size = 64 * k;
        var px = new byte[size * size * 4];
        var rows = Math.Min(skin.Height, size);
        skin.Pixels.AsSpan(0, Math.Min(skin.Pixels.Length, rows * skin.Width * 4)).CopyTo(px);
        var image = new SkinImage(px, size, size);

        if (skin.IsLegacy)
        {
            // Vanilla's legacy conversion: each face of the right leg/arm, mirrored, into the left leg/arm.
            int[,] copies =
            {
                { 4, 16, 16, 32, 4, 4 }, { 8, 16, 16, 32, 4, 4 }, { 0, 20, 24, 32, 4, 12 },
                { 4, 20, 16, 32, 4, 12 }, { 8, 20, 8, 32, 4, 12 }, { 12, 20, 16, 32, 4, 12 },
                { 44, 16, -8, 32, 4, 4 }, { 48, 16, -8, 32, 4, 4 }, { 40, 20, 0, 32, 4, 12 },
                { 44, 20, -8, 32, 4, 12 }, { 48, 20, -16, 32, 4, 12 }, { 52, 20, -8, 32, 4, 12 },
            };
            for (var i = 0; i < copies.GetLength(0); i++)
                CopyMirrored(image, k, copies[i, 0], copies[i, 1], copies[i, 2], copies[i, 3], copies[i, 4], copies[i, 5]);
            if (!ShowHat(skin, k))
                SetAlpha(image, k, 32, 0, 64, 16, 0);
        }
        SetAlpha(image, k, 0, 0, 32, 16, 255);
        SetAlpha(image, k, 0, 16, 64, 32, 255);
        SetAlpha(image, k, 16, 48, 48, 64, 255);
        return image;
    }

    private static void CopyMirrored(SkinImage image, int k, int x, int y, int dx, int dy, int w, int h)
    {
        for (var row = 0; row < h * k; row++)
        {
            for (var col = 0; col < w * k; col++)
            {
                var src = ((y * k + row) * image.Width + x * k + col) * 4;
                var dst = (((y + dy) * k + row) * image.Width + (x + dx) * k + (w * k - 1 - col)) * 4;
                image.Pixels.AsSpan(src, 4).CopyTo(image.Pixels.AsSpan(dst, 4));
            }
        }
    }

    private static void SetAlpha(SkinImage image, int k, int x0, int y0, int x1, int y1, byte alpha)
    {
        for (var y = y0 * k; y < y1 * k; y++)
        {
            for (var x = x0 * k; x < x1 * k; x++)
                image.Pixels[(y * image.Width + x) * 4 + 3] = alpha;
        }
    }

    /// <summary>Nearest-neighbour enlargement, keeping pixels crisp.</summary>
    public static SkinImage Scale(SkinImage image, int factor)
    {
        if (factor <= 1)
            return image;
        var w = image.Width * factor;
        var h = image.Height * factor;
        var output = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            var sy = y / factor;
            for (var x = 0; x < w; x++)
                image.Pixels.AsSpan((sy * image.Width + x / factor) * 4, 4).CopyTo(output.AsSpan((y * w + x) * 4));
        }
        return new SkinImage(output, w, h);
    }

    /// <summary>
    /// Vanilla's legacy-skin rule: on a 64×32 skin whose hat area is fully opaque, the hat layer is ignored (old
    /// editors filled it with a solid colour).
    /// </summary>
    private static bool ShowHat(SkinImage skin, int s)
    {
        if (!skin.IsLegacy)
            return true;
        for (var y = 0; y < 16 * s; y++)
        {
            for (var x = 32 * s; x < 64 * s; x++)
            {
                if (skin.Pixels[(y * skin.Width + x) * 4 + 3] < 128)
                    return true;
            }
        }
        return false;
    }

    private static void DrawCape(Canvas canvas, SkinImage cape, int s)
    {
        // The outside of the cape (UV 1,1, 10×16) hangs over the back, from the shoulders down.
        var cs = cape.Width / 64;
        for (var y = 0; y < 16 * s; y++)
        {
            for (var x = 0; x < 10 * s; x++)
            {
                var sx = (1 * cs) + x * cs / s;
                var sy = (1 * cs) + y * cs / s;
                if (sy >= cape.Height)
                    continue;
                canvas.Blend(3 * s + x, 8 * s + y, cape.Pixels.AsSpan((sy * cape.Width + sx) * 4, 4), opaque: false);
            }
        }
    }

    /// <summary>Copies a <paramref name="w"/>×<paramref name="h"/> face (in 64-px units) from the skin to the canvas.</summary>
    private static void Part(Canvas canvas, SkinImage skin, int s, int u, int v, int w, int h, int dx, int dy, bool mirror = false, bool overlay = false)
    {
        if ((v + h) * s > skin.Height)
            return;
        for (var y = 0; y < h * s; y++)
        {
            for (var x = 0; x < w * s; x++)
            {
                var sx = u * s + (mirror ? w * s - 1 - x : x);
                var sy = v * s + y;
                canvas.Blend(dx * s + x, dy * s + y, skin.Pixels.AsSpan((sy * skin.Width + sx) * 4, 4), opaque: !overlay);
            }
        }
    }

    private sealed class Canvas(int width, int height)
    {
        private readonly byte[] _pixels = new byte[width * height * 4];

        /// <summary>Base layers are drawn opaque (as the game does); overlays are alpha-blended.</summary>
        public void Blend(int x, int y, ReadOnlySpan<byte> src, bool opaque)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
                return;
            var dst = _pixels.AsSpan((y * width + x) * 4, 4);
            if (opaque)
            {
                src[..3].CopyTo(dst);
                dst[3] = 255;
                return;
            }
            var a = src[3];
            if (a == 0)
                return;
            if (a == 255 || dst[3] == 0)
            {
                src.CopyTo(dst);
                return;
            }
            for (var i = 0; i < 3; i++)
                dst[i] = (byte)((src[i] * a + dst[i] * (255 - a)) / 255);
            dst[3] = (byte)Math.Max(dst[3], a);
        }

        public SkinImage ToImage() => new(_pixels, width, height);
    }
}
