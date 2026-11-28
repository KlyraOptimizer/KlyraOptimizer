using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace KlyraOptimizer.Core;

/// <summary>
/// Animacoes autenticas do Windows 10 UWP:
/// EntranceNavigationTransitionInfo (subida limpa e continua em bloco unico com KeySpline 0.1,0.9,0.2,1.0,
/// sem piscadas por BeginTime em filhos), DrillInThemeTransition, SplitView e ContentDialog.
/// Respeita SystemParameters.ClientAreaAnimation ("Mostrar animacoes no Windows").
/// </summary>
public static class Motion
{
    private static readonly IEasingFunction Win10Decelerate =
        new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4.5 };

    private static readonly IEasingFunction EaseInOut =
        new QuadraticEase { EasingMode = EasingMode.EaseInOut };

    private static readonly KeySpline Win10EntranceSpline = new(0.1, 0.9, 0.2, 1.0);

    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    /// <summary>
    /// EntranceNavigationTransitionInfo da XAML Controls Gallery:
    /// anima o container da pagina de forma sincrona (antes do primeiro Render pass do WPF)
    /// como uma camada unica de Y=24 -> 0 com KeySpline(0.1,0.9,0.2,1.0), eliminando
    /// completamente o flash de 1 quadro e o cascateamento piscante dos elementos filhos.
    /// </summary>
    public static void PageIn(FrameworkElement host)
    {
        if (host == null) return;

        var translate = EnsureTranslate(host);

        if (!Enabled)
        {
            host.BeginAnimation(UIElement.OpacityProperty, null);
            host.Opacity = 1;
            if (translate != null)
            {
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.Y = 0;
            }
            return;
        }

        var opacityFrames = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(150),
            FillBehavior = FillBehavior.Stop
        };
        opacityFrames.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        opacityFrames.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150)), Win10EntranceSpline));

        host.Opacity = 1;
        host.BeginAnimation(UIElement.OpacityProperty, opacityFrames);

        if (translate == null) return;

        var slideFrames = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(230),
            FillBehavior = FillBehavior.Stop
        };
        slideFrames.KeyFrames.Add(new DiscreteDoubleKeyFrame(24.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        slideFrames.KeyFrames.Add(new SplineDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(230)), Win10EntranceSpline));

        translate.Y = 0;
        translate.BeginAnimation(TranslateTransform.YProperty, slideFrames);
    }

    /// <summary>DrillInThemeTransition do Windows 10 (usado ao abrir pagina de detalhes na Loja).</summary>
    public static void DrillIn(FrameworkElement element)
    {
        if (element == null || !Enabled) return;

        var duration = new Duration(TimeSpan.FromMilliseconds(220));
        element.Opacity = 1;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration)
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = Win10Decelerate
        });

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(1, 1);
        element.RenderTransform = scale;

        var zoom = new DoubleAnimation(0.96, 1.0, duration)
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = Win10Decelerate
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
    }

    /// <summary>DrillOutThemeTransition do Windows 10 (ao voltar da pagina de detalhes).</summary>
    public static void DrillOut(FrameworkElement element)
    {
        if (element == null || !Enabled) return;

        var duration = new Duration(TimeSpan.FromMilliseconds(200));
        element.Opacity = 1;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration)
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = Win10Decelerate
        });

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(1, 1);
        element.RenderTransform = scale;

        var zoom = new DoubleAnimation(1.025, 1.0, duration)
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = Win10Decelerate
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
    }

    /// <summary>Anima a largura do painel SplitView (menu hamburguer do Windows 10).</summary>
    public static void AnimateWidth(FrameworkElement element, double toWidth, int milliseconds = 190)
    {
        if (element == null) return;

        if (!Enabled)
        {
            element.BeginAnimation(FrameworkElement.WidthProperty, null);
            element.Width = toWidth;
            return;
        }

        var from = double.IsNaN(element.Width) ? element.ActualWidth : element.Width;
        element.Width = toWidth;
        element.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(from, toWidth,
            new Duration(TimeSpan.FromMilliseconds(milliseconds)))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = Win10Decelerate
        });
    }

    public static void FadeIn(
        FrameworkElement element,
        double offsetY = 12,
        int milliseconds = 200,
        int delayMilliseconds = 0)
    {
        if (element == null || !Enabled) return;

        var totalMs = delayMilliseconds + milliseconds;
        var opacityFrames = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(totalMs),
            FillBehavior = FillBehavior.Stop
        };
        opacityFrames.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delayMilliseconds > 0)
        {
            opacityFrames.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMilliseconds))));
        }
        opacityFrames.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(totalMs)), Win10EntranceSpline));

        element.Opacity = 1;
        element.BeginAnimation(UIElement.OpacityProperty, opacityFrames);

        var translate = EnsureTranslate(element);
        if (translate == null || offsetY <= 0) return;

        var slideFrames = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(totalMs),
            FillBehavior = FillBehavior.Stop
        };
        slideFrames.KeyFrames.Add(new DiscreteDoubleKeyFrame(offsetY, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delayMilliseconds > 0)
        {
            slideFrames.KeyFrames.Add(new DiscreteDoubleKeyFrame(offsetY, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMilliseconds))));
        }
        slideFrames.KeyFrames.Add(new SplineDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(totalMs)), Win10EntranceSpline));

        translate.Y = 0;
        translate.BeginAnimation(TranslateTransform.YProperty, slideFrames);
    }

    public static void SetVisible(UIElement? element, bool visible, int milliseconds = 150)
    {
        if (element == null) return;

        var version = NextVersion(element);

        if (!Enabled)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (visible)
        {
            element.Visibility = Visibility.Visible;
            element.Opacity = 1;
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1,
                new Duration(TimeSpan.FromMilliseconds(milliseconds)))
            {
                FillBehavior = FillBehavior.Stop,
                EasingFunction = Win10Decelerate
            });
            return;
        }

        if (element.Visibility != Visibility.Visible)
        {
            element.Visibility = Visibility.Collapsed;
            return;
        }

        var fade = new DoubleAnimation(element.Opacity, 0,
            new Duration(TimeSpan.FromMilliseconds(milliseconds)))
        {
            EasingFunction = Win10Decelerate
        };

        void Finish()
        {
            if (!IsCurrentVersion(element, version)) return;
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            element.Visibility = Visibility.Collapsed;
        }

        fade.Completed += (_, _) => Finish();
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        After(milliseconds + 80, Finish, element.Dispatcher);
    }

    public static void ShowOverlay(FrameworkElement overlay)
    {
        if (overlay == null) return;

        NextVersion(overlay);
        overlay.IsHitTestVisible = true;
        overlay.Visibility = Visibility.Visible;

        if (!Enabled)
        {
            overlay.BeginAnimation(UIElement.OpacityProperty, null);
            overlay.Opacity = 1;
            return;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(180));
        overlay.Opacity = 1;
        overlay.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration)
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = Win10Decelerate
        });

        var card = FirstChild(overlay);
        if (card == null) return;

        card.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(1, 1);
        card.RenderTransform = scale;

        var grow = new DoubleAnimation(1.04, 1.0, duration)
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = Win10Decelerate
        };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    public static void HideOverlay(FrameworkElement overlay)
    {
        if (overlay == null) return;

        var version = NextVersion(overlay);

        if (!Enabled || overlay.Visibility != Visibility.Visible)
        {
            CollapseNow(overlay);
            return;
        }

        overlay.IsHitTestVisible = false;

        const int milliseconds = 120;
        var fade = new DoubleAnimation(overlay.Opacity, 0,
            new Duration(TimeSpan.FromMilliseconds(milliseconds)))
        {
            EasingFunction = EaseInOut
        };

        void Finish()
        {
            if (!IsCurrentVersion(overlay, version)) return;
            CollapseNow(overlay);
        }

        fade.Completed += (_, _) => Finish();
        overlay.BeginAnimation(UIElement.OpacityProperty, fade);
        After(milliseconds + 80, Finish, overlay.Dispatcher);
    }

    private static void CollapseNow(FrameworkElement overlay)
    {
        overlay.BeginAnimation(UIElement.OpacityProperty, null);
        overlay.Opacity = 1;
        overlay.IsHitTestVisible = true;
        overlay.Visibility = Visibility.Collapsed;
    }

    public static void Pulse(FrameworkElement? element, double scale = 1.06, int milliseconds = 220)
    {
        if (element == null || !Enabled) return;

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var transform = new ScaleTransform(1, 1);
        element.RenderTransform = transform;

        var duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
        var grow = new DoubleAnimationUsingKeyFrames
        {
            Duration = duration,
            FillBehavior = FillBehavior.Stop
        };
        grow.KeyFrames.Add(new EasingDoubleKeyFrame(scale, KeyTime.FromPercent(0.35), Win10Decelerate));
        grow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), EaseInOut));

        transform.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private static readonly ConditionalWeakTable<UIElement, StrongBox<int>> Versions = new();

    private static int NextVersion(UIElement element)
        => ++Versions.GetValue(element, _ => new StrongBox<int>()).Value;

    private static bool IsCurrentVersion(UIElement element, int version)
        => Versions.TryGetValue(element, out var box) && box.Value == version;

    private static void After(int milliseconds, Action action, Dispatcher dispatcher)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds)
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };

        timer.Start();
    }

    private static TranslateTransform? EnsureTranslate(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform existing && !existing.IsFrozen)
            return existing;

        if (element.RenderTransform is TransformGroup group && !group.IsFrozen)
        {
            foreach (var child in group.Children)
            {
                if (child is TranslateTransform tt && !tt.IsFrozen) return tt;
            }
            var added = new TranslateTransform();
            group.Children.Add(added);
            return added;
        }

        if (element.RenderTransform == null || element.RenderTransform == Transform.Identity)
        {
            var created = new TranslateTransform();
            element.RenderTransform = created;
            return created;
        }

        var newGroup = new TransformGroup();
        newGroup.Children.Add(element.RenderTransform.CloneCurrentValue());
        var tr = new TranslateTransform();
        newGroup.Children.Add(tr);
        element.RenderTransform = newGroup;
        return tr;
    }

    private static FrameworkElement? FirstChild(FrameworkElement parent)
        => parent is Panel panel && panel.Children.Count > 0
            ? panel.Children[0] as FrameworkElement
            : null;
}
