using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KlyraOptimizer.Core;
using KlyraOptimizer.Views;

namespace KlyraOptimizer;

public sealed class NavSearchEntry
{
    public string Glyph { get; init; } = "\uE721";
    public string Label { get; init; } = string.Empty;
    public string Keywords { get; init; } = string.Empty;
    public string TargetPage { get; init; } = "home";
    public string? StoreQuery { get; init; }
}

public partial class MainWindow : Window
{
    private const double ExpandedPaneWidth = 280;
    private const double CompactPaneWidth = 49;

    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private readonly Stack<string> _backStack = new();
    private string _current = "home";
    private bool _suppressHistory;
    private bool _paneOpen = false;
    private bool _windowActive = true;
    private bool _trayHintShown;
    private TaskCompletionSource<bool>? _dialogTcs;

    private static readonly string[] SmokePages =
        { "home", "optimizer", "tool", "store", "tools", "restore", "settings" };

    private static readonly NavSearchEntry[] SearchIndex =
    {
        new() { Glyph = "\uE80F", Label = "Início — Visão geral do sistema", Keywords = "inicio home dashboard sistema status", TargetPage = "home" },
        new() { Glyph = "\uE945", Label = "Otimizador — Perfis Standard, Medium e Avançado", Keywords = "otimizador standard medium avancado fps telemetria compactos delay", TargetPage = "optimizer" },
        new() { Glyph = "\uE90F", Label = "Klyra Tool — Alternar recursos individuais ao vivo", Keywords = "klyra tool toggles windows update copilot gamedvr vbs hvci sysmain superfetch bluetooth", TargetPage = "tool" },
        new() { Glyph = "\uECC9", Label = "Remover apps do computador — Desinstalar bloatware", Keywords = "remover apps aplicativos desinstalar bloatware computador", TargetPage = "tool" },
        new() { Glyph = "\uE74D", Label = "Limpar memória RAM e arquivos temporários", Keywords = "limpar ram memoria temp cache", TargetPage = "tool" },
        new() { Glyph = "\uE895", Label = "Reset completo de rede (Winsock, TCP/IP e DNS)", Keywords = "rede dns winsock ping internet latencia", TargetPage = "tool" },
        new() { Glyph = "\uE719", Label = "Microsoft Store — Baixar aplicativos via winget", Keywords = "loja microsoft store winget aplicativos jogos spotify discord whatsapp", TargetPage = "store" },
        new() { Glyph = "\uEC7A", Label = "Ferramentas — Scanner de conflitos, WinUtil e BleachBit", Keywords = "ferramentas conflitos scanner winutil bleachbit", TargetPage = "tools" },
        new() { Glyph = "\uE7FC", Label = "Hardware — MSI Mode das GPUs PCI", Keywords = "gpu msi placa de video nvidia amd latencia", TargetPage = "tools" },
        new() { Glyph = "\uE81C", Label = "Restauração — Pontos de restauração e Reverter tudo", Keywords = "restauracao ponto reverter backup rstrui padrao", TargetPage = "restore" },
        new() { Glyph = "\uE713", Label = "Configurações — Tema, logs e sobre o Klyra Optimizer", Keywords = "configuracoes tema escuro claro logs desinstalar sobre", TargetPage = "settings" }
    };

