using System.IO;
using Microsoft.Win32;

namespace KlyraOptimizer.Core;

public enum ActiveMode
{
    None = 0,
    Standard = 1,
    Medium = 2,
    Advanced = 3
}

/// <summary>Estado do sistema: versao do Windows, modo ativo, winget e protecao do sistema.</summary>
public sealed class SystemInfo
{
    private const string CurrentVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string RestoreKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string KlyraKey = @"SOFTWARE\KlyraOptimizer";

    public int BuildNumber { get; private set; }

    public string BuildRevision { get; private set; } = string.Empty;

    public string ProductName { get; private set; } = "Windows";

    public string DisplayVersion { get; private set; } = string.Empty;

    public string MachineName => Environment.MachineName;

    public string UserName => Environment.UserName;

    public bool IsWindows11 => BuildNumber >= 22000;

    /// <summary>Windows 10 21H2 (build 19044) em diante, incluindo Windows 11.</summary>
    public bool IsSupported => BuildNumber >= 19044;

    public string OsLabel => $"{ProductName} {DisplayVersion}".Trim();

    public string FullVersion => $"build {BuildNumber}.{BuildRevision}";

    public ActiveMode ActiveMode { get; private set; } = ActiveMode.None;

    public string ActiveModeLabel => ActiveMode switch
    {
        ActiveMode.Standard => "Modo Standard",
        ActiveMode.Medium => "Modo Medium",
        ActiveMode.Advanced => "Modo Avançado",
        _ => "Nenhum modo aplicado"
    };

    public bool RestoreProtectionEnabled { get; private set; }

    public bool WingetInstalled { get; private set; }

    public string WingetVersion { get; private set; } = string.Empty;

    public bool IsAdmin => Shell.IsAdmin;

    public void Refresh()
    {
        var build = TweakEngine.ReadString($@"HKLM\{CurrentVersion}", "CurrentBuildNumber");
        BuildNumber = int.TryParse(build, out var parsed) ? parsed : Environment.OSVersion.Version.Build;

        BuildRevision = TweakEngine.ReadString($@"HKLM\{CurrentVersion}", "UBR") ?? string.Empty;

        var product = TweakEngine.ReadString($@"HKLM\{CurrentVersion}", "ProductName") ?? "Windows";
        if (BuildNumber >= 22000)
        {
            product = product.Replace("Windows 10", "Windows 11");
        }

        ProductName = product;
        DisplayVersion = TweakEngine.ReadString($@"HKLM\{CurrentVersion}", "DisplayVersion")
                         ?? TweakEngine.ReadString($@"HKLM\{CurrentVersion}", "ReleaseId")
                         ?? string.Empty;

        var mode = TweakEngine.ReadString($@"HKLM\{KlyraKey}", "ActiveMode");
        ActiveMode = int.TryParse(mode, out var modeValue) && modeValue is >= 1 and <= 3
            ? (ActiveMode)modeValue
            : ActiveMode.None;

        var session = TweakEngine.ReadInt($@"HKLM\{RestoreKey}", "RPSessionInterval");
        var disabled = TweakEngine.ReadInt(@"HKLM\SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore", "DisableSR");
        RestoreProtectionEnabled = (session is > 0) && (disabled is null or 0);

        RefreshWinget();
    }

    public void RefreshWinget()
    {
        var path = FindWinget();
        WingetInstalled = path != null;
        WingetVersion = path == null ? string.Empty : GetWingetVersion(path);
    }

    public static string? FindWinget()
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(local)) return local;

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "winget.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // entrada de PATH invalida
            }
        }

        return null;
    }

    private static string GetWingetVersion(string path)
    {
        try
        {
            var result = Shell.RunCapture(path, "--version", 15000);
            return result.Output.Trim().Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void SetActiveMode(ActiveMode mode)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey($@"{KlyraKey}", writable: true);
            key?.SetValue("ActiveMode", ((int)mode).ToString(), RegistryValueKind.String);
            ActiveMode = mode;
        }
        catch
        {
            // sem permissao: mantem o estado apenas em memoria
        }
    }

    public void ClearActiveMode()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KlyraKey, writable: true);
            key?.DeleteValue("ActiveMode", throwOnMissingValue: false);
        }
        catch
        {
            // ignorado
        }

        ActiveMode = ActiveMode.None;
    }
}
