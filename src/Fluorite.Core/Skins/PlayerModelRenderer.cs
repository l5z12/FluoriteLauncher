using System.Numerics;

namespace Fluorite.Core.Skins;

/// <summary>
/// A small software renderer for the player model: the vanilla boxes (head, body, arms, legs, each with its outer
/// layer as a slightly larger shell, and the cape) textured from the skin, lit from the front, drawn into a BGRA
/// buffer with a depth buffer. Pixel-exact texturing keeps the Minecraft look; no GPU or web view needed.
/// </summary>
public sealed class PlayerModelRenderer
{
    private enum Part
    {
        Head,
        Body,
        RightArm,
        LeftArm,
        RightLeg,
        LeftLeg,
        Cape,
    }

    /// <summary>A textured quad; corners in order top-left, top-right, bottom-right, bottom-left as seen from outside.</summary>
    private readonly record struct Quad(Vector3 P0, Vector3 P1, Vector3 P2, Vector3 P3, float U0, float V0, float U1, float V1, Part Part, bool OnCape);

    private static readonly Vector3 s_light = Vector3.Normalize(new Vector3(-0.35f, 0.55f, 1f));
    private readonly SkinImage _skin;
    private readonly SkinImage? _cape;
    private readonly List<Quad> _quads = [];
    private readonly int _armWidth;
    private float[] _depth = [];

    /// <param name="skin">Any skin texture (legacy 64×32 is converted the way the game does).</param>
    /// <param name="cape">A cape texture (64×32 or HD multiples), or null.</param>
    public PlayerModelRenderer(SkinImage skin, bool slim, SkinImage? cape)
    {
        _skin = SkinRenderer.Normalize(skin);
        _cape = cape is { Width: >= 64 } c && c.Width % 64 == 0 && c.Height >= c.Width / 2 ? c : null;
        _armWidth = slim ? 3 : 4;
        Build();
    }

    // ── Model (units are skin pixels; y up, feet at 0, the player faces +z) ──

    private void Build()
    {
        var aw = _armWidth;
        Box(Part.Head, new Vector3(-4, 24, -4), new Vector3(8, 8, 8), 0, 0, 0f);
        Box(Part.Head, new Vector3(-4, 24, -4), new Vector3(8, 8, 8), 32, 0, 0.5f);
        Box(Part.Body, new Vector3(-4, 12, -2), new Vector3(8, 12, 4), 16, 16, 0f);
        Box(Part.Body, new Vector3(-4, 12, -2), new Vector3(8, 12, 4), 16, 32, 0.25f);
        Box(Part.RightArm, new Vector3(-4 - aw, 12, -2), new Vector3(aw, 12, 4), 40, 16, 0f);
        Box(Part.RightArm, new Vector3(-4 - aw, 12, -2), new Vector3(aw, 12, 4), 40, 32, 0.25f);
        Box(Part.LeftArm, new Vector3(4, 12, -2), new Vector3(aw, 12, 4), 32, 48, 0f);
        Box(Part.LeftArm, new Vector3(4, 12, -2), new Vector3(aw, 12, 4), 48, 48, 0.25f);
        Box(Part.RightLeg, new Vector3(-4, 0, -2), new Vector3(4, 12, 4), 0, 16, 0f);
        Box(Part.RightLeg, new Vector3(-4, 0, -2), new Vector3(4, 12, 4), 0, 32, 0.25f);
        Box(Part.LeftLeg, new Vector3(0, 0, -2), new Vector3(4, 12, 4), 16, 48, 0f);
        Box(Part.LeftLeg, new Vector3(0, 0, -2), new Vector3(4, 12, 4), 0, 48, 0.25f);
        if (_cape is not null)
        {
            // Built facing +z like the others, then turned around so its outside (UV 1,1) faces back.
            var start = _quads.Count;
            Box(Part.Cape, new Vector3(-5, -16, -0.5f), new Vector3(10, 16, 1), 0, 0, 0f, cape: true);
            for (var i = start; i < _quads.Count; i++)
            {
                var q = _quads[i];
                _quads[i] = q with { P0 = Turn(q.P0), P1 = Turn(q.P1), P2 = Turn(q.P2), P3 = Turn(q.P3) };
            }
        }

        // 180° about y, then hung from the shoulders just behind the back.
        static Vector3 Turn(Vector3 p) => new(-p.X, p.Y + 24, -p.Z - 2.5f);
    }

