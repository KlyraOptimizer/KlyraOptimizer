using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace KlyraOptimizer.Core;

public enum ThemeMode
{
    System,
    Light,
    Dark
}

/// <summary>
/// Tema Fluent Design do Windows 10: aplica paleta clara/escura baseada nos
/// SystemChrome* / SystemBase* brushes do Windows 10 SDK 10.0.19041.0 e le a
/// cor de destaque do Windows (#0078D7 por padrao).
/// </summary>
public static class ThemeService
{
    private static ResourceDictionary? _tokens;

    public static ThemeMode Mode { get; private set; } = ThemeMode.System;

    public static bool IsLight { get; private set; }

    public static event Action? Changed;

    private static readonly Dictionary<string, (string Dark, string Light)> Palette = new()
    {
        // Painel lateral sobre o Acrylic nativo da janela: transparente suave quando Acrylic esta ativo
        ["App.PaneAcrylic"] = ("#221F1F1F", "#28F2F2F2"),
        ["App.PaneFallback"] = ("#FF1F1F1F", "#FFE6E6E6"),
        ["App.PageBackground"] = ("#FF111111", "#FFFFFFFF"),
        ["App.Layer"] = ("#FF171717", "#FFF2F2F2"),
        ["App.Card"] = ("#FF1F1F1F", "#FFF5F5F5"),
        ["App.CardHover"] = ("#FF282828", "#FFEAEAEA"),
        ["App.CardStroke"] = ("#24FFFFFF", "#1E000000"),
        ["App.CardStrokeHover"] = ("#58FFFFFF", "#50000000"),
        ["App.CardSolid"] = ("#FF2B2B2B", "#FFFFFFFF"),
        ["App.Divider"] = ("#22FFFFFF", "#1A000000"),
        ["App.Hover"] = ("#19FFFFFF", "#14000000"),
        ["App.Pressed"] = ("#33FFFFFF", "#29000000"),
        ["App.NavSelected"] = ("#28FFFFFF", "#1F000000"),

        ["App.Text"] = ("#FFFFFFFF", "#FF000000"),
        ["App.TextSecondary"] = ("#B3FFFFFF", "#99000000"),
        ["App.TextTertiary"] = ("#78FFFFFF", "#66000000"),

        ["App.ControlFill"] = ("#28FFFFFF", "#14000000"),
        ["App.ControlFillHover"] = ("#38FFFFFF", "#22000000"),
        ["App.ControlStroke"] = ("#38FFFFFF", "#33000000"),
        ["App.ControlStrokeHover"] = ("#88FFFFFF", "#7A000000"),
        ["App.InputBackground"] = ("#CC121212", "#FFFFFFFF"),
        ["App.InputStroke"] = ("#66FFFFFF", "#66000000"),
        ["App.HoverOverlay"] = ("#FFFFFFFF", "#FF000000"),

        ["App.Success"] = ("#FF10893E", "#FF107C10"),
        ["App.SuccessText"] = ("#FF6CCB5F", "#FF107C10"),
        ["App.SuccessSoft"] = ("#2610893E", "#1E107C10"),
        ["App.Warning"] = ("#FFFFB900", "#FF9D5D00"),
        ["App.WarningSoft"] = ("#26FFB900", "#20FFB900"),
        ["App.Danger"] = ("#FFFF6B6B", "#FFD13438"),
        ["App.DangerFill"] = ("#FFE81123", "#FFD13438"),
        ["App.DangerSoft"] = ("#26E81123", "#1ED13438")
    };

