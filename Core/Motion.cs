using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace KlyraOptimizer.Core;

/// <summary>
/// Animacoes autenticas do Windows 10 UWP:
/// EntranceThemeTransition (subida suave com desaceleracao exponencial),
/// DrillInThemeTransition (escala 0.96 -> 1.0 para detalhes da Loja),
/// SplitView pane expand/collapse e ContentDialog pop-in.
/// Respeita SystemParameters.ClientAreaAnimation ("Mostrar animacoes no Windows").
/// </summary>
public static class Motion
{
    private static readonly IEasingFunction Win10Decelerate =
        new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4.5 };

    private static readonly IEasingFunction EaseInOut =
        new QuadraticEase { EasingMode = EasingMode.EaseInOut };

    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    /// <summary>EntranceThemeTransition do UWP Windows 10 (deslocamento vertical + fade rapido).</summary>
    public static void PageIn(FrameworkElement page)
    {
        if (page == null || !Enabled) return;

        FadeIn(page, 22, 240);

        var panel = FindDescendant<ScrollViewer>(page)?.Content as Panel;
        if (panel == null) return;

        var delay = 24;
        var staggered = 0;

        foreach (var child in panel.Children)
        {
            if (child is not FrameworkElement element || element.Visibility != Visibility.Visible) continue;

            FadeIn(element, 16, 220, delay);
            delay += 28;

            if (++staggered >= 6) break;
        }
    }

    /// <summary>DrillInThemeTransition do Windows 10 (usado ao abrir pagina de detalhes na Loja).</summary>
    public static void DrillIn(FrameworkElement element)
    {
        if (element == null || !Enabled) return;

        var duration = new Duration(TimeSpan.FromMilliseconds(240));
        element.Opacity = 1;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration)
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = Win10Decelerate
        });

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(1, 1);
        element.RenderTransform = scale;

        var zoom = new DoubleAnimation(0.955, 1.0, duration)
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

        var duration = new Duration(TimeSpan.FromMilliseconds(210));
        element.Opacity = 1;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration)
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = Win10Decelerate
        });

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(1, 1);
        element.RenderTransform = scale;

        var zoom = new DoubleAnimation(1.03, 1.0, duration)
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
        int milliseconds = 220,
        int delayMilliseconds = 0)
    {
        if (element == null || !Enabled) return;

        var duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
        var beginTime = TimeSpan.FromMilliseconds(delayMilliseconds);

        element.Opacity = 1;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration)
        {
            BeginTime = beginTime,
            FillBehavior = FillBehavior.Stop,
            EasingFunction = Win10Decelerate
        });

        var translate = EnsureTranslate(element);
        if (translate == null || offsetY <= 0) return;

        translate.Y = 0;
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offsetY, 0, duration)
        {
            BeginTime = beginTime,
            FillBehavior = FillBehavior.Stop,
            EasingFunction = Win10Decelerate
        });
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
        if (element.RenderTransform is TranslateTransform existing) return existing;

        if (element.RenderTransform == null || element.RenderTransform == Transform.Identity)
        {
            var created = new TranslateTransform();
            element.RenderTransform = created;
            return created;
        }

        return null;
    }

    private static FrameworkElement? FirstChild(FrameworkElement parent)
        => parent is Panel panel && panel.Children.Count > 0
            ? panel.Children[0] as FrameworkElement
            : null;

    private static T? FindDescendant<T>(DependencyObject root, int maxDepth = 6) where T : DependencyObject
    {
        if (root == null || maxDepth < 0) return null;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;

            var found = FindDescendant<T>(child, maxDepth - 1);
            if (found != null) return found;
        }

        return null;
    }
}
