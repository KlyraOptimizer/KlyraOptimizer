using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace KlyraOptimizer.Core;

/// <summary>
/// Rolagem suave inercial (estilo UWP / Edge) para todos os ScrollViewers do aplicativo:
/// interpola o deslocamento vertical em 60/120/144/240Hz via CompositionTarget.Rendering
/// independente da taxa de quadros, sincronizando perfeitamente com a ScrollBar lateral,
/// cliques no trilho, teclado e ScrollViewers aninhados.
/// </summary>
public static class SmoothScroll
{
    private sealed class ScrollState
    {
        public double TargetOffset;
        public double CurrentOffset;
        public long LastTicks;
        public bool Animating;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, ScrollState> States = new();
    private static readonly HashSet<ScrollViewer> ActiveViewers = new();
    private static bool _hooked;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(SmoothScroll),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;

        // Nao intercepta o ScrollViewer interno de caixas de texto (PART_ContentHost)
        if (sv.Name == "PART_ContentHost" || sv.TemplatedParent is TextBoxBase) return;

        if ((bool)e.NewValue)
        {
            sv.PreviewMouseWheel += OnPreviewMouseWheel;
            sv.PreviewMouseDown += OnPreviewMouseDown;
            sv.PreviewKeyDown += OnPreviewKeyDown;
            sv.ScrollChanged += OnScrollChanged;
            sv.Unloaded += OnScrollViewerUnloaded;
        }
        else
        {
            sv.PreviewMouseWheel -= OnPreviewMouseWheel;
            sv.PreviewMouseDown -= OnPreviewMouseDown;
            sv.PreviewKeyDown -= OnPreviewKeyDown;
            sv.ScrollChanged -= OnScrollChanged;
            sv.Unloaded -= OnScrollViewerUnloaded;
            StopViewer(sv);
        }
    }

