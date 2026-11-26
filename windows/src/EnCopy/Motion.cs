using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EnCopy;

public readonly record struct SpringWeight(double Stiffness, double Damping)
{
    public double SettlingRatio => Stiffness > 0 ? Damping / (2.0 * Math.Sqrt(Stiffness)) : 0.0;

    public static SpringWeight FromResponse(double responseSeconds, double dampingFraction)
    {
        double omega = 2.0 * Math.PI / Math.Max(0.05, responseSeconds);
        return new SpringWeight(omega * omega, 2.0 * dampingFraction * omega);
    }
}

public sealed class SpringValue
{
    public double Position { get; set; }
    public double Velocity { get; set; }
    public double Target { get; set; }
    public SpringWeight Weight { get; set; }

    public SpringValue(double initial = 0.0, SpringWeight? weight = null)
    {
        Position = initial;
        Target = initial;
        Weight = weight ?? new SpringWeight(46.0, 2.8);
    }

    public bool IsAtRest(double epsilon = 0.002, double? velocityEpsilon = null) =>
        Math.Abs(Position - Target) <= epsilon && Math.Abs(Velocity) <= (velocityEpsilon ?? epsilon * 4.0);

    public void Snap(double value)
    {
        Position = value;
        Target = value;
        Velocity = 0.0;
    }

    public bool Step(double dtSeconds, double epsilon = 0.002, double? velocityEpsilon = null)
    {
        if (IsAtRest(epsilon, velocityEpsilon))
        {
            Position = Target;
            Velocity = 0.0;
            return false;
        }

        double clamped = Math.Clamp(dtSeconds, 0.0, 0.05);
        if (clamped <= 0.0)
        {
            return true;
        }

        const double maxSubStep = 1.0 / 240.0;
        int steps = Math.Max(1, (int)Math.Ceiling(clamped / maxSubStep));
        double h = clamped / steps;

        double pos = Position;
        double vel = Velocity;
        double k = Weight.Stiffness;
        double c = Weight.Damping;
        double target = Target;

        for (int i = 0; i < steps; i++)
        {
            double acc = -k * (pos - target) - c * vel;
            vel += acc * h;
            pos += vel * h;
        }

        Position = pos;
        Velocity = vel;

        if (IsAtRest(epsilon, velocityEpsilon))
        {
            Position = Target;
            Velocity = 0.0;
            return false;
        }
        return true;
    }

    public static double MeasureSettleTime(SpringWeight weight, double initialDisplacement = 10.0, double stepSeconds = 1.0 / 120.0)
    {
        if (weight.Stiffness <= 0 || weight.Damping <= 0)
        {
            return 0.0;
        }
        var spring = new SpringValue(initialDisplacement, weight) { Target = 0.0 };
        double elapsed = 0.0;
        const double maxSeconds = 25.0;
        while (elapsed < maxSeconds && spring.Step(stepSeconds, epsilon: 0.01))
        {
            elapsed += stepSeconds;
        }
        return elapsed;
    }
}

/// <summary>
/// 对齐 Mac 版 Motion.swift（T-006 / T-060）：
/// 按键在按下当帧立即生效、不排队；五种纸按自身轻重摆动；系统关闭「动画效果」时仅保留淡入淡出。
/// </summary>
public static class Motion
{
    public const double TuckDuration = 0.16;
    public const double FadeDuration = 0.12;
    public const double HopHeight = 7.0;
    public const double RopeTaut = 0.15;
    public const double BasketTaut = 0.60;
    public const double PegOpenScale = 1.30;
    public const double FlyOutDuration = 0.30;
    public const double FlyOutScale = 0.16;
    public const double FallDuration = 0.42;
    public const double FallSink = 5.0;
    public const double FallSinkShare = 0.10;
    public const double BreezeQuietSeconds = 4.0;
    public const double TraySwayDuration = 0.60;
    public static readonly float[] TraySwayAngles = [8f, -6f, 4f, -2f, 0f];