    public MainWindow()
    {
        InitializeComponent();
        FitToWorkArea();

        AppServices.NavigationRequested = key =>
            Dispatcher.BeginInvoke(new Action(() => Navigate(key)), DispatcherPriority.Normal);

        AppServices.DialogRequested = ShowModalDialogAsync;

        SourceInitialized += (_, _) =>
        {
            Win10Acrylic.AttachWindowHooks(this, ApplyWindowAcrylic);
            ApplyWindowAcrylic();
        };
        Activated += (_, _) =>
        {
            _windowActive = true;
            ApplyWindowAcrylic();
        };
        Deactivated += (_, _) =>
        {
            _windowActive = false;
            ApplyWindowAcrylic();
        };
        StateChanged += (_, _) => UpdateWindowStateVisuals();

        AppServices.StateChanged += UpdateChrome;
        ThemeService.Changed += ApplyWindowAcrylic;
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            AppServices.StateChanged -= UpdateChrome;
            ThemeService.Changed -= ApplyWindowAcrylic;
        };
    }

    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;

        MinWidth = Math.Min(MinWidth, Math.Max(720, area.Width - 80));
        MinHeight = Math.Min(MinHeight, Math.Max(520, area.Height - 80));

        Width = Math.Min(Width, area.Width * 0.95);
        Height = Math.Min(Height, area.Height * 0.94);
    }

    private void ApplyWindowAcrylic()
    {
        try
        {
            var acrylicActive = Win10Acrylic.Apply(this, ThemeService.IsLight, _windowActive);

            NavPane.Background = (Brush)FindResource(acrylicActive ? "App.PaneAcrylic" : "App.PaneFallback");
            OuterFrame.BorderBrush = _windowActive
                ? (Brush)FindResource("App.WindowBorder")
                : (Brush)FindResource("App.CardStroke");
        }
        catch (Exception ex)
        {
            AppServices.Log?.Write("[TEMA] falha ao aplicar Acrylic: " + ex.Message, LogKind.Warning);
        }
    }

    private void UpdateWindowStateVisuals()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaxRestoreButton.Content = maximized ? "\uE923" : "\uE922";
        MaxRestoreButton.ToolTip = maximized ? "Restaurar tamanho" : "Maximizar";
        OuterFrame.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);

        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        if (chrome != null)
        {
            chrome.ResizeBorderThickness = maximized ? new Thickness(0) : new Thickness(6);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWindowAcrylic();
        UpdateChrome();
        UpdateWindowStateVisuals();

        if (Content is FrameworkElement root)
        {
            Motion.FadeIn(root, offsetY: 0, milliseconds: 180);
        }

        var args = Environment.GetCommandLineArgs();
        var index = Array.FindIndex(args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length && SmokePages.Contains(args[index + 1]))
        {
            _current = args[index + 1];
        }

        _suppressHistory = true;
        SyncNavSelection(_current);
        Navigate(_current, pushHistory: false, animate: false);
        _suppressHistory = false;

        if (!App.IsSmokeTest)
        {
            _ = Services.UpdateCheckerService.CheckForUpdatesAsync(this);
        }
    }

    // ------------------------------------------------------------------
    // NavigationView SplitView & AutoSuggestBox (estilo XAML Controls Gallery)
    // ------------------------------------------------------------------
    private void Hamburger_Click(object sender, RoutedEventArgs e)
    {
        _paneOpen = !_paneOpen;
        NavSuggestionsPopup.IsOpen = false;
        Motion.AnimateWidth(NavPane, _paneOpen ? ExpandedPaneWidth : CompactPaneWidth);
        UpdatePaneElements();
    }

    private void UpdatePaneElements()
    {
        PaneAppTitle.Visibility = _paneOpen ? Visibility.Visible : Visibility.Collapsed;
        CollapsedTitleText.Visibility = _paneOpen ? Visibility.Collapsed : Visibility.Visible;
        NavSearchHost.Visibility = _paneOpen ? Visibility.Visible : Visibility.Collapsed;
        NavSearchCompactButton.Visibility = _paneOpen ? Visibility.Collapsed : Visibility.Visible;
        ModeStatusBox.Visibility = _paneOpen ? Visibility.Visible : Visibility.Collapsed;
        AdminBanner.Visibility = _paneOpen && !Shell.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NavSearchCompact_Click(object sender, RoutedEventArgs e)
    {
        if (!_paneOpen)
        {
            _paneOpen = true;
            Motion.AnimateWidth(NavPane, ExpandedPaneWidth);
            UpdatePaneElements();
        }

        Dispatcher.BeginInvoke(new Action(() => NavSearchBox.Focus()), DispatcherPriority.Input);
    }

    private void NavSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = NavSearchBox.Text.Trim();
        if (query.Length == 0)
        {
            NavSuggestionsPopup.IsOpen = false;
            return;
        }

        var matches = SearchIndex
            .Where(item =>
                item.Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Keywords.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(6)
            .ToList();

        matches.Add(new NavSearchEntry
        {
            Glyph = "\uE719",
            Label = $"Buscar \"{query}\" na Microsoft Store",
            TargetPage = "store",
            StoreQuery = query
        });

        NavSuggestionsList.ItemsSource = matches;
        NavSuggestionsPopup.IsOpen = true;
    }

    private void NavSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            NavSuggestionsPopup.IsOpen = false;
            return;
        }

        if (e.Key == Key.Enter)
        {
            SubmitNavSearch();
        }
    }

    private void NavSearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!NavSuggestionsPopup.IsKeyboardFocusWithin)
            {
                NavSuggestionsPopup.IsOpen = false;
            }
        }), DispatcherPriority.Background);
    }

    private void NavSearchSubmit_Click(object sender, RoutedEventArgs e) => SubmitNavSearch();

    private void SubmitNavSearch()
    {
        var query = NavSearchBox.Text.Trim();
        if (query.Length == 0) return;

        NavSuggestionsPopup.IsOpen = false;

        var firstMatch = SearchIndex.FirstOrDefault(item =>
            item.Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            item.Keywords.Contains(query, StringComparison.OrdinalIgnoreCase));

        if (firstMatch != null)
        {
            Navigate(firstMatch.TargetPage);
        }
        else
        {
            NavigateToStoreSearch(query);
        }
    }

    private void NavSuggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: NavSearchEntry entry }) return;
        NavSuggestionsPopup.IsOpen = false;
        NavSearchBox.Clear();

        if (!string.IsNullOrWhiteSpace(entry.StoreQuery))
        {
            NavigateToStoreSearch(entry.StoreQuery!);
        }
        else
        {
            Navigate(entry.TargetPage);
        }
    }

    private void NavigateToStoreSearch(string query)
    {
        Navigate("store");
        if (PageHost.Content is StorePage store)
        {
            store.RunExternalSearch(query);
        }
    }

    // ------------------------------------------------------------------
    // Navegacao & Historico (Entrance / DrillIn)
    // ------------------------------------------------------------------
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_current == "store" && PageHost.Content is StorePage store && store.CanNavigateBack)
        {
            store.NavigateBackToHome();
            UpdateBackButton();
            return;
        }

        if (_backStack.Count == 0) return;

        var previous = _backStack.Pop();
        _suppressHistory = true;
        SyncNavSelection(previous);
        Navigate(previous, pushHistory: false);
        _suppressHistory = false;
    }

    public void UpdateBackButton()
    {
        var storeCanBack = _current == "store" && PageHost.Content is StorePage s && s.CanNavigateBack;
        BackButton.IsEnabled = storeCanBack || _backStack.Count > 0;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxRestore_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.IsExiting && !App.IsSmokeTest && AppSettings.Current.CloseToTray)
        {
            e.Cancel = true;
            AppServices.Tray?.EnsureVisible();
            Hide();

            if (!_trayHintShown)
            {
                _trayHintShown = true;
                AppServices.Tray?.ShowNotification(
                    AppServices.AppName,
                    "O Klyra Optimizer continua ativo na bandeja do sistema.");
            }
            return;
        }

        base.OnClosing(e);
    }

    public void RestoreFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void SyncNavSelection(string key)
    {
        var target = key switch
        {
            "optimizer" => NavOptimizer,
            "tool" => NavTool,
            "store" => NavStore,
            "tools" => NavTools,
            "restore" => NavRestore,
            "settings" => NavSettings,
            _ => NavHome
        };

        target.IsChecked = true;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { CommandParameter: string key } && !_suppressHistory)
        {
            Navigate(key, pushHistory: true);
        }
    }

    public void Navigate(string key, bool pushHistory = true, bool animate = true)
    {
        if (PageHost == null) return;
        if (PageHost.Content != null && _current == key && pushHistory && !_suppressHistory) return;

        if (pushHistory && !_suppressHistory && !string.IsNullOrEmpty(_current) && _current != key)
        {
            _backStack.Push(_current);
        }

        if (!_pages.TryGetValue(key, out var page))
        {
            page = CreatePage(key);
            if (page == null) return;
            _pages[key] = page;
        }

        PageHost.Content = page;
        _current = key;

        _suppressHistory = true;
        SyncNavSelection(key);
        _suppressHistory = false;

        if (animate)
        {
            Motion.PageIn(PageHost);
        }

        if (page is IRefreshablePage refreshable)
        {
            refreshable.OnActivated();
        }

        UpdateBackButton();
    }

    private FrameworkElement? CreatePage(string key) => key switch
    {
        "home" => new DashboardPage(),
        "optimizer" => new OptimizerPage(),
        "tool" => new KlyraToolPage(),
        "store" => new StorePage(UpdateBackButton),
        "tools" => new ToolsPage(),
        "restore" => new RestorePage(),
        "settings" => new SettingsPage(),
        _ => null
    };

    private void UpdateChrome()
    {
        var mode = AppServices.System.ActiveMode;
        ModeText.Text = AppServices.System.ActiveModeLabel;
        ModeGlyph.Text = mode == ActiveMode.None ? "\uE946" : "\uE930";
        ModeGlyph.Foreground = mode == ActiveMode.None
            ? (Brush)FindResource("App.TextTertiary")
            : (Brush)FindResource("App.SuccessText");

        UpdatePaneElements();
    }

    private async void Elevate_Click(object sender, RoutedEventArgs e)
    {
        if (Shell.RelaunchElevated("--elevated-launch"))
        {
            App.ExitCompletely();
        }
        else
        {
            await ShowModalDialogAsync(
                AppServices.AppName,
                "Não foi possível solicitar a elevação automaticamente. Feche o Klyra Optimizer e abra novamente com \"Executar como administrador\".",
                "Entendi",
                null,
                false);
        }
    }

    // ------------------------------------------------------------------
    // ContentDialog Modal Global (Windows 10 UWP 1809)
    // ------------------------------------------------------------------
    public Task<bool> ShowModalDialogAsync(
        string title,
        string content,
        string primaryText,
        string? closeText,
        bool isDanger)
    {
        _dialogTcs?.TrySetResult(false);
        var tcs = new TaskCompletionSource<bool>();
        _dialogTcs = tcs;

        GlobalDialogTitle.Text = title;
        GlobalDialogBody.Text = content;
        GlobalDialogPrimaryBtn.Content = primaryText;
        GlobalDialogPrimaryBtn.Style = (Style)FindResource(isDanger ? "Btn.Danger" : "Btn.Accent");

        if (string.IsNullOrWhiteSpace(closeText))
        {
            GlobalDialogCloseBtn.Visibility = Visibility.Collapsed;
            GlobalDialogButtonsGrid.Columns = 1;
            GlobalDialogPrimaryBtn.Margin = new Thickness(0);
        }
        else
        {
            GlobalDialogCloseBtn.Content = closeText;
            GlobalDialogCloseBtn.Visibility = Visibility.Visible;
            GlobalDialogButtonsGrid.Columns = 2;
            GlobalDialogPrimaryBtn.Margin = new Thickness(0, 0, 4, 0);
        }

        Motion.ShowOverlay(GlobalDialogOverlay);
        return tcs.Task;
    }

    private void GlobalDialogPrimary_Click(object sender, RoutedEventArgs e)
    {
        Motion.HideOverlay(GlobalDialogOverlay);
        var tcs = _dialogTcs;
        _dialogTcs = null;
        tcs?.TrySetResult(true);
    }

    private void GlobalDialogClose_Click(object sender, RoutedEventArgs e)
    {
        Motion.HideOverlay(GlobalDialogOverlay);
        var tcs = _dialogTcs;
        _dialogTcs = null;
        tcs?.TrySetResult(false);
    }

    // ------------------------------------------------------------------
    // Smoke test automatizado
    // ------------------------------------------------------------------
    public void RunSmokeTest(string resultFile)
    {
        var report = new StringBuilder();

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                foreach (var key in SmokePages)
                {
                    Navigate(key, pushHistory: false);
                    UpdateLayout();
                    report.AppendLine($"OK: {key}");

                    CheckOverlay(report, PageHost.Content as FrameworkElement, "ConfirmOverlay");
                    CheckOverlay(report, PageHost.Content as FrameworkElement, "RebootOverlay");
                    CheckOverlay(report, PageHost.Content as FrameworkElement, "AppRemovalOverlay");
                }

                ThemeService.Apply(ThemeMode.Light);
                UpdateLayout();
                ThemeService.Apply(ThemeMode.Dark);
                UpdateLayout();
                report.AppendLine("OK: theme-light-dark");

                CheckOverlay(report, this, "GlobalDialogOverlay");

                Hamburger_Click(this, new RoutedEventArgs());
                UpdateLayout();
                Hamburger_Click(this, new RoutedEventArgs());
                UpdateLayout();
                report.AppendLine("OK: splitview");

                WindowState = WindowState.Maximized;
                UpdateLayout();
                Pump(120);
                var work = SystemParameters.WorkArea;
                var fitsWorkArea = ActualWidth <= work.Width + 2 && ActualHeight <= work.Height + 2 && OuterFrame.BorderThickness.Top == 0;
                report.AppendLine(fitsWorkArea
                    ? $"OK: maximize ({ActualWidth:0}x{ActualHeight:0} <= WorkArea {work.Width:0}x{work.Height:0})"
                    : $"FALHA: maximize ({ActualWidth:0}x{ActualHeight:0} vs WorkArea {work.Width:0}x{work.Height:0})");
                WindowState = WindowState.Normal;
                UpdateLayout();

                CheckSmoothScrollAfterScrollBar(report);

                File.WriteAllText(resultFile, report.ToString());
            }
            catch (Exception ex)
            {
                report.AppendLine("FALHA: " + ex);
                try { File.WriteAllText(resultFile, report.ToString()); } catch { }
            }

            App.ExitCompletely();
        }), DispatcherPriority.ApplicationIdle);
    }

    private static void CheckOverlay(StringBuilder report, FrameworkElement? page, string name)
    {
        if (page?.FindName(name) is not FrameworkElement overlay) return;

        Motion.ShowOverlay(overlay);
        Pump(360);

        var card = overlay is Panel panel && panel.Children.Count > 0 ? panel.Children[0] as FrameworkElement : null;
        var scaleX = (card?.RenderTransform as ScaleTransform)?.ScaleX ?? 1;

        var opened = overlay.Visibility == Visibility.Visible &&
                     overlay.Opacity > 0.9 &&
                     scaleX > 0.99;

        report.AppendLine(opened
            ? $"OK: {name} (abre)"
            : $"FALHA: {name} (abre) — visibility={overlay.Visibility} opacity={overlay.Opacity:0.00} escala={scaleX:0.00}");

        Motion.HideOverlay(overlay);
        Pump(220);

        report.AppendLine(overlay.Visibility == Visibility.Collapsed
            ? $"OK: {name} (fecha)"
            : $"FALHA: {name} (fecha) — visibility={overlay.Visibility}");
    }

    private void CheckSmoothScrollAfterScrollBar(StringBuilder report)
    {
        var prevHeight = Height;
        Height = 540;
        Navigate("optimizer", pushHistory: false, animate: false);
        UpdateLayout();
        Pump(80);

        var sv = FindFirstDescendant<ScrollViewer>(PageHost.Content as DependencyObject);
        if (sv == null || sv.ScrollableHeight < 100)
        {
            Height = prevHeight;
            UpdateLayout();
            report.AppendLine($"FALHA: smoothscroll — ScrollViewer nao encontrado ou sem altura rolavel ({sv?.ScrollableHeight:0})");
            return;
        }

        // 1) Rola para baixo com o scroll do mouse
        sv.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
            Source = sv
        });
        Pump(220);
        var afterFirstWheel = sv.VerticalOffset;

        // 2) Usa a barrinha do canto (ScrollBar lateral) ate o final da pagina
        if (sv.Template.FindName("PART_VerticalScrollBar", sv) is System.Windows.Controls.Primitives.ScrollBar bar)
        {
            bar.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseDownEvent,
                Source = bar
            });
        }
        sv.ScrollToVerticalOffset(sv.ScrollableHeight);
        UpdateLayout();
        Pump(80);
        var afterBar = sv.VerticalOffset;

        // 3) Volta a rolar com o scroll do mouse para cima apos ter usado a barrinha
        sv.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 240)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
            Source = sv
        });
        Pump(260);
        var afterSecondWheel = sv.VerticalOffset;

        var maxScroll = sv.ScrollableHeight;
        sv.ScrollToHome();
        Height = prevHeight;
        UpdateLayout();

        var ok = afterFirstWheel > 15 &&
                 afterBar >= maxScroll - 2 &&
                 afterSecondWheel < afterBar - 50;

        report.AppendLine(ok
            ? $"OK: smoothscroll+scrollbar ({afterFirstWheel:0} -> barra {afterBar:0} -> wheel {afterSecondWheel:0})"
            : $"FALHA: smoothscroll+scrollbar ({afterFirstWheel:0} -> barra {afterBar:0} -> wheel {afterSecondWheel:0})");
    }

    private static T? FindFirstDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root == null) return null;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var nested = FindFirstDescendant<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(milliseconds),
            DispatcherPriority.Background,
            (_, _) => frame.Continue = false,
            Dispatcher.CurrentDispatcher);

        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }
}