    private static void OnScrollViewerUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer sv) StopViewer(sv);
    }

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;

        // Ao clicar na barrinha lateral (Thumb ou trilho da ScrollBar) ou botao do meio,
        // encerra imediatamente qualquer inercia ativa e sincroniza o offset real.
        if (e.ChangedButton == MouseButton.Middle ||
            (e.OriginalSource is DependencyObject origin && BelongsToScrollBarOf(origin, sv)))
        {
            SyncAndStop(sv);
        }
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;

        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Space)
        {
            SyncAndStop(sv);
        }
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv || !ReferenceEquals(e.OriginalSource, sv)) return;

        if (States.TryGetValue(sv, out var state) && !state.Animating)
        {
            state.CurrentOffset = e.VerticalOffset;
            state.TargetOffset = e.VerticalOffset;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer sv) return;
        if (sv.Name == "PART_ContentHost" || sv.TemplatedParent is TextBoxBase) return;

        // Se o usuario estiver segurando e arrastando a barrinha no exato instante, nao disputa com o arraste
        if (IsScrollBarDragging(sv))
        {
            SyncAndStop(sv);
            return;
        }

        // Se o cursor estiver sobre um ScrollViewer filho rolavel, deixa o filho tratar a roda do mouse
        if (e.OriginalSource is DependencyObject origin && ShouldDeferToInnerScrollViewer(origin, sv, e.Delta))
        {
            return;
        }

        // ScrollViewers exclusivamente horizontais (ex.: galeria de capturas da Loja) deixam o evento subir para a pagina
        if (sv.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled || sv.ScrollableHeight <= 0)
        {
            return;
        }

        if (!Motion.Enabled)
        {
            return;
        }

        var state = States.GetValue(sv, _ => new ScrollState
        {
            CurrentOffset = sv.VerticalOffset,
            TargetOffset = sv.VerticalOffset
        });

        // Sempre que nao houver animacao ativa (ex.: apos usar a ScrollBar lateral, clicar no trilho ou trocar de aba),
        // ressincroniza 100% a partir do VerticalOffset atual real do ScrollViewer.
        if (!state.Animating)
        {
            state.CurrentOffset = sv.VerticalOffset;
            state.TargetOffset = sv.VerticalOffset;
        }

        // Touchpads de precisao enviam deltas pequenos (< 60); rodas de mouse padrao enviam multiplos de 120
        var stepFactor = Math.Abs(e.Delta) < 60 ? 0.55 : 0.85;
        var deltaPixels = -e.Delta * stepFactor;

        var newTarget = Math.Clamp(state.TargetOffset + deltaPixels, 0.0, sv.ScrollableHeight);

        // Se ja esta encostado no topo/fim, permite propagacao caso exista um ScrollViewer pai
        if (Math.Abs(newTarget - state.TargetOffset) < 0.05 && Math.Abs(sv.VerticalOffset - newTarget) < 0.5)
        {
            return;
        }

        e.Handled = true;
        state.TargetOffset = newTarget;

        if (!state.Animating)
        {
            state.Animating = true;
            state.LastTicks = Stopwatch.GetTimestamp();
            ActiveViewers.Add(sv);
            EnsureRenderingHook();
        }
    }

    private static bool IsScrollBarDragging(ScrollViewer sv)
    {
        return Mouse.LeftButton == MouseButtonState.Pressed &&
               Mouse.Captured is DependencyObject captured &&
               BelongsToScrollBarOf(captured, sv);
    }

    private static bool BelongsToScrollBarOf(DependencyObject? origin, ScrollViewer sv)
    {
        var sawScrollBar = false;
        var current = origin;
        while (current != null)
        {
            if (current is ScrollBar)
            {
                sawScrollBar = true;
            }
            else if (current is ScrollViewer owner)
            {
                return sawScrollBar && ReferenceEquals(owner, sv);
            }

            current = GetParentSafe(current);
        }

        return false;
    }

    private static bool ShouldDeferToInnerScrollViewer(DependencyObject origin, ScrollViewer outer, int delta)
    {
        DependencyObject? current = origin;
        while (current != null && !ReferenceEquals(current, outer))
        {
            if (current is ScrollViewer inner &&
                inner.Name != "PART_ContentHost" &&
                inner.TemplatedParent is not TextBoxBase &&
                inner.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled &&
                inner.ScrollableHeight > 0)
            {
                if (delta > 0 && inner.VerticalOffset > 0.5) return true;
                if (delta < 0 && inner.VerticalOffset < inner.ScrollableHeight - 0.5) return true;
            }

            current = GetParentSafe(current);
        }

        return false;
    }

    private static DependencyObject? GetParentSafe(DependencyObject d)
    {
        if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
        {
            return VisualTreeHelper.GetParent(d);
        }
        return LogicalTreeHelper.GetParent(d);
    }

    private static void EnsureRenderingHook()
    {
        if (_hooked || ActiveViewers.Count == 0) return;
        _hooked = true;
        CompositionTarget.Rendering += OnCompositionRendering;
    }

    private static void SyncAndStop(ScrollViewer sv)
    {
        if (States.TryGetValue(sv, out var state))
        {
            state.CurrentOffset = sv.VerticalOffset;
            state.TargetOffset = sv.VerticalOffset;
            state.Animating = false;
        }
        StopViewer(sv);
    }

    private static void StopViewer(ScrollViewer sv)
    {
        if (States.TryGetValue(sv, out var state))
        {
            state.Animating = false;
        }
        ActiveViewers.Remove(sv);
        if (ActiveViewers.Count == 0 && _hooked)
        {
            _hooked = false;
            CompositionTarget.Rendering -= OnCompositionRendering;
        }
    }

    private static void OnCompositionRendering(object? sender, EventArgs e)
    {
        if (ActiveViewers.Count == 0)
        {
            if (_hooked)
            {
                _hooked = false;
                CompositionTarget.Rendering -= OnCompositionRendering;
            }
            return;
        }

        var now = Stopwatch.GetTimestamp();
        List<ScrollViewer>? finished = null;

        foreach (var sv in ActiveViewers)
        {
            if (!sv.IsLoaded || !sv.IsVisible || !States.TryGetValue(sv, out var state) || !state.Animating)
            {
                finished ??= new List<ScrollViewer>();
                finished.Add(sv);
                continue;
            }

            if (IsScrollBarDragging(sv))
            {
                state.CurrentOffset = sv.VerticalOffset;
                state.TargetOffset = sv.VerticalOffset;
                state.Animating = false;
                finished ??= new List<ScrollViewer>();
                finished.Add(sv);
                continue;
            }

            var dt = (double)(now - state.LastTicks) / Stopwatch.Frequency;
            state.LastTicks = now;
            dt = Math.Clamp(dt, 0.001, 0.05);

            state.TargetOffset = Math.Clamp(state.TargetOffset, 0.0, sv.ScrollableHeight);
            state.CurrentOffset = Math.Clamp(state.CurrentOffset, 0.0, sv.ScrollableHeight);
            var diff = state.TargetOffset - state.CurrentOffset;

            if (Math.Abs(diff) < 0.35)
            {
                state.CurrentOffset = state.TargetOffset;
                sv.ScrollToVerticalOffset(state.CurrentOffset);
                state.Animating = false;
                finished ??= new List<ScrollViewer>();
                finished.Add(sv);
                continue;
            }

            // Decaimento exponencial suave independente de Hz (60Hz / 120Hz / 144Hz / 240Hz)
            var blend = 1.0 - Math.Exp(-17.5 * dt);
            state.CurrentOffset += diff * blend;
            sv.ScrollToVerticalOffset(state.CurrentOffset);
        }

        if (finished != null)
        {
            foreach (var sv in finished)
            {
                ActiveViewers.Remove(sv);
            }

            if (ActiveViewers.Count == 0 && _hooked)
            {
                _hooked = false;
                CompositionTarget.Rendering -= OnCompositionRendering;
            }
        }
    }
}