    public static readonly SpringWeight SummonSpring = SpringWeight.FromResponse(0.28, 0.86);
    public static readonly SpringWeight SlideDownSpring = SpringWeight.FromResponse(0.42, 0.82);
    public static readonly SpringWeight RopeSummonSpring = SpringWeight.FromResponse(0.30, 0.85);
    public static readonly SpringWeight RopeSlideSpring = SpringWeight.FromResponse(0.50, 0.50);
    public static readonly SpringWeight PegClampSpring = SpringWeight.FromResponse(0.25, 0.60);
    public static readonly SpringWeight HopSpring = SpringWeight.FromResponse(0.42, 0.72);

    private static bool? _reducedOverride;

    public static void SetReducedOverride(bool? reduced) => _reducedOverride = reduced;

    public static bool IsReduced => _reducedOverride ?? !SystemParameters.ClientAreaAnimation;

    public static SpringWeight WeightFor(PaperKind paper) => paper switch
    {
        PaperKind.Receipt => new SpringWeight(38.0, 2.4),
        PaperKind.Tag => new SpringWeight(42.0, 2.6),
        PaperKind.Polaroid => new SpringWeight(46.0, 2.8),
        PaperKind.Tractor => new SpringWeight(50.0, 3.0),
        PaperKind.Folder => new SpringWeight(58.0, 3.4),
        _ => new SpringWeight(46.0, 2.8)
    };

    public static (double Sink, double Gravity) FallProgress(double raw)
    {
        double clamped = Math.Clamp(raw, 0.0, 1.0);
        double s = Math.Min(1.0, clamped / FallSinkShare);
        double sink = FallSink * (1.0 - (1.0 - s) * (1.0 - s));
        double g = clamped <= FallSinkShare ? 0.0 : (clamped - FallSinkShare) / (1.0 - FallSinkShare);
        return (sink, g * g);
    }

    public static bool CanBreeze(DateTime nowUtc, DateTime lastKeyUtc, bool? reduced = null) =>
        !(reduced ?? IsReduced) && (nowUtc - lastKeyUtc).TotalSeconds >= BreezeQuietSeconds;

    public static (double MinDeg, double MaxDeg) GustAmplitude(bool summoned) =>
        summoned ? (0.8, 1.7) : (1.6, 3.4);
}

/// <summary>
/// 按显示器刷新率（CompositionTarget.Rendering）推进面板、绳索与每张卡片的弹簧状态；
/// 全部弹簧与淡入淡出到达稳态后当帧立即退订 CompositionTarget.Rendering，空闲零空转。
/// </summary>
public sealed class MotionEngine : IDisposable
{
    private readonly SpringValue _panelSlide = new(0.0, Motion.SummonSpring);
    private readonly SpringValue _ropeSag = new(1.0, Motion.RopeSummonSpring);
    private readonly Dictionary<string, SpringValue> _cardSways = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SpringValue> _cardHops = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SpringValue> _pegScales = new(StringComparer.OrdinalIgnoreCase);

    private bool _subscribed;
    private bool _disposed;
    private TimeSpan? _lastRenderTime;

    private bool _linearActive;
    private double _linearElapsed;
    private double _linearDuration;
    private double _linearStartSlideY;
    private double _linearEndSlideY;
    private double _linearStartOpacity = 1.0;
    private double _linearEndOpacity = 1.0;
    private Action? _onLinearCompleted;

    public double PanelSlideY { get; private set; }
    public double PanelOpacity { get; private set; } = 1.0;
    public double RopeSagScale => _ropeSag.Position;
    public bool IsActive { get; private set; }
    public bool IsRenderingSubscribed => _subscribed;

    public event Action? FrameUpdated;

    public double GetCardSway(string path) =>
        _cardSways.TryGetValue(path, out var s) ? s.Position : 0.0;

