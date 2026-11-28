using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace KlyraOptimizer.Core;

/// <summary>
/// Integracao Win32 nativa do Windows 10:
/// 1) Vidro fosco Acrylic (SetWindowCompositionAttribute) + cantos retos;
/// 2) Eliminacao total do lag de arraste do DWM (WM_ENTERSIZEMOVE / WM_EXITSIZEMOVE):
///    alterna dinamicamente entre EnableAcrylicBlurBehind (4) em repouso e EnableBlurBehind (3)
///    durante o movimento da janela, mantendo o desfoque de vidro ao vivo a 144Hz sem travar;
/// 3) Hook WM_GETMINMAXINFO que encaixa a janela maximizada exatamente na area
///    de trabalho (rcWork) respeitando a Barra de Tarefas em qualquer monitor/DPI.
/// </summary>
public static class Win10Acrylic
{
    private enum AccentState
    {
        Disabled = 0,
        EnableGradient = 1,
        EnableTransparentGradient = 2,
        EnableBlurBehind = 3,
        EnableAcrylicBlurBehind = 4,
        EnableHostBackdrop = 5
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public uint AccentFlags;
        public uint GradientColor;
        public uint AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttribData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    private const int WcaAccentPolicy = 19;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpDoNotRound = 1;
    private const int DwmwaUseImmersiveDarkMode = 20;

    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint AbmGetState = 0x00000004;
    private const int AbsAutohide = 0x00000001;

    public static bool IsMovingOrResizing { get; private set; }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttribData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shell32.dll")]
    private static extern uint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    /// <summary>
    /// Registra os hooks Win32 de maximizacao (WM_GETMINMAXINFO) e de arraste sem lag
    /// (WM_ENTERSIZEMOVE / WM_EXITSIZEMOVE).
    /// </summary>
    public static void AttachWindowHooks(Window window, Action? onMoveStateChanged = null)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook((IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            switch (msg)
            {
                case WmGetMinMaxInfo:
                    WmGetMinMaxInfoHandler(window, hWnd, lParam);
                    handled = true;
                    break;

                case WmEnterSizeMove:
                    if (!IsMovingOrResizing)
                    {
                        IsMovingOrResizing = true;
                        onMoveStateChanged?.Invoke();
                    }
                    break;

                case WmExitSizeMove:
                    if (IsMovingOrResizing)
                    {
                        IsMovingOrResizing = false;
                        onMoveStateChanged?.Invoke();
                    }
                    break;
            }
            return IntPtr.Zero;
        });
    }

    private static void WmGetMinMaxInfoHandler(Window window, IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref monitorInfo))
            {
                var rcWork = monitorInfo.rcWork;
                var rcMonitor = monitorInfo.rcMonitor;

                var maxX = rcWork.Left - rcMonitor.Left;
                var maxY = rcWork.Top - rcMonitor.Top;
                var maxW = rcWork.Right - rcWork.Left;
                var maxH = rcWork.Bottom - rcWork.Top;

                if (rcWork.Left == rcMonitor.Left &&
                    rcWork.Top == rcMonitor.Top &&
                    rcWork.Right == rcMonitor.Right &&
                    rcWork.Bottom == rcMonitor.Bottom &&
                    IsTaskbarAutoHide())
                {
                    maxH = Math.Max(100, maxH - 1);
                }

                mmi.ptMaxPosition.X = maxX;
                mmi.ptMaxPosition.Y = maxY;
                mmi.ptMaxSize.X = maxW;
                mmi.ptMaxSize.Y = maxH;
                mmi.ptMaxTrackSize.X = maxW;
                mmi.ptMaxTrackSize.Y = maxH;
            }
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        if (window.MinWidth > 0 && !double.IsInfinity(window.MinWidth))
        {
            mmi.ptMinTrackSize.X = (int)Math.Ceiling(window.MinWidth * dpi.DpiScaleX);
        }
        if (window.MinHeight > 0 && !double.IsInfinity(window.MinHeight))
        {
            mmi.ptMinTrackSize.Y = (int)Math.Ceiling(window.MinHeight * dpi.DpiScaleY);
        }

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private static bool IsTaskbarAutoHide()
    {
        try
        {
            var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
            return (SHAppBarMessage(AbmGetState, ref abd) & AbsAutohide) != 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool SystemTransparencyEnabled()
    {
        try
        {
            var val = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "EnableTransparency",
                1);
            return val is not int i || i != 0;
        }
        catch
        {
            return true;
        }
    }

    public static void ForceSquareCorners(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var pref = DwmwcpDoNotRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref pref, sizeof(int));
        }
        catch { }
    }

    public static bool Apply(Window window, bool lightTheme, bool windowActive)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        ForceSquareCorners(window);

        try
        {
            var dark = lightTheme ? 0 : 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        }
        catch { }

        var useAcrylic = windowActive && SystemTransparencyEnabled();

        uint tintAbgr;
        AccentState state;

        if (useAcrylic)
        {
            // Durante o arraste (WM_ENTERSIZEMOVE), o EnableAcrylicBlurBehind (4) sofre um gargalo
            // conhecido do DWM no Windows 10/11 ao recalcular ruido+gaussiana em 500Hz.
            // Usamos EnableBlurBehind (3) apenas enquanto o mouse arrasta a janela (mantendo o
            // vidro desfocado ao vivo sem nenhum lag!) e voltamos para EnableAcrylicBlurBehind (4)
            // no exato instante em que o usuario solta o mouse (WM_EXITSIZEMOVE).
            state = IsMovingOrResizing
                ? AccentState.EnableBlurBehind
                : AccentState.EnableAcrylicBlurBehind;
            tintAbgr = lightTheme ? 0xCCEBEBEBu : 0xC4171717u;
        }
        else
        {
            state = AccentState.EnableGradient;
            tintAbgr = lightTheme ? 0xFFE6E6E6u : 0xFF1F1F1Fu;
        }

        var accent = new AccentPolicy
        {
            AccentState = state,
            AccentFlags = 2,
            GradientColor = tintAbgr,
            AnimationId = 0
        };

        var size = Marshal.SizeOf<AccentPolicy>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttribData
            {
                Attribute = WcaAccentPolicy,
                SizeOfData = size,
                Data = ptr
            };
            SetWindowCompositionAttribute(hwnd, ref data);
            return useAcrylic;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
