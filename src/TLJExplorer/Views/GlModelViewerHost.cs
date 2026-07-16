using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Silk.NET.Maths;
using TLJExplorer.Rendering;
using TLJExplorer.Core.Formats;

namespace TLJExplorer.Views;

/// <summary>
/// Avalonia control that hosts a 3D render of a <see cref="CirModel"/> via <see cref="ModelRenderer"/>. The GL
/// context renders offscreen into an FBO and the resulting pixels are blitted into a <see cref="WriteableBitmap"/>
/// on demand (see <see cref="ModelRenderer"/> for the rationale). Left-drag orbits, right-drag pans, wheel zooms.
/// </summary>
public sealed class GlModelViewerHost : ContentControl, IDisposable
{
    private const float OrbitSensitivity = 0.01f;
    private const float PanSensitivityFactor = 0.0025f;
    private const float ZoomStep = 1.15f;
    private const float MinPitch = -1.55f;
    private const float MaxPitch = 1.55f;

    private readonly Image _image = new() { Stretch = Stretch.Uniform };

    // Lazily created on first actual use (loading a model) rather than a field initializer: constructing
    // a ModelRenderer creates a hidden GLFW window + OpenGL context, which takes several hundred ms --
    // eating into it eagerly here would delay showing the main window, since this control is built as
    // part of MainWindow's visual tree regardless of which content panel is initially active.
    private ModelRenderer? _rendererBacking;
    private ModelRenderer Renderer => _rendererBacking ??= new ModelRenderer
    {
        ClearColor = _pendingClearColor,
        ShowWireframe = _pendingShowWireframe,
    };

    private (float R, float G, float B, float A) _pendingClearColor = (0.16f, 0.16f, 0.18f, 1f);
    private bool _pendingShowWireframe;

    private WriteableBitmap? _bitmap;
    private bool _hasModel;
    private bool _dirty;
    private bool _frameLoopActive;

    private Point? _lastPointerPos;
    private bool _orbiting;
    private bool _panning;

    private float _yaw = -0.6f;
    private float _pitch = 0.35f;
    private float _distance = 5f;
    private Vector3D<float> _target = Vector3D<float>.Zero;

    // _timeMs advances from the compositor's per-frame RequestAnimationFrame timestamp so playback stays
    // synchronized with the rest of Avalonia's animation system.
    private CirModel? _model;
    private AniAnimation? _animation;
    private float _timeMs;
    private float _progress;
    private bool _isPlaying;
    private TimeSpan? _lastRenderingTime;

    public GlModelViewerHost()
    {
        Focusable = true;
        ClipToBounds = true;
        Background = Brushes.Transparent;
        Content = _image;

        PointerPressed += OnPointerPressed;
        PointerReleased += OnPointerReleased;
        PointerMoved += OnPointerMoved;
        PointerWheelChanged += OnPointerWheelChanged;
        PointerCaptureLost += (_, _) => { _orbiting = false; _panning = false; _lastPointerPos = null; };

        SizeChanged += (_, _) => RequestRedraw();
        Loaded += (_, _) => HookFrameLoop();
        Unloaded += (_, _) => UnhookFrameLoop();
    }

    private void HookFrameLoop()
    {
        if (_frameLoopActive)
            return;

        _frameLoopActive = true;
        RequestNextFrame();
    }

    private void UnhookFrameLoop() => _frameLoopActive = false;

    private void RequestNextFrame()
    {
        if (!_frameLoopActive)
            return;

        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
    }

    /// <summary>
    /// Schedules construction of the (expensive: hidden GLFW window + OpenGL context) renderer at
    /// Background dispatcher priority, so it happens after the current UI frame has been rendered rather
    /// than stealing time from it. Call once, shortly after the owning window has loaded -- the goal is to
    /// have the renderer ready by the time the user actually opens a model, without delaying first paint.
    /// </summary>
    public void WarmUp() => Dispatcher.UIThread.Post(() => _ = Renderer, DispatcherPriority.Background);

