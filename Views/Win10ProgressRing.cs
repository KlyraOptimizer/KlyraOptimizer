using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace KlyraOptimizer.Views;

/// <summary>
/// Reproducao 1:1 do Windows.UI.Xaml.Controls.ProgressRing usado na XAML Controls Gallery
/// (SDK 10.0.19041.0 generic.xaml):
/// - 5 esferas (Small &lt; 36px) ou 6 esferas (Large &gt;= 36px), defasadas em -6° e 167ms;
/// - 7 SplineDoubleKeyFrames exatos com avaliacao Bezier cubica (KeySpline) nativa do UWP:
///   0.000s (-110°) -> 0.433s (10°, spline 0.02,0.33,0.38,0.77) -> 1.200s (93°, linear)
///   -> 1.617s (205°, spline 0.57,0.17,0.95,0.75) -> 2.017s (357°, spline 0,0.19,0.07,0.72)
///   -> 2.783s (439°, linear) -> 3.217s (585°, spline 0,0,0.95,0.37), ciclo total 4.305s.
/// </summary>
public sealed class Win10ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(
            nameof(IsActive),
            typeof(bool),
            typeof(Win10ProgressRing),
            new PropertyMetadata(true, OnStateChanged));

    public static readonly DependencyProperty ForegroundProperty =
        DependencyProperty.Register(
            nameof(Foreground),
            typeof(Brush),
            typeof(Win10ProgressRing),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    private readonly Stopwatch _clock = new();
    private bool _rendering;

    public Win10ProgressRing()
    {
        Width = 20;
        Height = 20;
        MinWidth = 16;
        MinHeight = 16;
        IsHitTestVisible = false;
        Focusable = false;
        SetResourceReference(ForegroundProperty, "App.Accent");

        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => StopRendering();
        IsVisibleChanged += (_, _) => UpdateSubscription();
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Win10ProgressRing ring) ring.UpdateSubscription();
    }

    private void UpdateSubscription()
    {
        if (IsLoaded && IsVisible && IsActive)
        {
            StartRendering();
        }
        else
        {
            StopRendering();
        }
    }

    private void StartRendering()
    {
        if (_rendering) return;
        _rendering = true;
        _clock.Restart();
        CompositionTarget.Rendering += OnFrame;
        InvalidateVisual();
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        _rendering = false;
        _clock.Stop();
        CompositionTarget.Rendering -= OnFrame;
        InvalidateVisual();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!IsVisible || !IsActive)
        {
            StopRendering();
            return;
        }

        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = !double.IsNaN(Width) && Width > 0 ? Width : 20;
        var h = !double.IsNaN(Height) && Height > 0 ? Height : 20;
        return new Size(
            double.IsInfinity(availableSize.Width) ? w : Math.Min(w, availableSize.Width),
            double.IsInfinity(availableSize.Height) ? h : Math.Min(h, availableSize.Height));
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!IsActive || !IsVisible) return;

        var w = ActualWidth > 0 ? ActualWidth : (!double.IsNaN(Width) ? Width : 20);
        var h = ActualHeight > 0 ? ActualHeight : (!double.IsNaN(Height) ? Height : 20);
        var side = Math.Min(w, h);
        if (side < 6) return;

        var brush = Foreground ?? Brushes.DodgerBlue;

        // SizeStates de generic.xaml: Small (< 36px -> 5 esferas) / Large (>= 36px -> 6 esferas)
        var dotCount = side >= 36.0 ? 6 : 5;

        // ProgressRingTemplateSettings (EllipseDiameter & EllipseOffset)
        var dotDiameter = side <= 20.0 ? 2.4 : (side <= 30.0 ? 3.0 : side * 0.10);
        var dotRadius = Math.Max(1.15, dotDiameter * 0.5);
        var orbitRadius = Math.Max(2.0, (side * 0.5) - dotRadius - 0.5);
        var cx = w * 0.5;
        var cy = h * 0.5;

        // Duracao exata do Storyboard em generic.xaml: BeginTime(E6)=0.835s + KeyTime=3.470s = 4.305s
        const double cycleDuration = 4.305;
        const double dotStagger = 0.167;
        const double activeDuration = 3.217;

        var elapsed = _clock.IsRunning ? _clock.Elapsed.TotalSeconds : 0.90;
        var cycleTime = elapsed % cycleDuration;

        for (var i = 0; i < dotCount; i++)
        {
            var local = cycleTime - (i * dotStagger);
            if (local < 0.0 || local >= activeDuration) continue;

            var angleDeg = EvaluateUwpRingAngle(local) - (i * 6.0);
            var rad = angleDeg * (Math.PI / 180.0);

            var x = cx + (orbitRadius * Math.Sin(rad));
            var y = cy - (orbitRadius * Math.Cos(rad));

            dc.DrawEllipse(brush, null, new Point(x, y), dotRadius, dotRadius);
        }
    }

    /// <summary>
    /// Avalia exatamente os 7 SplineDoubleKeyFrames de E1R em generic.xaml (linhas 12529-12537).
    /// </summary>
    private static double EvaluateUwpRingAngle(double t)
    {
        if (t <= 0.0) return -110.0;

        if (t < 0.433)
        {
            var u = t / 0.433;
            return Lerp(-110.0, 10.0, EvaluateKeySpline(u, 0.02, 0.33, 0.38, 0.77));
        }

        if (t < 1.200)
        {
            var u = (t - 0.433) / (1.200 - 0.433);
            return Lerp(10.0, 93.0, u);
        }

        if (t < 1.617)
        {
            var u = (t - 1.200) / (1.617 - 1.200);
            return Lerp(93.0, 205.0, EvaluateKeySpline(u, 0.57, 0.17, 0.95, 0.75));
        }

        if (t < 2.017)
        {
            var u = (t - 1.617) / (2.017 - 1.617);
            return Lerp(205.0, 357.0, EvaluateKeySpline(u, 0.00, 0.19, 0.07, 0.72));
        }

        if (t < 2.783)
        {
            var u = (t - 2.017) / (2.783 - 2.017);
            return Lerp(357.0, 439.0, u);
        }

        {
            var u = Math.Clamp((t - 2.783) / (3.217 - 2.783), 0.0, 1.0);
            return Lerp(439.0, 585.0, EvaluateKeySpline(u, 0.00, 0.00, 0.95, 0.37));
        }
    }

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);

    /// <summary>
    /// Resolve Y(X) de uma curva Bezier cubica de KeySpline (0,0)-(x1,y1)-(x2,y2)-(1,1) via Newton-Raphson.
    /// </summary>
    internal static double EvaluateKeySpline(double x, double x1, double y1, double x2, double y2)
    {
        if (x <= 0.0) return 0.0;
        if (x >= 1.0) return 1.0;
        if (Math.Abs(x1 - y1) < 1e-6 && Math.Abs(x2 - y2) < 1e-6) return x;

        var s = x;
        for (var iter = 0; iter < 6; iter++)
        {
            var inv = 1.0 - s;
            var bx = (3.0 * inv * inv * s * x1) + (3.0 * inv * s * s * x2) + (s * s * s) - x;
            if (Math.Abs(bx) < 1e-7) break;

            var dbx = (3.0 * inv * inv * x1) + (6.0 * inv * s * (x2 - x1)) + (3.0 * s * s * (1.0 - x2));
            if (Math.Abs(dbx) < 1e-7) break;

            s = Math.Clamp(s - (bx / dbx), 0.0, 1.0);
        }

        var u = 1.0 - s;
        return (3.0 * u * u * s * y1) + (3.0 * u * s * s * y2) + (s * s * s);
    }
}