    /// <summary>
    /// The six faces of a box with the standard Minecraft UV layout at (<paramref name="u"/>, <paramref name="v"/>);
    /// <paramref name="inflate"/> grows the geometry (outer layers) but not the texture area.
    /// </summary>
    private void Box(Part part, Vector3 origin, Vector3 size, float u, float v, float inflate, bool cape = false)
    {
        float w = size.X, h = size.Y, d = size.Z;
        var a = origin - new Vector3(inflate);
        var b = origin + size + new Vector3(inflate);
        float x0 = a.X, y0 = a.Y, z0 = a.Z, x1 = b.X, y1 = b.Y, z1 = b.Z;

        Add(new(x0, y1, z1), new(x1, y1, z1), new(x1, y0, z1), new(x0, y0, z1), u + d, v + d, u + d + w, v + d + h);                 // front
        Add(new(x0, y1, z0), new(x0, y1, z1), new(x0, y0, z1), new(x0, y0, z0), u, v + d, u + d, v + d + h);                         // right side (−x)
        Add(new(x1, y1, z1), new(x1, y1, z0), new(x1, y0, z0), new(x1, y0, z1), u + d + w, v + d, u + d + w + d, v + d + h);         // left side (+x)
        Add(new(x1, y1, z0), new(x0, y1, z0), new(x0, y0, z0), new(x1, y0, z0), u + d + w + d, v + d, u + d + w + d + w, v + d + h); // back
        Add(new(x0, y1, z0), new(x1, y1, z0), new(x1, y1, z1), new(x0, y1, z1), u + d, v, u + d + w, v + d);                         // top
        Add(new(x0, y0, z1), new(x1, y0, z1), new(x1, y0, z0), new(x0, y0, z0), u + d + w, v, u + d + w + w, v + d);                 // bottom

        void Add(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u0, float v0, float u1, float v1) =>
            _quads.Add(new Quad(p0, p1, p2, p3, u0, v0, u1, v1, part, cape));
    }

    // ── Rendering ────────────────────────────────────────────────────────

    /// <summary>
    /// Draws the player into <paramref name="target"/> (premultiplied BGRA, <paramref name="width"/>×<paramref name="height"/>,
    /// cleared first), turned by <paramref name="yaw"/>/<paramref name="pitch"/> degrees; <paramref name="walk"/> is the
    /// walking cycle in radians (0 = standing).
    /// </summary>
    public void Render(Span<byte> target, int width, int height, float yaw, float pitch, float walk)
    {
        target.Clear();
        if (_depth.Length != width * height)
            _depth = new float[width * height];
        Array.Fill(_depth, float.NegativeInfinity);

        var view = Matrix4x4.CreateTranslation(0, -16, 0)
            * Matrix4x4.CreateRotationY(yaw * MathF.PI / 180f)
            * Matrix4x4.CreateRotationX(pitch * MathF.PI / 180f);
        // Fit the turned figure (about 34 px tall with the hat, up to 20 wide) with a margin.
        var scale = Math.Min(height / 40f, width / 24f);
        var center = new Vector2(width / 2f, height / 2f);

        var swing = MathF.Sin(walk) * 0.55f;
        var transforms = new Matrix4x4[7];
        transforms[(int)Part.Head] = Matrix4x4.Identity;
        transforms[(int)Part.Body] = Matrix4x4.Identity;
        transforms[(int)Part.RightArm] = Swing(swing, 22, 0);
        transforms[(int)Part.LeftArm] = Swing(-swing, 22, 0);
        transforms[(int)Part.RightLeg] = Swing(-swing, 12, 0);
        transforms[(int)Part.LeftLeg] = Swing(swing, 12, 0);
        // The cape hangs a little away from the back and flutters with the walk.
        transforms[(int)Part.Cape] = Swing(-(0.12f + MathF.Abs(swing) * 0.35f), 24, -2.5f);

        foreach (var q in _quads)
        {
            var m = transforms[(int)q.Part] * view;
            var p0 = Vector3.Transform(q.P0, m);
            var p1 = Vector3.Transform(q.P1, m);
            var p2 = Vector3.Transform(q.P2, m);
            var p3 = Vector3.Transform(q.P3, m);
            var normal = Vector3.Normalize(Vector3.Cross(p3 - p0, p1 - p0));
            if (normal.Z <= 0)
                continue; // Facing away.
            var shade = 0.55f + 0.45f * MathF.Max(0, Vector3.Dot(normal, s_light));
            var texture = q.OnCape ? _cape!.Value : _skin;
            var k = texture.Width / 64f;
            var s0 = Project(p0, center, scale);
            var s1 = Project(p1, center, scale);
            var s2 = Project(p2, center, scale);
            var s3 = Project(p3, center, scale);
            var uv = new Vector4(q.U0 * k, q.V0 * k, q.U1 * k, q.V1 * k);
            Triangle(target, width, height, s0, s1, s2, new(0, 0), new(1, 0), new(1, 1), texture, uv, shade);
            Triangle(target, width, height, s0, s2, s3, new(0, 0), new(1, 1), new(0, 1), texture, uv, shade);
        }
    }

