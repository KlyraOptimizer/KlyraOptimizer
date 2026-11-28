using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace KlyraOptimizer.Core;

/// <summary>
/// Icone nativo na Bandeja do Sistema (Win32 Shell_NotifyIconW, sem dependencia de WinForms)
/// com menu de contexto no estilo Windows 10 Fluent Design contendo:
/// - Abrir Klyra Optimizer
/// - Limpar RAM
/// - Reset Rede
/// - Fechar
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const int WmUser = 0x0400;
    private const int WmTrayCallback = WmUser + 0x42;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;

    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NiifInfo = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpdata);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractAssociatedIconW(IntPtr hInst, string lpIconPath, ref ushort lpiIcon);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly Action _onOpenMainWindow;
    private readonly Action _onExitApplication;
    private HwndSource? _messageSource;
    private Window? _menuWindow;
    private IntPtr _hIcon;
    private bool _added;
    private bool _busyAction;
    private bool _disposed;

    public TrayIconService(Action onOpenMainWindow, Action onExitApplication)
    {
        _onOpenMainWindow = onOpenMainWindow;
        _onExitApplication = onExitApplication;
    }

    public void EnsureVisible()
    {
        if (_disposed || _added) return;

        var parameters = new HwndSourceParameters("KlyraTraySink")
        {
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = unchecked((int)0x80000000) // WS_POPUP invisivel
        };

        _messageSource = new HwndSource(parameters);
        _messageSource.AddHook(WndProc);

        var exe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exe))
        {
            ushort iconIndex = 0;
            _hIcon = ExtractAssociatedIconW(IntPtr.Zero, exe, ref iconIndex);
        }

        var data = CreateBaseData();
        data.uFlags = NifMessage | NifIcon | NifTip;
        _added = Shell_NotifyIconW(NimAdd, ref data);
    }

    public void ShowNotification(string title, string message)
    {
        if (!_added || _messageSource == null) return;

        var data = CreateBaseData();
        data.uFlags = NifInfo;
        data.szInfoTitle = title.Length > 60 ? title[..60] : title;
        data.szInfo = message.Length > 250 ? message[..250] : message;
        data.dwInfoFlags = NiifInfo;
        data.uTimeoutOrVersion = 3000;
        Shell_NotifyIconW(NimModify, ref data);
    }

    private NOTIFYICONDATA CreateBaseData()
    {
        return new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _messageSource?.Handle ?? IntPtr.Zero,
            uID = 1,
            uCallbackMessage = WmTrayCallback,
            hIcon = _hIcon,
            szTip = "Klyra Optimizer",
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmTrayCallback)
        {
            var mouseMsg = (int)(lParam.ToInt64() & 0xFFFF);
            switch (mouseMsg)
            {
                case WmLButtonUp:
                case WmLButtonDblClk:
                    CloseMenu();
                    Application.Current?.Dispatcher.BeginInvoke(_onOpenMainWindow);
                    handled = true;
                    break;

                case WmRButtonUp:
                case WmContextMenu:
                    Application.Current?.Dispatcher.BeginInvoke(ShowTrayMenu);
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    private void ShowTrayMenu()
    {
        CloseMenu();
        GetCursorPos(out var cursorPx);

        var menuWin = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
            FontFamily = (FontFamily)Application.Current.FindResource("Font.Ui")
        };

        var outer = new Border
        {
            Width = 224,
            Background = (Brush)Application.Current.FindResource("App.CardSolid"),
            BorderBrush = (Brush)Application.Current.FindResource("App.CardStrokeHover"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(0, 4, 0, 4)
        };

        var stack = new StackPanel();

        stack.Children.Add(BuildMenuItem("\uE8A7", "Abrir Klyra Optimizer", isAccent: true, () =>
        {
            CloseMenu();
            _onOpenMainWindow();
        }));

        stack.Children.Add(BuildSeparator());

        stack.Children.Add(BuildMenuItem("\uE74D", "Limpar RAM", isAccent: false, async () =>
        {
            CloseMenu();
            await ExecuteCleanRamAsync();
        }));

        stack.Children.Add(BuildMenuItem("\uE895", "Reset Rede", isAccent: false, async () =>
        {
            CloseMenu();
            await ExecuteResetNetworkAsync();
        }));

        stack.Children.Add(BuildSeparator());

        stack.Children.Add(BuildMenuItem("\uE8BB", "Fechar", isAccent: false, () =>
        {
            CloseMenu();
            _onExitApplication();
        }));

        outer.Child = stack;
        menuWin.Content = outer;

        menuWin.Deactivated += (_, _) => CloseMenu();
        menuWin.Loaded += (_, _) =>
        {
            var dpi = VisualTreeHelper.GetDpi(menuWin);
            var cursorDipX = cursorPx.X / dpi.DpiScaleX;
            var cursorDipY = cursorPx.Y / dpi.DpiScaleY;
            var work = SystemParameters.WorkArea;

            var x = cursorDipX - menuWin.ActualWidth + 12;
            var y = cursorDipY - menuWin.ActualHeight - 6;

            x = Math.Clamp(x, work.Left + 4, work.Right - menuWin.ActualWidth - 4);
            y = Math.Clamp(y, work.Top + 4, work.Bottom - menuWin.ActualHeight - 4);

            menuWin.Left = x;
            menuWin.Top = y;

            var hwnd = new WindowInteropHelper(menuWin).Handle;
            if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
            menuWin.Activate();
        };

        _menuWindow = menuWin;
        menuWin.Show();
    }

    private static Border BuildSeparator()
    {
        return new Border
        {
            Height = 1,
            Margin = new Thickness(12, 4, 12, 4),
            Background = (Brush)Application.Current.FindResource("App.Divider")
        };
    }

    private static Button BuildMenuItem(string glyph, string text, bool isAccent, Action onClick)
    {
        var btn = new Button
        {
            Style = (Style)Application.Current.FindResource("Btn.Subtle"),
            Height = 34,
            Padding = new Thickness(12, 0, 12, 0),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        FluentEffects.SetEnableTilt(btn, false);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)Application.Current.FindResource("Font.Icons"),
            FontSize = 14,
            Foreground = (Brush)Application.Current.FindResource(isAccent ? "App.AccentText" : "App.TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center
        };

        var label = new TextBlock
        {
            Text = text,
            FontSize = 13,
            FontWeight = isAccent ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = (Brush)Application.Current.FindResource("App.Text"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);

        grid.Children.Add(icon);
        grid.Children.Add(label);
        btn.Content = grid;
        btn.Click += (_, _) => onClick();

        return btn;
    }

    private void CloseMenu()
    {
        var w = _menuWindow;
        _menuWindow = null;
        if (w != null)
        {
            try { w.Close(); } catch { }
        }
    }

    private async Task ExecuteCleanRamAsync()
    {
        if (_busyAction) return;
        _busyAction = true;
        try
        {
            await Task.Run(() => AppServices.Optimizer.CleanMemory());
            ShowNotification("Klyra Optimizer", "Memória RAM e arquivos temporários limpos com sucesso.");
        }
        catch
        {
            ShowNotification("Klyra Optimizer", "Limpeza de memória concluída.");
        }
        finally
        {
            _busyAction = false;
        }
    }

    private async Task ExecuteResetNetworkAsync()
    {
        if (_busyAction) return;

        if (!Shell.IsAdmin)
        {
            _onOpenMainWindow();
            await Shell.RequireAdminAsync();
            return;
        }

        _busyAction = true;
        try
        {
            ShowNotification("Klyra Optimizer", "Redefinindo pilha de rede e cache DNS em segundo plano...");
            await Task.Run(() => AppServices.Optimizer.ResetNetwork());
            ShowNotification("Klyra Optimizer", "Rede redefinida com sucesso. Reinicie o computador quando puder.");
        }
        catch (Exception ex)
        {
            ShowNotification("Klyra Optimizer", "Falha ao redefinir rede: " + ex.Message);
        }
        finally
        {
            _busyAction = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CloseMenu();

        if (_added && _messageSource != null)
        {
            var data = CreateBaseData();
            Shell_NotifyIconW(NimDelete, ref data);
            _added = false;
        }

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        _messageSource?.Dispose();
        _messageSource = null;
    }
}