    /// <summary>Uploads <paramref name="model"/>'s geometry and frames the orbit camera around it.</summary>
    public void LoadModel(CirModel model)
    {
        Renderer.LoadModel(model);
        _model = model;
        _hasModel = true;

        ClearAnimationState();

        // Force a fresh WriteableBitmap on the next render, otherwise the Image control can render stale
        // content across a model swap.
        _bitmap = null;
        _image.Source = null;

        // Framing on identity-pose bounds here is a fallback; LoadAnimation re-frames on the actual posed
        // bounds once an animation is applied, which is what the user will see rendered.
        FrameCameraOnCurrentBounds();

        RequestRedraw();
    }

    /// <summary>Positions the orbit camera to comfortably show the current
    /// <see cref="ModelRenderer.BoundsCenter"/> / <see cref="ModelRenderer.BoundsRadius"/>.</summary>
    private void FrameCameraOnCurrentBounds()
    {
        _target = Renderer.BoundsCenter;
        _distance = Math.Max(0.05f, Renderer.BoundsRadius * 2.2f);
        _yaw = -0.6f;
        _pitch = 0.35f;
    }

    /// <summary>Clears the current model.</summary>
    public void ClearModel()
    {
        _hasModel = false;
        _model = null;
        ClearAnimationState();
        _image.Source = null;
    }

    /// <summary>One of the six standard orthographic axis-aligned camera directions.</summary>
    public enum ViewDirection { Front, Back, Left, Right, Top, Bottom }

    /// <summary>
    /// Colour the viewport clears to before rendering the mesh. Setting this before a model has ever
    /// been loaded just records the preference -- it does not force the (expensive) renderer to spin up.
    /// </summary>
    public (float R, float G, float B, float A) ClearColor
    {
        get => _rendererBacking?.ClearColor ?? _pendingClearColor;
        set
        {
            _pendingClearColor = value;
            if (_rendererBacking is not null)
            {
                _rendererBacking.ClearColor = value;
                RequestRedraw();
            }
        }
    }

    /// <summary>
    /// Overlay the shaded model with a wireframe pass. Setting this before a model has ever been loaded
    /// just records the preference -- it does not force the (expensive) renderer to spin up.
    /// </summary>
    public bool ShowWireframe
    {
        get => _rendererBacking?.ShowWireframe ?? _pendingShowWireframe;
        set
        {
            _pendingShowWireframe = value;
            if (_rendererBacking is not null)
            {
                _rendererBacking.ShowWireframe = value;
                RequestRedraw();
            }
        }
    }

    /// <summary>
    /// Renders the current model at native panel resolution and returns a top-down BGRA32 pixel buffer.
    /// Useful for saving a screenshot at the display's actual size regardless of Avalonia DPI scaling.
    /// </summary>
    public byte[]? RenderCurrentFrameToBytes(out int width, out int height)
    {
        width = Math.Max(1, (int)Math.Round(Bounds.Width));
        height = Math.Max(1, (int)Math.Round(Bounds.Height));
        if (!_hasModel || width <= 1 || height <= 1)
            return null;
        var camera = new OrbitCamera(_target, _yaw, _pitch, _distance);
        return Renderer.RenderFrame(width, height, camera);
    }

    /// <summary>Reframes the orbit camera on the current mesh bounds, keeping the current yaw/pitch.</summary>
    public void FrameCurrent()
    {
        if (!_hasModel)
            return;

        _target = Renderer.BoundsCenter;
        _distance = Math.Max(0.05f, Renderer.BoundsRadius * 2.2f);
        RequestRedraw();
    }

    /// <summary>Restores the default 3/4 orbit angle AND reframes on the mesh bounds — the "get me back to a
    /// known good view" button.</summary>
    public void ResetView()
    {
        _yaw = -0.6f;
        _pitch = 0.35f;
        FrameCurrent();
    }

