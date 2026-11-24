using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace KlyraOptimizer.Views;

/// <summary>
/// Barra de progresso indeterminada autentica do Windows 10 (5 esferas em uma
/// curva continua de ~4.4s onde o espacamento vem exclusivamente do dotStagger,
/// sem saltos entre as fases de entrada, centro e saida).
/// </summary>
public sealed class Win10ProgressBar : FrameworkElement
{
    public static readonly DependencyProperty IsIndeterminateProperty =
        DependencyProperty.Register(
            nameof(IsIndeterminate),
            typeof(bool),
            typeof(Win10ProgressBar),
            new PropertyMetadata(true, OnStateChanged));

    public static readonly DependencyProperty ForegroundProperty =
        DependencyProperty.Register(
            nameof(Foreground),
            typeof(Brush),
            typeof(Win10ProgressBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsIndeterminate
    {
        get => (bool)GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    private readonly Stopwatch _clock = new();
    private bool _rendering;

    public Win10ProgressBar()
    {
        Height = 6;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        ClipToBounds = true;
        IsHitTestVisible = false;
        Focusable = false;
        SetResourceReference(ForegroundProperty, "App.Accent");

        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => StopRendering();
        IsVisibleChanged += (_, _) => UpdateSubscription();
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Win10ProgressBar bar) bar.UpdateSubscription();
    }

    private void UpdateSubscription()
    {
        if (IsLoaded && IsVisible && IsIndeterminate)
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
        if (!IsVisible || !IsIndeterminate)
        {
            StopRendering();
            return;
        }

        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width;
        var h = !double.IsNaN(Height) && Height > 0 ? Height : 6;
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!IsIndeterminate || !IsVisible) return;

        var width = ActualWidth;
        var height = ActualHeight > 0 ? ActualHeight : (!double.IsNaN(Height) ? Height : 6);
        if (width <= 8 || height <= 1) return;

        var brush = Foreground ?? Brushes.DodgerBlue;
        var dotRadius = Math.Max(1.8, Math.Min(2.5, height * 0.40));
        var cy = height * 0.5;

        // Ritmo autentico do Windows 10 (~4.4s por ciclo)
        const int dotCount = 5;
        const double cycleDuration = 4.40;
        const double dotStagger = 0.22;
        const double dotLifetime = 3.40;

        var elapsed = _clock.IsRunning ? _clock.Elapsed.TotalSeconds : 1.70;
        var cycleTime = elapsed % cycleDuration;

        var startX = -dotRadius * 2.0;
        var endX = width + (dotRadius * 2.0);
        var span = endX - startX;

        for (var i = 0; i < dotCount; i++)
        {
            var local = cycleTime - (i * dotStagger);
            if (local < 0 || local > dotLifetime) continue;

            var u = local / dotLifetime; // 0.0 .. 1.0
            var progress = ComputeContinuousProgress(u); // 0.0 .. 1.0 sem saltos
            var x = startX + (span * progress);

            if (x < -dotRadius || x > width + dotRadius) continue;
            dc.DrawEllipse(brush, null, new Point(x, cy), dotRadius, dotRadius);
        }
    }

    /// <summary>
    /// Curva continua monotona de 0.0 a 1.0:
    /// - [0.00 .. 0.32]: entrada suavizada (expoente 2.2) de 0.00 ate 0.36;
    /// - [0.32 .. 0.68]: deslize linear lento entre 36% e 64% da largura;
    /// - [0.68 .. 1.00]: saida acelerada (expoente 2.2) de 0.64 ate 1.00.
    /// </summary>
    private static double ComputeContinuousProgress(double u)
    {
        const double u1 = 0.32;
        const double u2 = 0.68;
        const double wellStartRatio = 0.36;
        const double wellEndRatio = 0.64;

        if (u < u1)
        {
            var p = u / u1;
            var eased = 1.0 - Math.Pow(1.0 - p, 2.2);
            return wellStartRatio * eased;
        }

        if (u < u2)
        {
            var p = (u - u1) / (u2 - u1);
            return wellStartRatio + ((wellEndRatio - wellStartRatio) * p);
        }

        {
            var p = (u - u2) / (1.0 - u2);
            var eased = Math.Pow(p, 2.2);
            return wellEndRatio + ((1.0 - wellEndRatio) * eased);
        }
    }
}
