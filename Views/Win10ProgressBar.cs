using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace KlyraOptimizer.Views;

/// <summary>
/// Reproducao 1:1 do Windows.UI.Xaml.Controls.ProgressBar usado na XAML Controls Gallery
/// (ProgressBarPage.xaml + SDK 10.0.19041.0 generic.xaml linhas 12152-12431):
/// - Suporta modo Indeterminate (IsIndeterminate=True), Determinate (Value/Minimum/Maximum),
///   ShowPaused e ShowError igual na XAML Controls Gallery;
/// - No modo Indeterminate, reproduz a soma exata dos 3 TranslateTransforms do generic.xaml:
///   IndeterminateRoot (deslize linear de 3.917s) + B1..B5 (espacamento de entrada/saida) +
///   E1..E5 (KeySpline="0.4,0,0.6,1" ate EllipseAnimationWellPosition e saida ate EndPosition),
///   mantendo as 5 esferas agrupadas com espacamento fixo no centro em qualquer largura de tela.
/// </summary>
public sealed class Win10ProgressBar : FrameworkElement
{
    public static readonly DependencyProperty IsIndeterminateProperty =
        DependencyProperty.Register(
            nameof(IsIndeterminate),
            typeof(bool),
            typeof(Win10ProgressBar),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnStateChanged));

    public static readonly DependencyProperty ShowPausedProperty =
        DependencyProperty.Register(
            nameof(ShowPaused),
            typeof(bool),
            typeof(Win10ProgressBar),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnStateChanged));

    public static readonly DependencyProperty ShowErrorProperty =
        DependencyProperty.Register(
            nameof(ShowError),
            typeof(bool),
            typeof(Win10ProgressBar),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnStateChanged));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(double),
            typeof(Win10ProgressBar),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(
            nameof(Minimum),
            typeof(double),
            typeof(Win10ProgressBar),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(
            nameof(Maximum),
            typeof(double),
            typeof(Win10ProgressBar),
            new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty =
        DependencyProperty.Register(
            nameof(Foreground),
            typeof(Brush),
            typeof(Win10ProgressBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BackgroundProperty =
        DependencyProperty.Register(
            nameof(Background),
            typeof(Brush),
            typeof(Win10ProgressBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsIndeterminate
    {
        get => (bool)GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    public bool ShowPaused
    {
        get => (bool)GetValue(ShowPausedProperty);
        set => SetValue(ShowPausedProperty, value);
    }

    public bool ShowError
    {
        get => (bool)GetValue(ShowErrorProperty);
        set => SetValue(ShowErrorProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public Brush? Background
    {
        get => (Brush?)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    private readonly Stopwatch _clock = new();
    private bool _rendering;

    public Win10ProgressBar()
    {
        MinHeight = 4;
        Height = 4;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        ClipToBounds = true;
        IsHitTestVisible = false;
        Focusable = false;
        SetResourceReference(ForegroundProperty, "App.Accent");
        SetResourceReference(BackgroundProperty, "App.Divider");

        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => StopRendering(resetClock: true);
        IsVisibleChanged += (_, _) => UpdateSubscription();
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Win10ProgressBar bar) bar.UpdateSubscription();
    }

    private void UpdateSubscription()
    {
        if (!IsLoaded || !IsVisible || !IsIndeterminate || ShowError)
        {
            StopRendering(resetClock: true);
            InvalidateVisual();
            return;
        }

        if (ShowPaused)
        {
            StopRendering(resetClock: false);
            InvalidateVisual();
            return;
        }

        StartRendering();
    }

    private void StartRendering()
    {
        if (_rendering) return;
        _rendering = true;
        if (_clock.ElapsedTicks == 0) _clock.Restart();
        else _clock.Start();
        CompositionTarget.Rendering += OnFrame;
        InvalidateVisual();
    }

    private void StopRendering(bool resetClock)
    {
        if (_rendering)
        {
            _rendering = false;
            CompositionTarget.Rendering -= OnFrame;
        }

        if (resetClock) _clock.Reset();
        else _clock.Stop();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!IsVisible || !IsIndeterminate || ShowPaused || ShowError)
        {
            UpdateSubscription();
            return;
        }

        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? 130 : availableSize.Width;
        var h = !double.IsNaN(Height) && Height > 0 ? Height : 4;
        return new Size(w, Math.Max(MinHeight, h));
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!IsVisible) return;

        var width = ActualWidth;
        var height = ActualHeight > 0 ? ActualHeight : (!double.IsNaN(Height) ? Height : 4);
        if (width <= 4 || height <= 1) return;

        var fg = Foreground ?? Brushes.DodgerBlue;
        var pauseOpacity = ShowPaused ? 0.6 : 1.0; // ProgressBarIndicatorPauseOpacity em generic.xaml

        // Modo Determinate (igual ao Example 2 de ProgressBarPage.xaml na XAML Controls Gallery)
        if (!IsIndeterminate)
        {
            var bg = Background;
            if (bg != null)
            {
                var trackH = Math.Min(2.0, height);
                dc.DrawRectangle(bg, null, new Rect(0, (height - trackH) * 0.5, width, trackH));
            }

            if (ShowError) return;

            var range = Math.Max(1e-6, Maximum - Minimum);
            var ratio = Math.Clamp((Value - Minimum) / range, 0.0, 1.0);
            var fillWidth = width * ratio;
            if (fillWidth > 0.5)
            {
                if (pauseOpacity < 0.99) dc.PushOpacity(pauseOpacity);
                dc.DrawRectangle(fg, null, new Rect(0, 0, fillWidth, height));
                if (pauseOpacity < 0.99) dc.Pop();
            }
            return;
        }

        // Modo Indeterminate: Error oculta o indicador conforme VisualState "Error" do generic.xaml
        if (ShowError) return;

        // ProgressBarTemplateSettings do UWP (EllipseDiameter + EllipseOffset)
        var dotDiameter = width <= 180.0 ? 3.0 : 4.0;
        dotDiameter = Math.Min(dotDiameter, Math.Max(3.0, height));
        var dotRadius = dotDiameter * 0.5;
        var ellipseOffset = width <= 180.0 ? 4.0 : (width <= 280.0 ? 6.0 : 7.0);
        var step = dotDiameter + ellipseOffset;
        var cy = height * 0.5;

        // Storyboard exato de generic.xaml (linhas 12196-12303)
        const int dotCount = 5;
        const double cycleDuration = 3.917;
        const double dotStagger = 0.167;
        const double dotLifetime = 3.000;

        var elapsed = (_clock.ElapsedTicks > 0) ? _clock.Elapsed.TotalSeconds : 1.75;
        var t = elapsed % cycleDuration;

        // Parametros proporcionais de UpdateWidthBasedTemplateSettings
        var containerStart = -(0.34 * width) - (4.0 * step);
        var containerEnd = 0.66 * width;
        var wellPos = 0.36 * width;
        var endPos = (0.66 * width) + 50.0;

        var containerX = containerStart + ((containerEnd - containerStart) * (t / cycleDuration));

        if (pauseOpacity < 0.99) dc.PushOpacity(pauseOpacity);

        for (var i = 0; i < dotCount; i++)
        {
            var s = i * dotStagger;

            // DiscreteDoubleKeyFrame de Opacity: visivel em [s, s + 3.0]
            if (t < s || t >= s + dotLifetime) continue;

            // Posicao base no StackPanel horizontal (B5..B1 da esquerda para a direita)
            var initialStackX = ((dotCount - 1 - i) * step) + dotRadius;

            // Animacao de B(i+1): -50 em t=0 -> 0 em t=(0.5+s) -> 0 ate t=(2.0+s) -> +100 em t=(3.0+s)
            double bx;
            if (t < 0.5 + s)
            {
                bx = -50.0 * (1.0 - (t / (0.5 + s)));
            }
            else if (t < 2.0 + s)
            {
                bx = 0.0;
            }
            else
            {
                bx = 100.0 * ((t - (2.0 + s)) / 1.0);
            }

            // Animacao de E(i+1) com KeySpline="0.4,0,0.6,1"
            double ex;
            var local = t - s; // [0 .. 3.0]
            if (local < 1.0)
            {
                ex = wellPos * Win10ProgressRing.EvaluateKeySpline(local, 0.4, 0.0, 0.6, 1.0);
            }
            else if (local < 2.0)
            {
                ex = wellPos;
            }
            else
            {
                var exitU = local - 2.0;
                var eased = Win10ProgressRing.EvaluateKeySpline(exitU, 0.4, 0.0, 0.6, 1.0);
                ex = wellPos + ((endPos - wellPos) * eased);
            }

            var x = initialStackX + containerX + bx + ex;
            if (x < -dotRadius || x > width + dotRadius) continue;

            dc.DrawEllipse(fg, null, new Point(x, cy), dotRadius, dotRadius);
        }

        if (pauseOpacity < 0.99) dc.Pop();
    }
}