    public double GetCardHop(string path) =>
        _cardHops.TryGetValue(path, out var s) ? s.Position : 0.0;

    public double GetPegScale(string path) =>
        _pegScales.TryGetValue(path, out var s) ? s.Position : 1.0;

    public void BeginReveal(bool bySummon, double panelHeight, IReadOnlyList<Note>? visibleCards = null)
    {
        _onLinearCompleted = null;
        if (Motion.IsReduced)
        {
            _panelSlide.Snap(0.0);
            _ropeSag.Snap(1.0);
            _cardSways.Clear();
            _cardHops.Clear();
            _pegScales.Clear();

            PanelSlideY = 0.0;
            PanelOpacity = 0.0;
            _linearActive = true;
            _linearElapsed = 0.0;
            _linearDuration = Motion.FadeDuration;
            _linearStartSlideY = 0.0;
            _linearEndSlideY = 0.0;
            _linearStartOpacity = 0.0;
            _linearEndOpacity = 1.0;
            ActivateLoop();
            FrameUpdated?.Invoke();
            return;
        }

        _linearActive = false;
        PanelOpacity = 1.0;
        double startY = -Math.Min(180.0, Math.Max(60.0, panelHeight * 0.55));
        _panelSlide.Weight = bySummon ? Motion.SummonSpring : Motion.SlideDownSpring;
        _panelSlide.Position = startY;
        _panelSlide.Velocity = 0.0;
        _panelSlide.Target = 0.0;
        PanelSlideY = startY;

        _ropeSag.Weight = bySummon ? Motion.RopeSummonSpring : Motion.RopeSlideSpring;
        _ropeSag.Position = Motion.RopeTaut;
        _ropeSag.Velocity = 0.0;
        _ropeSag.Target = 1.0;

        if (visibleCards is { Count: > 0 })
        {
            double baseAmp = bySummon ? 2.2 : 6.2;
            for (int i = 0; i < visibleCards.Count; i++)
            {
                var card = visibleCards[i];
                double sign = (i % 2 == 0) ? 1.0 : -1.0;
                var sway = GetOrCreateSway(card.FilePath, card.Paper);
                sway.Position = sign * baseAmp;
                sway.Target = 0.0;
            }
        }

        ActivateLoop();
        FrameUpdated?.Invoke();
    }

    public void BeginTuck(double panelHeight, Action? onCompleted = null)
    {
        _panelSlide.Snap(0.0);
        _linearActive = true;
        _linearElapsed = 0.0;
        _linearStartSlideY = PanelSlideY;
        _linearStartOpacity = PanelOpacity;
        _onLinearCompleted = onCompleted;

        if (Motion.IsReduced)
        {
            _linearDuration = Motion.FadeDuration;
            _linearEndSlideY = 0.0;
            _linearEndOpacity = 0.0;
        }
        else
        {
            _linearDuration = Motion.TuckDuration;
            _linearEndSlideY = -Math.Min(140.0, Math.Max(48.0, panelHeight * 0.42));
            _linearEndOpacity = 0.0;
        }

        ActivateLoop();
        FrameUpdated?.Invoke();
    }

    public void BounceRope(double fromTaut, bool bySummon)
    {
        if (Motion.IsReduced) return;
        _ropeSag.Weight = bySummon ? Motion.RopeSummonSpring : Motion.RopeSlideSpring;
        _ropeSag.Position = fromTaut;
        _ropeSag.Velocity = 0.0;
        _ropeSag.Target = 1.0;
        ActivateLoop();
        FrameUpdated?.Invoke();
    }

    public void TriggerNewCard(string path, PaperKind paper)
    {
        if (Motion.IsReduced) return;
        var peg = GetOrCreatePeg(path);
        peg.Position = Motion.PegOpenScale;
        peg.Velocity = 0.0;
        peg.Target = 1.0;

        var sway = GetOrCreateSway(path, paper);
        sway.Position = -4.2;
        sway.Velocity = 0.0;
        sway.Target = 0.0;

        ActivateLoop();
        FrameUpdated?.Invoke();
    }

