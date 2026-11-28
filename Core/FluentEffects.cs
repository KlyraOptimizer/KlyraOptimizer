using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;

namespace KlyraOptimizer.Core;

/// <summary>
/// Efeitos interativos classicos do Fluent Design System (Windows 10 1809/1903):
/// 1) Reveal Highlight (com cache de TemplateParts e pausa durante arraste da janela);
/// 2) Tilt Effect 3D REAL (Viewport3D + PerspectiveCamera + AxisAngleRotation3D):
///    projecao perspectiva 3D verdadeira fiel ao PointerDownThemeAnimation / PointerUpThemeAnimation
///    do Windows 10 UWP, onde a ponta ou borda clicada afunda para dentro da tela (-Z) enquanto
///    o lado oposto permanece ancorado, sem qualquer inversao;
/// 3) Clear Button helper para TextBox no estilo UWP (botao X &#xE10A;).
/// </summary>
public static class FluentEffects
{
    private const double BorderRevealRadius = 128.0;
    private const double FillRevealRadius = 96.0;

    private sealed class RevealPartsCache
    {
        public Border? Border;
        public Border? Fill;
        public bool Resolved;
    }

    private sealed class Tilt3DRig
    {
        public required Viewport3D Viewport;
        public required PerspectiveCamera Camera;
        public required MeshGeometry3D Mesh;
        public required AxisAngleRotation3D AxisRotation;
        public required RotateTransform3D RotateTransform;
        public required TranslateTransform3D TranslateTransform;
        public double LastWidth;
        public double LastHeight;
        public int Version;
    }

    private static readonly HashSet<FrameworkElement> RevealTargets = new();
    private static readonly HashSet<Window> HookedWindows = new();
    private static readonly ConditionalWeakTable<FrameworkElement, RevealPartsCache> RevealCache = new();
    private static readonly ConditionalWeakTable<FrameworkElement, Tilt3DRig> TiltRigs = new();

