using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace KlyraOptimizer.Views;

/// <summary>
/// Anel de carregamento autentico do Windows 10 (~4.0s para 2 voltas completas,
/// agrupamento/desaceleracao centralizado exatamente no topo 0° / 12h e
/// passagem continua sem engasgo pela parte inferior entre as voltas).
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
        var dotCount = side >= 34 ? 6 : 5;
        var dotRadius = Math.Max(1.35, side * 0.072);
        var orbitRadius = Math.Max(2.0, (side * 0.5) - dotRadius - 0.6);
        var cx = w * 0.5;
        var cy = h * 0.5;

        // Ciclo autentico do Windows 10 (~4.0s para 2 voltas + pausa curta)
        const double cycleDuration = 4.00;
        const double dotStagger = 0.185;
        const double dotLifetime = 3.05;

        var elapsed = _clock.IsRunning ? _clock.Elapsed.TotalSeconds : 0.85;
        var cycleTime = elapsed % cycleDuration;

        for (var i = 0; i < dotCount; i++)
        {
            var local = cycleTime - (i * dotStagger);
            if (local < 0 || local > dotLifetime) continue;

            var u = local / dotLifetime; // 0.0 .. 1.0 ao longo das 2 voltas
            var angleDeg = ComputeWin10RingAngle(u) - (i * 6.0);
            var rad = angleDeg * (Math.PI / 180.0);

            var alpha = 1.0;
            if (u < 0.05) alpha = u / 0.05;
            else if (u > 0.94) alpha = Math.Max(0, (1.0 - u) / 0.06);

            var x = cx + (orbitRadius * Math.Sin(rad));
            var y = cy - (orbitRadius * Math.Cos(rad));

            if (alpha < 0.99)
            {
                dc.PushOpacity(alpha);
                dc.DrawEllipse(brush, null, new Point(x, y), dotRadius, dotRadius);
                dc.Pop();
            }
            else
            {
                dc.DrawEllipse(brush, null, new Point(x, y), dotRadius, dotRadius);
            }
        }
    }

    /// <summary>
    /// Curva continua de 2 voltas (720°):
    /// - Inicia em -160° subindo rapido com ease-out ate -35°;
    /// - Desacelera de forma simetrica no topo entre -35° e +35° (centrado em 0° / 12h);
    /// - Acelera com ease-in pela base de +35° ate +200° (-160° + 360°),
    ///   conectando-se na velocidade maxima sem engasgo com o inicio da 2a volta!
    /// </summary>
    private static double ComputeWin10RingAngle(double u)
    {
        var clamped = Math.Clamp(u, 0.0, 1.0);
        var turn = clamped < 0.5 ? 0 : 1;
        var t = clamped < 0.5 ? (clamped * 2.0) : ((clamped - 0.5) * 2.0); // 0.0 .. 1.0 por volta

        const double t1 = 0.27;
        const double t2 = 0.65;

        double localDeg;
        if (t < t1)
        {
            // Subida desacelerando da base (-160°) ate a entrada do topo (-35°): +125°
            var p = t / t1;
            var eased = 1.0 - Math.Pow(1.0 - p, 2.2);
            localDeg = -160.0 + (125.0 * eased);
        }
        else if (t < t2)
        {
            // Agrupamento lento centralizado exatamente em 0° (12h): -35° -> +35° (+70°)
            var p = (t - t1) / (t2 - t1);
            localDeg = -35.0 + (70.0 * p);
        }
        else
        {
            // Descida acelerando de +35° ate +200° (-160° + 360°): +165° em velocidade continua
            var p = (t - t2) / (1.0 - t2);
            var eased = Math.Pow(p, 2.2);
            localDeg = 35.0 + (165.0 * eased);
        }

        return localDeg + (turn * 360.0);
    }
}