    public void TriggerHop(string path)
    {
        if (Motion.IsReduced) return;
        if (!_cardHops.TryGetValue(path, out var hop))
        {
            hop = new SpringValue(0.0, Motion.HopSpring);
            _cardHops[path] = hop;
        }
        hop.Weight = Motion.HopSpring;
        hop.Position = -Motion.HopHeight;
        hop.Velocity = 0.0;
        hop.Target = 0.0;

        ActivateLoop();
        FrameUpdated?.Invoke();
    }

    public void TriggerGust(IReadOnlyList<Note> visibleCards, bool summoned, Random? rng = null)
    {
        if (Motion.IsReduced || visibleCards.Count == 0) return;
        var random = rng ?? Random.Shared;
        var (minDeg, maxDeg) = Motion.GustAmplitude(summoned);
        double direction = random.NextDouble() < 0.5 ? -1.0 : 1.0;

        foreach (var card in visibleCards)
        {
            double mag = minDeg + random.NextDouble() * (maxDeg - minDeg);
            var sway = GetOrCreateSway(card.FilePath, card.Paper);
            sway.Velocity += direction * mag * 9.0;
            sway.Target = 0.0;
        }

        ActivateLoop();
        FrameUpdated?.Invoke();
    }

    public void StepManual(double dtSeconds)
    {
        Advance(dtSeconds);
    }

    public void CancelLinearWithoutCallback()
    {
        _linearActive = false;
        _onLinearCompleted = null;
    }

    public void SnapAllToRest(bool invokePendingCompletion = false)
    {
        _linearActive = false;
        var cb = invokePendingCompletion ? _onLinearCompleted : null;
        _onLinearCompleted = null;
        _panelSlide.Snap(0.0);
        _ropeSag.Snap(1.0);
        PanelSlideY = 0.0;
        PanelOpacity = 1.0;
        _cardSways.Clear();
        _cardHops.Clear();
        _pegScales.Clear();
        StopLoop();
        cb?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _onLinearCompleted = null;
        StopLoop();
    }

    private SpringValue GetOrCreateSway(string path, PaperKind paper)
    {
        var w = Motion.WeightFor(paper);
        if (!_cardSways.TryGetValue(path, out var s))
        {
            s = new SpringValue(0.0, w);
            _cardSways[path] = s;
        }
        else
        {
            s.Weight = w;
        }
        return s;
    }

    private SpringValue GetOrCreatePeg(string path)
    {
        if (!_pegScales.TryGetValue(path, out var p))
        {
            p = new SpringValue(1.0, Motion.PegClampSpring);
            _pegScales[path] = p;
        }
        else
        {
            p.Weight = Motion.PegClampSpring;
        }
        return p;
    }

    private void ActivateLoop()
    {
        if (_disposed) return;
        IsActive = true;
        if (!_subscribed)
        {
            _lastRenderTime = null;
            CompositionTarget.Rendering += OnRendering;
            _subscribed = true;
        }
    }

    private void StopLoop()
    {
        IsActive = false;
        _lastRenderTime = null;
        if (_subscribed)
        {
            CompositionTarget.Rendering -= OnRendering;
            _subscribed = false;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_disposed || !IsActive)
        {
            StopLoop();
            return;
        }

        double dt = 1.0 / 60.0;
        if (e is RenderingEventArgs rea)
        {
            if (_lastRenderTime is { } prev && rea.RenderingTime > prev)
            {
                dt = (rea.RenderingTime - prev).TotalSeconds;
            }
            _lastRenderTime = rea.RenderingTime;
        }
        Advance(dt);
    }