    public static void Initialize(ThemeMode mode)
    {
        SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Mode == ThemeMode.System) Apply(ThemeMode.System);
                else Changed?.Invoke();
            }));
        };

        Apply(mode);
    }

    public static void Apply(ThemeMode mode)
    {
        Mode = mode;

        IsLight = mode switch
        {
            ThemeMode.Light => true,
            ThemeMode.Dark => false,
            _ => IsSystemLight()
        };

        ApplyPalette(IsLight);
        Changed?.Invoke();
    }

    private static bool IsSystemLight()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                null);
            return value is int i && i != 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyPalette(bool light)
    {
        var tokens = FindTokens();
        if (tokens == null) return;

        foreach (var (key, colors) in Palette)
        {
            var hex = light ? colors.Light : colors.Dark;
            tokens[key] = Freeze(new SolidColorBrush(Parse(hex)));
        }

        var accent = SystemAccent();
        var onAccent = Luminance(accent) > 0.62 ? Parse("#FF000000") : Parse("#FFFFFFFF");
        var accentText = light ? Darken(accent, 0.78) : Lighten(accent, 0.38);

        var fill = Luminance(accent) > 0.68 ? Darken(accent, 0.65) : accent;
        if (light && Luminance(fill) > 0.55) fill = Darken(fill, 0.88);

        tokens["App.AccentColor"] = accent;
        tokens["App.Accent"] = Freeze(new SolidColorBrush(fill));
        tokens["App.AccentHover"] = Freeze(new SolidColorBrush(Lighten(fill, 0.14)));
        tokens["App.AccentPressed"] = Freeze(new SolidColorBrush(Darken(fill, 0.80)));
        tokens["App.AccentText"] = Freeze(new SolidColorBrush(accentText));
        tokens["App.AccentSoft"] = Freeze(new SolidColorBrush(Color.FromArgb(light ? (byte)0x1E : (byte)0x2B, fill.R, fill.G, fill.B)));
        tokens["App.OnAccent"] = Freeze(new SolidColorBrush(onAccent));
        tokens["App.WindowBorder"] = Freeze(new SolidColorBrush(Color.FromArgb(0xB8, fill.R, fill.G, fill.B)));

        var hero = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        hero.GradientStops.Add(new GradientStop(fill, 0));
        hero.GradientStops.Add(new GradientStop(Darken(fill, 0.72), 0.6));
        hero.GradientStops.Add(new GradientStop(Darken(fill, 0.45), 1));
        hero.Freeze();
        tokens["App.HeroGradient"] = hero;
    }

    private static ResourceDictionary? FindTokens()
    {
        if (_tokens != null) return _tokens;

        var app = Application.Current;
        if (app == null) return null;

        foreach (var dictionary in app.Resources.MergedDictionaries)
        {
            if (dictionary.Source != null &&
                dictionary.Source.OriginalString.EndsWith("Tokens.xaml", StringComparison.OrdinalIgnoreCase))
            {
                _tokens = dictionary;
                return _tokens;
            }
        }

        return null;
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Color SystemAccent()
    {
        try
        {
            var opacity = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "ColorizationColor", null);

            if (opacity is int value)
            {
                var argb = unchecked((uint)value);
                var r = (byte)((argb >> 16) & 0xFF);
                var g = (byte)((argb >> 8) & 0xFF);
                var b = (byte)(argb & 0xFF);
                var candidate = Color.FromRgb(r, g, b);

                if (Saturation(candidate) >= 0.12 && r + g + b > 30)
                {
                    return candidate;
                }
            }
        }
        catch { }

        // Azul classico do Windows 10 (#0078D7)
        return Color.FromRgb(0x00, 0x78, 0xD7);
    }

    private static double Saturation(Color c)
    {
        var max = Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0;
        var min = Math.Min(c.R, Math.Min(c.G, c.B)) / 255.0;
        return max <= 0 ? 0 : (max - min) / max;
    }

    private static double Luminance(Color c)
        => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static Color Lighten(Color c, double amount)
    {
        byte Mix(byte channel) => (byte)Math.Clamp(channel + (255 - channel) * amount, 0, 255);
        return Color.FromRgb(Mix(c.R), Mix(c.G), Mix(c.B));
    }

    private static Color Darken(Color c, double factor)
    {
        byte Mix(byte channel) => (byte)Math.Clamp(channel * factor, 0, 255);
        return Color.FromRgb(Mix(c.R), Mix(c.G), Mix(c.B));
    }
}