    /// <summary>Snaps the orbit camera to one of the six standard axis-aligned views and reframes on the
    /// current bounds. The reframing keeps the model visible even if it was panned off-centre first.</summary>
    public void SetOrthographicView(ViewDirection direction)
    {
        (float yaw, float pitch) = direction switch
        {
            ViewDirection.Front => (0f, 0f),
            ViewDirection.Back => (MathF.PI, 0f),
            ViewDirection.Right => (MathF.PI / 2f, 0f),
            ViewDirection.Left => (-MathF.PI / 2f, 0f),
            ViewDirection.Top => (0f, MaxPitch),    // MinPitch/MaxPitch stay just shy of ±π/2 to avoid gimbal lock
            ViewDirection.Bottom => (0f, MinPitch),
            _ => (_yaw, _pitch),
        };

        _yaw = yaw;
        _pitch = pitch;
        FrameCurrent();
    }

    public bool IsPlaying => _isPlaying;
    public bool Loop { get; set; } = true;
    public float Progress => _progress;
    public int DurationMs => _animation?.MaxTime ?? 0;
    public bool HasAnimation => _animation is not null;

    /// <summary>
    /// Sets (or clears, via <see langword="null"/>) the current animation and resets playback to time 0.
    /// Does not auto-play -- call <see cref="Play"/> to start playback.
    /// </summary>
    public void LoadAnimation(AniAnimation? animation)
    {
        _animation = animation;
        _timeMs = 0f;
        _isPlaying = false;
        _lastRenderingTime = null;

        ApplyCurrentPose(updateBounds: true);
        if (_model is not null)
            FrameCameraOnCurrentBounds();
        RequestRedraw();
    }

    public void Play()
    {
        if (_animation is null)
            return;

        _isPlaying = true;
        _lastRenderingTime = null; // Fresh delta baseline next tick instead of one spanning the paused gap.
        RequestRedraw();
    }

    public void Pause() => _isPlaying = false;

    public void SeekToFraction(float fraction)
    {
        if (_animation is null)
            return;

        _timeMs = Math.Clamp(fraction, 0f, 1f) * _animation.MaxTime;
        ApplyCurrentPose();
        RequestRedraw();
    }

    private void ClearAnimationState()
    {
        _animation = null;
        _timeMs = 0f;
        _progress = 0f;
        _isPlaying = false;
        _lastRenderingTime = null;

        if (_model is not null)
            Renderer.ApplyPose(SkeletonPoser.IdentityPose(_model.Skeleton.Length));
    }

    /// <summary>Samples the current animation at <see cref="_timeMs"/> and re-skins the renderer. Pass
    /// <paramref name="updateBounds"/>=true only when reframing the camera; leaving it false during ordinary
    /// playback avoids camera jitter as the animation moves the mesh.</summary>
    private void ApplyCurrentPose(bool updateBounds = false)
    {
        if (_model is null)
            return;

        if (_animation is null)
        {
            _progress = 0f;
            Renderer.ApplyPose(SkeletonPoser.IdentityPose(_model.Skeleton.Length), updateBounds);
            return;
        }

        BonePose[] poses = AnimationSampler.Sample(_animation, _timeMs, Loop, out _progress);
        Matrix4x4[] world = SkeletonPoser.Pose(_model.Skeleton, poses);
        Renderer.ApplyPose(world, updateBounds);
    }

    /// <summary>Supplies a resolved texture for a material. Must be called on the UI thread.</summary>
    public void SetMaterialTexture(int materialIndex, DecodedImage? image)
    {
        Renderer.SetMaterialTexture(materialIndex, image);
        RequestRedraw();
    }

    /// <summary>Reverts a single material back to its flat-color fallback.</summary>
    public void ResetMaterialTexture(int materialIndex)
    {
        Renderer.ResetMaterialTexture(materialIndex);
        RequestRedraw();
    }

    /// <summary>Reverts every material back to its flat-color fallback.</summary>
    public void ResetMaterialTextures()
    {
        Renderer.ResetMaterialTextures();
        RequestRedraw();
    }

    private void RequestRedraw() => _dirty = true;

