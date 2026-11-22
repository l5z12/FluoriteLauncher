using System.Runtime.InteropServices.WindowsRuntime;
using Fluorite.Core.Skins;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Fluorite.App.Views;

/// <summary>
/// The player in 3D (<see cref="PlayerModelRenderer"/>): drag to turn, double-click to face front again. With
/// <see cref="Animate"/> on, it walks. Frames are drawn only while it's on screen; leaving frees the bitmap.
/// </summary>
public sealed partial class PlayerModelView : Grid
{
    private const float DefaultYaw = -25, DefaultPitch = 8;
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private PlayerModelRenderer? _renderer;
    private WriteableBitmap? _bitmap;
    private byte[] _pixels = [];
    private float _yaw = DefaultYaw, _pitch = DefaultPitch, _walk;
    private bool _dirty, _dragging, _rendering;
    private Windows.Foundation.Point _last;
    private TimeSpan _lastFrame;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public PlayerModelView()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // Hit-testable everywhere, for dragging.
        Children.Add(_image);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
        Loaded += (_, _) => StartRendering();
        Unloaded += (_, _) => StopRendering();
        SizeChanged += (_, _) => _dirty = true;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerEnded;
        PointerCaptureLost += OnPointerEnded;
        DoubleTapped += (_, _) =>
        {
            _yaw = DefaultYaw;
            _pitch = DefaultPitch;
            _dirty = true;
        };
    }

    /// <summary>Walk in place (when off, the walk eases back to standing).</summary>
    public bool Animate { get; set; } = true;

    /// <summary>Shows a skin (null clears the view).</summary>
    public void SetSkin(SkinImage? skin, bool slim, SkinImage? cape)
    {
        _renderer = skin is { IsSkin: true } s ? new PlayerModelRenderer(s, slim, cape) : null;
        _dirty = true;
        if (_renderer is null)
            _image.Source = null;
        else
            StartRendering();
    }

    private void StartRendering()
    {
        if (_rendering || _renderer is null || !IsLoaded)
            return;
        _rendering = true;
        _dirty = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (_rendering)
            CompositionTarget.Rendering -= OnRendering;
        _rendering = false;
        _image.Source = null;
        _bitmap = null;
        _pixels = [];
    }

    private void OnRendering(object? sender, object e)
    {
        if (_renderer is null)
            return;
        // Our own clock: the event's RenderingEventArgs doesn't come through as that type in AOT builds.
        var now = _clock.Elapsed;
        var dt = _lastFrame == TimeSpan.Zero ? 0f : (float)Math.Min(0.1, (now - _lastFrame).TotalSeconds);
        _lastFrame = now == TimeSpan.Zero ? TimeSpan.FromTicks(1) : now;

        if (Animate)
        {
            _walk = (_walk + dt * 5f) % (MathF.PI * 2);
            _dirty = true;
        }
        else if (_walk != 0)
        {
            // Finish the step: ease towards the nearest standing pose.
            var target = MathF.Round(_walk / MathF.PI) * MathF.PI;
            _walk = MathF.Abs(target - _walk) < 0.05f ? 0 : _walk + (target - _walk) * Math.Min(1, dt * 8);
            _dirty = true;
        }
        if (_dirty)
            Draw();
    }

    private void Draw()
    {
        _dirty = false;
        var scale = XamlRoot?.RasterizationScale ?? 1.0;
        var width = (int)Math.Round(ActualWidth * scale);
        var height = (int)Math.Round(ActualHeight * scale);
        if (width < 8 || height < 8 || _renderer is null)
            return;
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height);
            _pixels = new byte[width * height * 4];
            _image.Source = _bitmap;
        }
        _renderer.Render(_pixels, width, height, _yaw, _pitch, _walk);
        using (var stream = _bitmap.PixelBuffer.AsStream())
            stream.Write(_pixels, 0, _pixels.Length);
        _bitmap.Invalidate();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = CapturePointer(e.Pointer);
        _last = e.GetCurrentPoint(this).Position;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;
        var p = e.GetCurrentPoint(this).Position;
        _yaw += (float)(p.X - _last.X) * 0.8f;
        _pitch = Math.Clamp(_pitch + (float)(p.Y - _last.Y) * 0.4f, -45f, 45f);
        _last = p;
        _dirty = true;
    }

    private void OnPointerEnded(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        ReleasePointerCapture(e.Pointer);
    }
}
