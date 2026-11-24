using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using KlyraOptimizer.Core;
using KlyraOptimizer.Views;

namespace KlyraOptimizer;

public partial class MainWindow : Window
{
    private const double ExpandedPaneWidth = 268;
    private const double CompactPaneWidth = 48;

    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private readonly Stack<string> _backStack = new();
    private string _current = "home";
    private bool _suppressHistory;
    private bool _paneOpen = true;
    private bool _windowActive = true;

    private static readonly string[] SmokePages =
        { "home", "optimizer", "tool", "store", "tools", "restore", "settings" };

    public MainWindow()
    {
        InitializeComponent();
        FitToWorkArea();

        AppServices.NavigationRequested = key =>
            Dispatcher.BeginInvoke(new Action(() => Navigate(key)), DispatcherPriority.Normal);

        SourceInitialized += (_, _) =>
        {
            Win10Acrylic.AttachMaximizeFix(this);
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

            // Com o Acrylic DWM ligado, o painel lateral usa tint translucido; quando a janela
            // perde o foco ou transparencia esta desligada, usa o fallback solido do UWP Win10.
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
            Motion.FadeIn(root, offsetY: 0, milliseconds: 200);
        }

        var args = Environment.GetCommandLineArgs();
        var index = Array.FindIndex(args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length && SmokePages.Contains(args[index + 1]))
        {
            _current = args[index + 1];
        }

        _suppressHistory = true;
        SyncNavSelection(_current);
        Navigate(_current, pushHistory: false);
        _suppressHistory = false;
    }

    private void Hamburger_Click(object sender, RoutedEventArgs e)
    {
        _paneOpen = !_paneOpen;
        Motion.AnimateWidth(NavPane, _paneOpen ? ExpandedPaneWidth : CompactPaneWidth);
        UpdatePaneElements();
    }

    private void UpdatePaneElements()
    {
        PaneTitlePanel.Visibility = _paneOpen ? Visibility.Visible : Visibility.Collapsed;
        PaneBrandLabel.Visibility = _paneOpen ? Visibility.Visible : Visibility.Collapsed;
        CollapsedTitleText.Visibility = _paneOpen ? Visibility.Collapsed : Visibility.Visible;
        ModeStatusBox.Visibility = _paneOpen ? Visibility.Visible : Visibility.Collapsed;
        AdminBanner.Visibility = _paneOpen && !Shell.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        // Se a Loja estiver aberta em um aplicativo especifico, volta primeiro para a vitrine
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
        var canBack = storeCanBack || _backStack.Count > 0;
        BackButton.Visibility = canBack ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxRestore_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

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

    public void Navigate(string key, bool pushHistory = true)
    {
        if (PageHost == null) return;

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

        if (page is IRefreshablePage refreshable)
        {
            refreshable.OnActivated();
        }

        UpdateBackButton();

        Dispatcher.BeginInvoke(
            new Action(() => Motion.PageIn(page)),
            DispatcherPriority.Loaded);
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

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        if (Shell.RelaunchElevated("--elevated-launch"))
        {
            Application.Current.Shutdown();
        }
        else
        {
            MessageBox.Show(
                "Não foi possível solicitar a elevação. Feche o Klyra Optimizer e abra novamente com " +
                "\"Executar como administrador\".",
                AppServices.AppName,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------------
    // Teste automatizado de construcao das telas (usado em build/CI)
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
                }

                // Testa tambem recolher e expandir o SplitView do Windows 10
                Hamburger_Click(this, new RoutedEventArgs());
                UpdateLayout();
                Hamburger_Click(this, new RoutedEventArgs());
                UpdateLayout();
                report.AppendLine("OK: splitview");

                // Testa Maximizar: garante que nao transborda a area util (rcWork / barra de tarefas)
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

                File.WriteAllText(resultFile, report.ToString());
            }
            catch (Exception ex)
            {
                report.AppendLine("FALHA: " + ex);
                try { File.WriteAllText(resultFile, report.ToString()); } catch { }
            }

            Application.Current.Shutdown(0);
        }), DispatcherPriority.ApplicationIdle);
    }

    private static void CheckOverlay(StringBuilder report, FrameworkElement? page, string name)
    {
        if (page?.FindName(name) is not FrameworkElement overlay) return;

        Motion.ShowOverlay(overlay);
        Pump(380);

        var card = overlay is Panel panel && panel.Children.Count > 0 ? panel.Children[0] as FrameworkElement : null;
        var scaleX = (card?.RenderTransform as ScaleTransform)?.ScaleX ?? 1;

        var opened = overlay.Visibility == Visibility.Visible &&
                     overlay.Opacity > 0.9 &&
                     scaleX > 0.99;

        report.AppendLine(opened
            ? $"OK: {name} (abre)"
            : $"FALHA: {name} (abre) — visibility={overlay.Visibility} opacity={overlay.Opacity:0.00} escala={scaleX:0.00}");

        Motion.HideOverlay(overlay);
        Pump(240);

        report.AppendLine(overlay.Visibility == Visibility.Collapsed
            ? $"OK: {name} (fecha)"
            : $"FALHA: {name} (fecha) — visibility={overlay.Visibility}");
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