    private void OnFrame(TimeSpan renderingTime)
    {
        if (!_frameLoopActive)
            return;

        if (_isPlaying && _animation is not null)
        {
            if (_lastRenderingTime is { } last)
            {
                double deltaMs = (renderingTime - last).TotalMilliseconds;
                if (deltaMs > 0)
                {
                    _timeMs += (float)deltaMs;
                    ApplyCurrentPose();
                    _dirty = true;
                }
            }

            _lastRenderingTime = renderingTime;
        }

        // Only clear _dirty when a frame actually rendered, so a zero-size layout pass retries next tick
        // instead of sitting blank until some other event calls RequestRedraw().
        if (_dirty && TryRenderNow())
            _dirty = false;

        RequestNextFrame();
    }

    /// <summary>Renders and blits one frame. Returns <see langword="false"/> when the panel has no valid
    /// layout size yet; the caller uses this to retry on the next frame.</summary>
    private unsafe bool TryRenderNow()
    {
        if (!_hasModel)
            return true;

        int width = Math.Max(1, (int)Math.Round(Bounds.Width));
        int height = Math.Max(1, (int)Math.Round(Bounds.Height));
        if (width <= 1 || height <= 1)
            return false;

        var camera = new OrbitCamera(_target, _yaw, _pitch, _distance);
        byte[] pixels = Renderer.RenderFrame(width, height, camera);

        if (_bitmap is null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
        {
            _bitmap = new WriteableBitmap(new PixelSize(width, height), new Avalonia.Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            _image.Source = _bitmap;
        }

        int stride = width * 4;
        using (ILockedFramebuffer fb = _bitmap.Lock())
        {
            if (fb.RowBytes == stride)
            {
                Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
            }
            else
            {
                for (int y = 0; y < height; y++)
                    Marshal.Copy(pixels, y * stride, fb.Address + (y * fb.RowBytes), stride);
            }
        }

        _image.InvalidateVisual();
        return true;
    }

    // -----------------------------------------------------------------------------------------------
    // Pointer-driven orbit camera
    // -----------------------------------------------------------------------------------------------

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerUpdateKind kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (kind == PointerUpdateKind.LeftButtonPressed)
        {
            _orbiting = true;
            _lastPointerPos = e.GetPosition(this);
            e.Pointer.Capture(this);
        }
        else if (kind == PointerUpdateKind.RightButtonPressed)
        {
            _panning = true;
            _lastPointerPos = e.GetPosition(this);
            e.Pointer.Capture(this);
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left)
            _orbiting = false;
        else if (e.InitialPressMouseButton == MouseButton.Right)
            _panning = false;

        if (!_orbiting && !_panning)
            e.Pointer.Capture(null);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_lastPointerPos is not { } last || (!_orbiting && !_panning))
            return;

        Point pos = e.GetPosition(this);
        double dx = pos.X - last.X;
        double dy = pos.Y - last.Y;
        _lastPointerPos = pos;

        if (_orbiting)
        {
            _yaw -= (float)dx * OrbitSensitivity;
            _pitch = Math.Clamp(_pitch - ((float)dy * OrbitSensitivity), MinPitch, MaxPitch);
            RequestRedraw();
        }
        else if (_panning)
        {
            var camera = new OrbitCamera(_target, _yaw, _pitch, _distance);
            Vector3D<float> viewDir = Silk.NET.Maths.Vector3D.Normalize(camera.Target - camera.Eye);
            Vector3D<float> right = Silk.NET.Maths.Vector3D.Normalize(Silk.NET.Maths.Vector3D.Cross(viewDir, Vector3D<float>.UnitY));
            Vector3D<float> up = Silk.NET.Maths.Vector3D.Cross(right, viewDir);

            float panScale = _distance * PanSensitivityFactor;
            _target -= right * ((float)dx * panScale);
            _target += up * ((float)dy * panScale);
            RequestRedraw();
        }
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        _distance = e.Delta.Y > 0 ? _distance / ZoomStep : _distance * ZoomStep;
        _distance = Math.Max(0.001f, _distance);
        RequestRedraw();
        e.Handled = true;
    }

    public void Dispose()
    {
        // Guard the frame-loop unhook so any failure there can't skip renderer disposal — the
        // renderer owns a hidden GLFW window whose thread will keep the process alive if leaked.
        try { UnhookFrameLoop(); } catch { }
        _rendererBacking?.Dispose();
    }
}