    private void Advance(double dtSeconds)
    {
        bool moving = false;
        Action? completedCallback = null;

        if (_linearActive)
        {
            _linearElapsed += Math.Clamp(dtSeconds, 0.0, 0.05);
            double t = _linearDuration <= 0.001 ? 1.0 : Math.Clamp(_linearElapsed / _linearDuration, 0.0, 1.0);
            // Ease-out cubic (0.23, 1, 0.32, 1 approx)
            double eased = 1.0 - Math.Pow(1.0 - t, 3.0);
            PanelSlideY = _linearStartSlideY + (_linearEndSlideY - _linearStartSlideY) * eased;
            PanelOpacity = _linearStartOpacity + (_linearEndOpacity - _linearStartOpacity) * eased;
            if (t >= 1.0)
            {
                _linearActive = false;
                PanelSlideY = _linearEndSlideY;
                PanelOpacity = _linearEndOpacity;
                completedCallback = _onLinearCompleted;
                _onLinearCompleted = null;
            }
            else
            {
                moving = true;
            }
        }
        else
        {
            if (_panelSlide.Step(dtSeconds, epsilon: 0.05))
            {
                moving = true;
            }
            PanelSlideY = _panelSlide.Position;
        }

        if (_ropeSag.Step(dtSeconds, epsilon: 0.002))
        {
            moving = true;
        }

        StepMap(_cardSways, dtSeconds, restEpsilon: 0.04, velocityEpsilon: 0.20, ref moving);
        StepMap(_cardHops, dtSeconds, restEpsilon: 0.05, velocityEpsilon: 0.25, ref moving);
        StepMap(_pegScales, dtSeconds, restEpsilon: 0.005, velocityEpsilon: 0.03, ref moving);

        if (!moving)
        {
            StopLoop();
        }

        FrameUpdated?.Invoke();
        completedCallback?.Invoke();
    }

    private static void StepMap(Dictionary<string, SpringValue> map, double dt, double restEpsilon, double velocityEpsilon, ref bool moving)
    {
        if (map.Count == 0) return;
        List<string>? settledKeys = null;
        foreach (var (key, spring) in map)
        {
            if (spring.Step(dt, restEpsilon, velocityEpsilon))
            {
                moving = true;
            }
            else
            {
                settledKeys ??= [];
                settledKeys.Add(key);
            }
        }
        if (settledKeys is not null)
        {
            foreach (var k in settledKeys)
            {
                map.Remove(k);
            }
        }
    }
}