    /// <summary>Rotation about the x axis through (y, z) = (<paramref name="pivotY"/>, <paramref name="pivotZ"/>); positive swings the bottom forward.</summary>
    private static Matrix4x4 Swing(float angle, float pivotY, float pivotZ) =>
        Matrix4x4.CreateTranslation(0, -pivotY, -pivotZ) * Matrix4x4.CreateRotationX(-angle) * Matrix4x4.CreateTranslation(0, pivotY, pivotZ);

    private static Vector3 Project(Vector3 p, Vector2 center, float scale) =>
        new(center.X + p.X * scale, center.Y - p.Y * scale, p.Z);

    /// <summary>Fills a triangle with nearest-texel sampling inside the face's texture rectangle, depth-tested.</summary>
    private void Triangle(Span<byte> target, int width, int height, Vector3 a, Vector3 b, Vector3 c,
        Vector2 ta, Vector2 tb, Vector2 tc, SkinImage tex, Vector4 uv, float shade)
    {
        var area = Edge(a, b, c.X, c.Y);
        if (MathF.Abs(area) < 1e-6f)
            return;
        var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
        var maxX = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
        var maxY = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        var uMin = MathF.Min(uv.X, uv.Z);
        var uMax = MathF.Max(uv.X, uv.Z) - 0.001f;
        var vMin = MathF.Min(uv.Y, uv.W);
        var vMax = MathF.Max(uv.Y, uv.W) - 0.001f;
        var pixels = tex.Pixels;

        for (var y = minY; y <= maxY; y++)
        {
            var py = y + 0.5f;
            for (var x = minX; x <= maxX; x++)
            {
                var px = x + 0.5f;
                var w0 = Edge(b, c, px, py) / area;
                var w1 = Edge(c, a, px, py) / area;
                var w2 = 1 - w0 - w1;
                if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f)
                    continue;
                var z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                var di = y * width + x;
                if (z <= _depth[di])
                    continue;

                var s = w0 * ta.X + w1 * tb.X + w2 * tc.X;
                var t = w0 * ta.Y + w1 * tb.Y + w2 * tc.Y;
                var tu = (int)Math.Clamp(uv.X + s * (uv.Z - uv.X), uMin, uMax);
                var tv = (int)Math.Clamp(uv.Y + t * (uv.W - uv.Y), vMin, vMax);
                if ((uint)tu >= (uint)tex.Width || (uint)tv >= (uint)tex.Height)
                    continue;
                var ti = (tv * tex.Width + tu) * 4;
                var alpha = pixels[ti + 3];
                if (alpha == 0)
                    continue;

                var o = di * 4;
                var sb = pixels[ti] * shade;
                var sg = pixels[ti + 1] * shade;
                var sr = pixels[ti + 2] * shade;
                if (alpha == 255)
                {
                    target[o] = (byte)sb;
                    target[o + 1] = (byte)sg;
                    target[o + 2] = (byte)sr;
                    target[o + 3] = 255;
                }
                else
                {
                    // Translucent outer-layer pixel over what's drawn (premultiplied "over").
                    var fa = alpha / 255f;
                    target[o] = (byte)(sb * fa + target[o] * (1 - fa));
                    target[o + 1] = (byte)(sg * fa + target[o + 1] * (1 - fa));
                    target[o + 2] = (byte)(sr * fa + target[o + 2] * (1 - fa));
                    target[o + 3] = (byte)Math.Min(255, alpha + target[o + 3] * (1 - fa));
                }
                _depth[di] = z;
            }
        }
    }

    private static float Edge(Vector3 a, Vector3 b, float x, float y) => (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
}