    private static readonly IEasingFunction PointerDownEase =
        new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4.5 };

    private static readonly IEasingFunction PointerUpEase =
        new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4.0 };

    // =========================================================================
    // 1. REVEAL HIGHLIGHT (Fundo + Borda de vizinhanca de 1px, otimizado)
    // =========================================================================
    public static readonly DependencyProperty EnableRevealProperty =
        DependencyProperty.RegisterAttached(
            "EnableReveal",
            typeof(bool),
            typeof(FluentEffects),
            new PropertyMetadata(false, OnEnableRevealChanged));

    public static bool GetEnableReveal(DependencyObject obj) => (bool)obj.GetValue(EnableRevealProperty);
    public static void SetEnableReveal(DependencyObject obj, bool value) => obj.SetValue(EnableRevealProperty, value);

    private static void OnEnableRevealChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        if ((bool)e.NewValue)
        {
            element.Loaded += OnRevealElementLoaded;
            element.Unloaded += OnRevealElementUnloaded;
            element.MouseLeave += OnRevealElementMouseLeave;
            if (element.IsLoaded) RegisterRevealTarget(element);
        }
        else
        {
            element.Loaded -= OnRevealElementLoaded;
            element.Unloaded -= OnRevealElementUnloaded;
            element.MouseLeave -= OnRevealElementMouseLeave;
            RevealTargets.Remove(element);
            RevealCache.Remove(element);
        }
    }

    private static void OnRevealElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe) RegisterRevealTarget(fe);
    }

    private static void OnRevealElementUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            ClearElementReveal(fe);
            RevealTargets.Remove(fe);
        }
    }

    private static void OnRevealElementMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        var (_, fill) = GetRevealParts(fe);
        if (fill != null)
        {
            if (Motion.Enabled)
            {
                fill.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = PointerUpEase
                });
            }
            else
            {
                fill.Opacity = 0;
            }
        }
    }

    private static void RegisterRevealTarget(FrameworkElement element)
    {
        RevealTargets.Add(element);

        var window = Window.GetWindow(element);
        if (window != null && HookedWindows.Add(window))
        {
            window.PreviewMouseMove += OnWindowPreviewMouseMove;
            window.MouseLeave += OnWindowMouseLeave;
            window.Closed += (_, _) =>
            {
                window.PreviewMouseMove -= OnWindowPreviewMouseMove;
                window.MouseLeave -= OnWindowMouseLeave;
                HookedWindows.Remove(window);
            };
        }
    }

    private static void OnWindowMouseLeave(object sender, MouseEventArgs e)
    {
        foreach (var target in RevealTargets)
        {
            ClearElementReveal(target);
        }
    }

    private static (Border? Border, Border? Fill) GetRevealParts(FrameworkElement element)
    {
        var entry = RevealCache.GetValue(element, _ => new RevealPartsCache());
        if (!entry.Resolved)
        {
            entry.Border = FindTemplatePart<Border>(element, "PART_RevealBorder");
            entry.Fill = FindTemplatePart<Border>(element, "PART_RevealFill");
            if (entry.Border != null || entry.Fill != null)
            {
                entry.Resolved = true;
            }
        }
        return (entry.Border, entry.Fill);
    }

    private static void OnWindowPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (Win10Acrylic.IsMovingOrResizing) return;
        if (sender is not Window window) return;

        var posInWindow = e.GetPosition(window);
        var light = ThemeService.IsLight;

        var borderCenterColor = light
            ? Color.FromArgb(0x88, 0x00, 0x00, 0x00)
            : Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF);
        var borderMidColor = light
            ? Color.FromArgb(0x30, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x42, 0xFF, 0xFF, 0xFF);
        var borderEdgeColor = light
            ? Color.FromArgb(0x00, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);

        var fillCenterColor = light
            ? Color.FromArgb(0x18, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);
        var fillEdgeColor = light
            ? Color.FromArgb(0x00, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);

        foreach (var element in RevealTargets)
        {
            if (!element.IsVisible || !element.IsEnabled || element.ActualWidth <= 0 || element.ActualHeight <= 0)
                continue;

            var (revealBorder, revealFill) = GetRevealParts(element);
            if (revealBorder == null && revealFill == null) continue;

            Point local;
            try
            {
                local = window.TranslatePoint(posInWindow, element);
            }
            catch
            {
                continue;
            }

            var dx = local.X < 0 ? -local.X : (local.X > element.ActualWidth ? local.X - element.ActualWidth : 0);
            var dy = local.Y < 0 ? -local.Y : (local.Y > element.ActualHeight ? local.Y - element.ActualHeight : 0);
            var distSq = (dx * dx) + (dy * dy);

            if (revealBorder != null)
            {
                if (distSq <= BorderRevealRadius * BorderRevealRadius)
                {
                    if (revealBorder.BorderBrush is not RadialGradientBrush borderBrush || borderBrush.IsFrozen)
                    {
                        borderBrush = new RadialGradientBrush
                        {
                            MappingMode = BrushMappingMode.Absolute,
                            RadiusX = BorderRevealRadius,
                            RadiusY = BorderRevealRadius,
                            GradientStops = new GradientStopCollection
                            {
                                new(borderCenterColor, 0.0),
                                new(borderMidColor, 0.45),
                                new(borderEdgeColor, 1.0)
                            }
                        };
                        revealBorder.BorderBrush = borderBrush;
                    }
                    else
                    {
                        borderBrush.GradientStops[0].Color = borderCenterColor;
                        borderBrush.GradientStops[1].Color = borderMidColor;
                        borderBrush.GradientStops[2].Color = borderEdgeColor;
                    }

                    borderBrush.Center = local;
                    borderBrush.GradientOrigin = local;
                    revealBorder.Opacity = 1;
                }
                else if (revealBorder.Opacity > 0)
                {
                    revealBorder.Opacity = 0;
                }
            }

            if (revealFill != null)
            {
                var inside = dx == 0 && dy == 0;
                if (inside)
                {
                    if (revealFill.Background is not RadialGradientBrush fillBrush || fillBrush.IsFrozen)
                    {
                        fillBrush = new RadialGradientBrush
                        {
                            MappingMode = BrushMappingMode.Absolute,
                            RadiusX = FillRevealRadius,
                            RadiusY = FillRevealRadius,
                            GradientStops = new GradientStopCollection
                            {
                                new(fillCenterColor, 0.0),
                                new(fillEdgeColor, 1.0)
                            }
                        };
                        revealFill.Background = fillBrush;
                    }
                    else
                    {
                        fillBrush.GradientStops[0].Color = fillCenterColor;
                        fillBrush.GradientStops[1].Color = fillEdgeColor;
                    }

                    fillBrush.Center = local;
                    fillBrush.GradientOrigin = local;
                    revealFill.BeginAnimation(UIElement.OpacityProperty, null);
                    revealFill.Opacity = 1;
                }
                else if (revealFill.Opacity > 0)
                {
                    revealFill.BeginAnimation(UIElement.OpacityProperty, null);
                    revealFill.Opacity = 0;
                }
            }
        }
    }

    private static void ClearElementReveal(FrameworkElement element)
    {
        var (rb, rf) = GetRevealParts(element);
        if (rb != null) rb.Opacity = 0;
        if (rf != null)
        {
            rf.BeginAnimation(UIElement.OpacityProperty, null);
            rf.Opacity = 0;
        }
    }

    // =========================================================================
    // 2. TILT EFFECT 3D VERDADEIRO (Viewport3D + PerspectiveCamera do Windows 10)
    //    Aplicado exclusivamente aos botoes (Btn.Base).
    // =========================================================================
    public static readonly DependencyProperty EnableTiltProperty =
        DependencyProperty.RegisterAttached(
            "EnableTilt",
            typeof(bool),
            typeof(FluentEffects),
            new PropertyMetadata(false, OnEnableTiltChanged));

    public static bool GetEnableTilt(DependencyObject obj) => (bool)obj.GetValue(EnableTiltProperty);
    public static void SetEnableTilt(DependencyObject obj, bool value) => obj.SetValue(EnableTiltProperty, value);

    private static void OnEnableTiltChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        if ((bool)e.NewValue)
        {
            element.PreviewMouseLeftButtonDown += OnTiltPointerDown;
            element.PreviewMouseMove += OnTiltPointerMove;
            element.PreviewMouseLeftButtonUp += OnTiltPointerUp;
            element.MouseLeave += OnTiltMouseLeave;
            element.LostMouseCapture += OnTiltLostCapture;
            element.Unloaded += OnTiltUnloaded;
        }
        else
        {
            element.PreviewMouseLeftButtonDown -= OnTiltPointerDown;
            element.PreviewMouseMove -= OnTiltPointerMove;
            element.PreviewMouseLeftButtonUp -= OnTiltPointerUp;
            element.MouseLeave -= OnTiltMouseLeave;
            element.LostMouseCapture -= OnTiltLostCapture;
            element.Unloaded -= OnTiltUnloaded;
        }
    }

    private static void OnTiltPointerDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || !element.IsEnabled) return;
        ApplyPointerDownTilt3D(element, e.GetPosition(element), animate: true);
    }

    private static void OnTiltPointerMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element || !element.IsEnabled) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var pt = e.GetPosition(element);
        if (pt.X < -6 || pt.Y < -6 || pt.X > element.ActualWidth + 6 || pt.Y > element.ActualHeight + 6)
        {
            ApplyPointerUpTilt3D(element, immediate: false);
            return;
        }

        ApplyPointerDownTilt3D(element, pt, animate: false);
    }

    private static void OnTiltPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element) ApplyPointerUpTilt3D(element, immediate: false);
    }

    private static void OnTiltMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement element) ApplyPointerUpTilt3D(element, immediate: false);
    }

    private static void OnTiltLostCapture(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement element) ApplyPointerUpTilt3D(element, immediate: false);
    }

    private static void OnTiltUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element) ApplyPointerUpTilt3D(element, immediate: true);
    }

    /// <summary>
    /// Projecao 3D perspectiva real (identica ao PlaneProjection / PointerDownThemeAnimation do Windows 10):
    /// No sistema 3D do WPF (+X = direita, +Y = cima, +Z = saindo da tela em direcao ao usuario):
    /// - Camera fica em (0, 0, +camZ) olhando para -Z;
    /// - Ponto clicado P = (+nx*W/2, -ny*H/2, 0);
    /// - Dobradica (Hinge) fica no lado oposto (-nx*0.44*W, +ny*0.44*H, 0);
    /// - Eixo de rotacao A = (ny*W, nx*H, 0) normalizado garante pela regra da mao direita
    ///   que (A x (P - Hinge)).Z &lt; 0 SEMPRE: o lado/canto clicado afunda para dentro da tela (-Z)
    ///   formando o trapezio de perspectiva real enquanto o lado oposto fica 100% firme!
    /// </summary>
    private static void ApplyPointerDownTilt3D(FrameworkElement element, Point pointer, bool animate)
    {
        var w = element.ActualWidth;
        var h = element.ActualHeight;
        if (w <= 4 || h <= 4) return;

        var flatHost = FindTemplatePart<Border>(element, "PART_FlatHost");
        var tiltVisual = FindTemplatePart<FrameworkElement>(element, "PART_TiltVisual");
        var host3D = FindTemplatePart<Grid>(element, "PART_Tilt3DHost");

        if (flatHost == null || tiltVisual == null || host3D == null) return;

        var rig = GetOrCreateTilt3DRig(element, tiltVisual, host3D, w, h);
        rig.Version++;

        // Ativa o Viewport3D mantendo o elemento 2D vivo para hit-test e VisualBrush (alpha < 1/255 = invisivel)
        host3D.Visibility = Visibility.Visible;
        flatHost.Opacity = 0.002;

        // Coordenadas normalizadas do clique em [-1, +1]
        var nx = Math.Clamp(((pointer.X / w) - 0.5) * 2.0, -1.0, 1.0);
        var ny = Math.Clamp(((pointer.Y / h) - 0.5) * 2.0, -1.0, 1.0);
        var ecc = Math.Clamp(Math.Sqrt((nx * nx) + (ny * ny)), 0.0, 1.0);

        var camZ = rig.Camera.Position.Z;

        // Dobradica 3D no lado oposto ao clique
        rig.RotateTransform.CenterX = -nx * w * 0.44;
        rig.RotateTransform.CenterY = +ny * h * 0.44;
        rig.RotateTransform.CenterZ = 0;

        double targetAngleDeg = 0.0;
        var rawAx = ny * w;
        var rawAy = nx * h;
        var axisLen = Math.Sqrt((rawAx * rawAx) + (rawAy * rawAy));

        if (axisLen > 1e-3 && ecc > 0.06)
        {
            var ax = rawAx / axisLen;
            var ay = rawAy / axisLen;
            rig.AxisRotation.Axis = new Vector3D(ax, ay, 0);

            // Braco de alavanca do ponto clicado ate a dobradica oposta
            var rx = nx * w * 0.94;
            var ry = -ny * h * 0.94;
            var lever = Math.Max(8.0, Math.Abs((ax * ry) - (ay * rx)));

            // Afundamento perspectivo calibrado (~6.8% da distancia focal camZ, limitado a 14 graus)
            var targetDropZ = camZ * 0.068 * ecc;
            targetAngleDeg = Math.Clamp((targetDropZ / lever) * (180.0 / Math.PI), 0.0, 14.0);
        }
        else
        {
            rig.AxisRotation.Axis = new Vector3D(1, 0, 0);
        }

        // Afundamento central em -Z (predomina quando clica no meio do botao)
        var targetOffsetZ = -camZ * 0.028 * (1.0 - (0.65 * ecc));

        var downMs = (Motion.Enabled && animate) ? 75 : 0;
        var ease = (Motion.Enabled && animate) ? PointerDownEase : null;

        Animate3DProperty(rig.AxisRotation, AxisAngleRotation3D.AngleProperty, targetAngleDeg, downMs, ease);
        Animate3DProperty(rig.TranslateTransform, TranslateTransform3D.OffsetZProperty, targetOffsetZ, downMs, ease);
    }

    private static void ApplyPointerUpTilt3D(FrameworkElement element, bool immediate)
    {
        if (!TiltRigs.TryGetValue(element, out var rig)) return;

        var flatHost = FindTemplatePart<Border>(element, "PART_FlatHost");
        var host3D = FindTemplatePart<Grid>(element, "PART_Tilt3DHost");
        var version = ++rig.Version;

        if (immediate || !Motion.Enabled)
        {
            rig.AxisRotation.BeginAnimation(AxisAngleRotation3D.AngleProperty, null);
            rig.TranslateTransform.BeginAnimation(TranslateTransform3D.OffsetZProperty, null);
            rig.AxisRotation.Angle = 0;
            rig.TranslateTransform.OffsetZ = 0;
            if (flatHost != null) flatHost.Opacity = 1.0;
            if (host3D != null) host3D.Visibility = Visibility.Collapsed;
            return;
        }

        const int upMs = 145;
        var angleAnim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(upMs))
        {
            EasingFunction = PointerUpEase
        };
        var zAnim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(upMs))
        {
            EasingFunction = PointerUpEase
        };

        angleAnim.Completed += (_, _) =>
        {
            if (rig.Version != version) return;
            rig.AxisRotation.BeginAnimation(AxisAngleRotation3D.AngleProperty, null);
            rig.TranslateTransform.BeginAnimation(TranslateTransform3D.OffsetZProperty, null);
            rig.AxisRotation.Angle = 0;
            rig.TranslateTransform.OffsetZ = 0;
            if (flatHost != null) flatHost.Opacity = 1.0;
            if (host3D != null) host3D.Visibility = Visibility.Collapsed;
        };

        rig.AxisRotation.BeginAnimation(AxisAngleRotation3D.AngleProperty, angleAnim);
        rig.TranslateTransform.BeginAnimation(TranslateTransform3D.OffsetZProperty, zAnim);
    }

    private static void Animate3DProperty(
        Animatable target,
        DependencyProperty prop,
        double to,
        int durationMs,
        IEasingFunction? easing)
    {
        if (durationMs <= 0)
        {
            target.BeginAnimation(prop, null);
            target.SetValue(prop, to);
            return;
        }

        target.BeginAnimation(prop, new DoubleAnimation(to, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = easing
        });
    }

    private static Tilt3DRig GetOrCreateTilt3DRig(
        FrameworkElement button,
        FrameworkElement tiltVisual,
        Grid host3D,
        double width,
        double height)
    {
        if (!TiltRigs.TryGetValue(button, out var rig))
        {
            var camera = new PerspectiveCamera
            {
                LookDirection = new Vector3D(0, 0, -1),
                UpDirection = new Vector3D(0, 1, 0),
                FieldOfView = 42.0
            };

            var mesh = new MeshGeometry3D
            {
                TextureCoordinates = new PointCollection
                {
                    new(0, 0),
                    new(0, 1),
                    new(1, 1),
                    new(1, 0)
                },
                TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 }
            };

            var visualBrush = new VisualBrush(tiltVisual)
            {
                Stretch = Stretch.Fill
            };
            RenderOptions.SetCachingHint(visualBrush, CachingHint.Cache);

            var material = new DiffuseMaterial(visualBrush);
            var axisRotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
            var rotateTransform = new RotateTransform3D(axisRotation);
            var translateTransform = new TranslateTransform3D(0, 0, 0);

            var transformGroup = new Transform3DGroup();
            transformGroup.Children.Add(rotateTransform);
            transformGroup.Children.Add(translateTransform);

            var geometryModel = new GeometryModel3D(mesh, material)
            {
                BackMaterial = material,
                Transform = transformGroup
            };

            var modelGroup = new Model3DGroup();
            modelGroup.Children.Add(new AmbientLight(Colors.White));
            modelGroup.Children.Add(geometryModel);

            var modelVisual = new ModelVisual3D { Content = modelGroup };
            var viewport = new Viewport3D
            {
                Camera = camera,
                ClipToBounds = false,
                IsHitTestVisible = false
            };
            viewport.Children.Add(modelVisual);

            host3D.Children.Clear();
            host3D.Children.Add(viewport);

            rig = new Tilt3DRig
            {
                Viewport = viewport,
                Camera = camera,
                Mesh = mesh,
                AxisRotation = axisRotation,
                RotateTransform = rotateTransform,
                TranslateTransform = translateTransform
            };
            TiltRigs.Add(button, rig);
        }
        else if (!host3D.Children.Contains(rig.Viewport))
        {
            host3D.Children.Clear();
            host3D.Children.Add(rig.Viewport);
        }

        if (Math.Abs(rig.LastWidth - width) > 0.2 || Math.Abs(rig.LastHeight - height) > 0.2)
        {
            rig.LastWidth = width;
            rig.LastHeight = height;
            rig.Viewport.Width = width;
            rig.Viewport.Height = height;

            var hw = width * 0.5;
            var hh = height * 0.5;

            rig.Mesh.Positions = new Point3DCollection
            {
                new(-hw, +hh, 0),
                new(-hw, -hh, 0),
                new(+hw, -hh, 0),
                new(+hw, +hh, 0)
            };

            // Posiciona a camera exatamente na distancia em que Z=0 mapeia 1:1 para (width x height)
            var halfFovRad = (rig.Camera.FieldOfView * 0.5) * (Math.PI / 180.0);
            var camZ = hw / Math.Tan(halfFovRad);
            rig.Camera.Position = new Point3D(0, 0, camZ);
            rig.Camera.NearPlaneDistance = Math.Max(1.0, camZ * 0.1);
            rig.Camera.FarPlaneDistance = camZ * 10.0;
        }

        return rig;
    }

    // =========================================================================
    // 3. BOTAO "X" DE LIMPAR PARA TEXTBOX (ESTILO UWP XAML CONTROLS GALLERY)
    // =========================================================================
    public static readonly DependencyProperty EnableClearButtonProperty =
        DependencyProperty.RegisterAttached(
            "EnableClearButton",
            typeof(bool),
            typeof(FluentEffects),
            new PropertyMetadata(false, OnEnableClearButtonChanged));

    public static bool GetEnableClearButton(DependencyObject obj) => (bool)obj.GetValue(EnableClearButtonProperty);
    public static void SetEnableClearButton(DependencyObject obj, bool value) => obj.SetValue(EnableClearButtonProperty, value);

    private static void OnEnableClearButtonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Button button) return;
        if ((bool)e.NewValue)
        {
            button.Click += OnClearTextButtonClick;
        }
        else
        {
            button.Click -= OnClearTextButtonClick;
        }
    }

    private static void OnClearTextButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject d) return;

        var current = VisualTreeHelper.GetParent(d);
        while (current != null && current is not TextBox)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        if (current is TextBox tb && !tb.IsReadOnly)
        {
            tb.Clear();
            tb.Focus();
            e.Handled = true;
        }
    }

    private static T? FindTemplatePart<T>(FrameworkElement element, string partName) where T : DependencyObject
    {
        if (element is Control ctrl && ctrl.Template != null)
        {
            return ctrl.Template.FindName(partName, ctrl) as T;
        }

        return element.FindName(partName) as T;
    }
}