/// <summary>
/// 离绳飞行与重力下坠叠层窗口（WS_EX_TRANSPARENT | WS_EX_NOACTIVATE）：
/// ⏎ 选中卡片缩小飞向指针（粘贴已在第 0 帧立即发出），Ctrl+Backspace 沉降后重力坠落淡出，Held 按 Esc 飞回原槽位。
/// </summary>
public static class FlightOverlay
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    public static void FlyToCursor(UIElement? cardVisual, Rect fromScreenDip, Point toScreenDip)
    {
        if (Motion.IsReduced || IsInChecksRunner() || cardVisual is null) return;
        if (fromScreenDip.Width <= 4 || fromScreenDip.Height <= 4) return;
        var snapshot = CaptureElement(cardVisual, (int)Math.Ceiling(fromScreenDip.Width), (int)Math.Ceiling(fromScreenDip.Height));
        if (snapshot is null) return;

        RunOverlay(fromScreenDip, snapshot, Motion.FlyOutDuration, raw =>
        {
            double eased = 1.0 - Math.Pow(1.0 - raw, 3.0);
            double scale = 1.0 + (Motion.FlyOutScale - 1.0) * eased;
            double cx = fromScreenDip.Left + fromScreenDip.Width / 2.0 + (toScreenDip.X - (fromScreenDip.Left + fromScreenDip.Width / 2.0)) * eased;
            double cy = fromScreenDip.Top + fromScreenDip.Height / 2.0 + (toScreenDip.Y - (fromScreenDip.Top + fromScreenDip.Height / 2.0)) * eased;
            return (cx - fromScreenDip.Width / 2.0, cy - fromScreenDip.Height / 2.0, scale, 0.0, 1.0 - raw * 0.65);
        });
    }

    public static void FallCard(UIElement? cardVisual, Rect fromScreenDip, double initialTilt)
    {
        if (Motion.IsReduced || IsInChecksRunner() || cardVisual is null) return;
        if (fromScreenDip.Width <= 4 || fromScreenDip.Height <= 4) return;
        var snapshot = CaptureElement(cardVisual, (int)Math.Ceiling(fromScreenDip.Width), (int)Math.Ceiling(fromScreenDip.Height));
        if (snapshot is null) return;

        RunOverlay(fromScreenDip, snapshot, Motion.FallDuration, raw =>
        {
            var (sink, gravity) = Motion.FallProgress(raw);
            double dy = sink + 420.0 * gravity;
            double extraTilt = (initialTilt * 4.0 + 16.0) * gravity;
            return (fromScreenDip.Left, fromScreenDip.Top + dy, 1.0, extraTilt, Math.Max(0.0, 1.0 - gravity));
        });
    }

    public static void FlyBackToLine(BitmapSource? snapshot, Rect fromScreenDip, Rect toScreenDip)
    {
        if (Motion.IsReduced || IsInChecksRunner() || snapshot is null) return;
        RunOverlay(fromScreenDip, snapshot, 0.24, raw =>
        {
            double eased = 1.0 - Math.Pow(1.0 - raw, 3.0);
            double x = fromScreenDip.Left + (toScreenDip.Left - fromScreenDip.Left) * eased;
            double y = fromScreenDip.Top + (toScreenDip.Top - fromScreenDip.Top) * eased;
            double scale = 1.0 + ((toScreenDip.Width / Math.Max(1.0, fromScreenDip.Width)) - 1.0) * eased;
            return (x, y, Math.Clamp(scale, 0.2, 2.0), 0.0, 1.0 - raw * 0.35);
        });
    }

    public static BitmapSource? CaptureElement(UIElement element, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(element);
        rtb.Freeze();
        return rtb;
    }

    private static void RunOverlay(
        Rect initialDip,
        BitmapSource snapshot,
        double durationSeconds,
        Func<double, (double Left, double Top, double Scale, double Angle, double Opacity)> stepFn)
    {
        var scaleTx = new ScaleTransform(1.0, 1.0);
        var rotateTx = new RotateTransform(0.0);
        var image = new Image
        {
            Source = snapshot,
            Width = initialDip.Width,
            Height = initialDip.Height,
            Stretch = Stretch.Fill,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { scaleTx, rotateTx } }
        };

        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Width = initialDip.Width + 60,
            Height = initialDip.Height + 60,
            Left = initialDip.Left - 30,
            Top = initialDip.Top - 30,
            IsHitTestVisible = false,
            Content = new Grid { Children = { image } }
        };
        win.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(win).Handle;
            int ex = GetWindowLong(hwnd, GwlExStyle);
            SetWindowLong(hwnd, GwlExStyle, ex | WsExTransparent | WsExToolWindow | WsExNoActivate);
        };

        win.Show();
        var start = DateTime.UtcNow;
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            double raw = Math.Clamp((DateTime.UtcNow - start).TotalSeconds / Math.Max(0.05, durationSeconds), 0.0, 1.0);
            var (left, top, scale, angle, opacity) = stepFn(raw);
            win.Left = left - 30;
            win.Top = top - 30;
            scaleTx.ScaleX = scale;
            scaleTx.ScaleY = scale;
            rotateTx.Angle = angle;
            image.Opacity = Math.Clamp(opacity, 0.0, 1.0);
            if (raw >= 1.0)
            {
                timer.Stop();
                win.Close();
            }
        };
        timer.Start();
    }

    private static bool IsInChecksRunner() =>
        string.Equals(Environment.GetEnvironmentVariable(DataFolder.ChecksRunnerEnv), "1", StringComparison.Ordinal);

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
