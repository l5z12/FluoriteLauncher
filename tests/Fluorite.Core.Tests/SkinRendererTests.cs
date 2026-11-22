using Fluorite.Core.Skins;

namespace Fluorite.Core.Tests;

public class SkinRendererTests
{
    /// <summary>A 64×64 skin where every pixel encodes its own coordinates (B = x, G = y), fully opaque.</summary>
    private static SkinImage CoordinateSkin(int height = 64)
    {
        var pixels = new byte[64 * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var i = (y * 64 + x) * 4;
                pixels[i] = (byte)x;
                pixels[i + 1] = (byte)y;
                pixels[i + 3] = 255;
            }
        }
        return new SkinImage(pixels, 64, height);
    }

    private static (int X, int Y) At(SkinImage image, int x, int y)
    {
        var i = (y * image.Width + x) * 4;
        return (image.Pixels[i], image.Pixels[i + 1]);
    }

    [Fact]
    public void Front_MapsVanillaUv()
    {
        var front = SkinRenderer.RenderBody(CoordinateSkin(), slim: false, back: false);
        Assert.Equal((16, 32), (front.Width, front.Height));
        // Opaque overlays win, so the visible pixels come from the hat, jacket, sleeves and pants.
        Assert.Equal((40, 8), At(front, 4, 0));    // hat over the face
        Assert.Equal((20, 36), At(front, 4, 8));   // jacket over the body
        Assert.Equal((44, 36), At(front, 0, 8));   // right sleeve, viewer's left
        Assert.Equal((52, 52), At(front, 12, 8));  // left sleeve
        Assert.Equal((4, 52), At(front, 8, 20));   // left pants
    }

    [Fact]
    public void Legacy_MirrorsRightLimbs()
    {
        var front = SkinRenderer.RenderBody(CoordinateSkin(32), slim: false, back: false);
        Assert.Equal((47, 20), At(front, 12, 8)); // left arm = right arm (44..47) mirrored
        Assert.Equal((7, 20), At(front, 8, 20));  // left leg = right leg (4..7) mirrored
        // A fully opaque legacy hat layer is ignored (vanilla's rule), so the face shows.
        Assert.Equal((8, 8), At(front, 4, 0));
    }

    [Fact]
    public void Slim_UsesThreePixelArms()
    {
        var front = SkinRenderer.RenderBody(CoordinateSkin(), slim: true, back: false);
        Assert.Equal(0, front.Pixels[(8 * 16 + 0) * 4 + 3]); // column 0 stays empty
        Assert.Equal((44, 36), At(front, 1, 8));
    }

    [Fact]
    public void Model_FrontViewShowsOuterLayersAtTheRightTexels()
    {
        var renderer = new PlayerModelRenderer(CoordinateSkin(), slim: false, cape: null);
        var pixels = new byte[240 * 400 * 4];
        renderer.Render(pixels, 240, 400, yaw: 0, pitch: 0, walk: 0);
        var light = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(-0.35f, 0.55f, 1f));
        var shade = 0.55f + 0.45f * light.Z; // A face looking at the viewer.

        // Scale 10 px per skin pixel, model centre (y = 16) in the middle: the jacket's centre texel is (24, 42)…
        AssertTexel(pixels, 240, 120, 180, 24, 42, shade);
        // …and the hat's is (44, 12).
        AssertTexel(pixels, 240, 120, 80, 44, 12, shade);
        // Nothing drawn outside the figure.
        Assert.Equal(0, pixels[(5 * 240 + 5) * 4 + 3]);
    }

    private static void AssertTexel(byte[] pixels, int width, int x, int y, int u, int v, float shade)
    {
        var i = (y * width + x) * 4;
        Assert.Equal(255, pixels[i + 3]);
        Assert.InRange(pixels[i], (int)(u * shade) - 1, (int)(u * shade) + 1);
        Assert.InRange(pixels[i + 1], (int)(v * shade) - 1, (int)(v * shade) + 1);
    }

    [Fact]
    public void Normalize_ConvertsLegacyLikeTheGame()
    {
        var modern = SkinRenderer.Normalize(CoordinateSkin(32));
        Assert.Equal((64, 64), (modern.Width, modern.Height));
        // Left leg front (20..23, 52..63) = right leg front (4..7, 20..31), mirrored.
        Assert.Equal((7, 20), At(modern, 20, 52));
        Assert.Equal((4, 20), At(modern, 23, 52));
    }

    [Fact]
    public void Scale_IsNearestNeighbour()
    {
        var head = SkinRenderer.Scale(SkinRenderer.RenderHead(CoordinateSkin()), 4);
        Assert.Equal((32, 32), (head.Width, head.Height));
        Assert.Equal(At(head, 0, 0), At(head, 3, 3));
    }
}
