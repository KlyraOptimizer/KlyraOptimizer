using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;
using KlyraOptimizer.Core;
using Microsoft.Win32;

namespace KlyraOptimizer.Services;

public sealed class InstalledAppInfo
{
    public bool IsInstalled { get; init; }
    public string? PackageFamilyName { get; init; }
    public string? PackageFullName { get; init; }
    public string? LaunchAumid { get; init; }
    public string? LaunchExecutable { get; init; }
    public string? UninstallCommand { get; init; }
    public bool MatchedByWinget { get; init; }
}

/// <summary>
/// Instalacao, verificacao silenciosa de aplicativos instalados, abertura e
/// desinstalacao via winget / Appx / Registro em segundo plano.
/// </summary>
public sealed class WingetService
{
    private sealed record AppxEntry(
        string Name,
        string PackageFamilyName,
        string PackageFullName,
        string InstallLocation);

    private sealed record StartAppEntry(string Name, string AppId);

    private sealed record Win32Entry(
        string DisplayName,
        string? UninstallString,
        string? QuietUninstallString,
        string? DisplayIcon,
        string? InstallLocation);

    private sealed class InstalledSnapshot
    {
        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
        public List<AppxEntry> Appx { get; } = new();
        public List<StartAppEntry> StartApps { get; } = new();
        public List<Win32Entry> Win32 { get; } = new();
    }

    private readonly AppLog _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);
    private InstalledSnapshot? _snapshot;

    private static readonly (string Name, string Url)[] Prerequisites =
    {
        ("Microsoft.VCLibs.140.00.UWPDesktop", "https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx"),
        ("Microsoft.UI.Xaml.2.8", "https://aka.ms/Microsoft.UI.Xaml.2.8.appx"),
        ("App Installer (winget)", "https://aka.ms/getwinget")
    };

    public WingetService(AppLog log) => _log = log;

    public static bool IsInstalled => SystemInfo.FindWinget() != null;

    public event Action<string>? Progress;

    private void RaiseProgress(string message)
    {
        var handler = Progress;
        if (handler == null) return;
        Ui.Post(() => handler(message));
    }

    public void InvalidateInstalledCache() => _snapshot = null;

    public Task WarmInstalledCacheAsync(CancellationToken ct = default)
        => Task.Run(() => GetSnapshotAsync(forceRefresh: false, ct), ct);

    /// <summary>
    /// Verifica silenciosamente em segundo plano se o aplicativo da Loja ja esta instalado
    /// (checando pacotes Appx/MSIX pelo PackageFamilyName, Registro Uninstall e winget list).
    /// </summary>
    public async Task<InstalledAppInfo> CheckInstalledAsync(
        string productId,
        string title,
        string? packageFamilyName,
        CancellationToken ct = default)
    {
        return await Task.Run(async () =>
        {
            var snap = await GetSnapshotAsync(forceRefresh: false, ct).ConfigureAwait(false);

            // 1) Correspondencia exata por PackageFamilyName (pacotes MSIX / UWP da Microsoft Store)
            if (!string.IsNullOrWhiteSpace(packageFamilyName))
            {
                var pfn = packageFamilyName.Trim();
                var identityPrefix = pfn.Contains('_') ? pfn[..pfn.IndexOf('_')] : pfn;

                var appx = snap.Appx.FirstOrDefault(a =>
                    a.PackageFamilyName.Equals(pfn, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(identityPrefix) &&
                     a.Name.Equals(identityPrefix, StringComparison.OrdinalIgnoreCase)));

                if (appx != null)
                {
                    var aumid = FindStartAumidByPfn(snap.StartApps, appx.PackageFamilyName)
                                ?? ReadManifestAumid(appx.InstallLocation, appx.PackageFamilyName);

                    return new InstalledAppInfo
                    {
                        IsInstalled = true,
                        PackageFamilyName = appx.PackageFamilyName,
                        PackageFullName = appx.PackageFullName,
                        LaunchAumid = aumid
                    };
                }
            }

            // 2) Verificacao silenciosa via winget list --id <ProductId> --exact
            var wingetInstalled = false;
            var winget = SystemInfo.FindWinget();
            if (winget != null && !string.IsNullOrWhiteSpace(productId))
            {
                var (code, output) = Shell.RunCapture(
                    winget,
                    $"list --id \"{productId}\" --exact --accept-source-agreements --disable-interactivity",
                    timeoutMs: 12000);

                if (code == 0 &&
                    output.Contains(productId, StringComparison.OrdinalIgnoreCase) &&
                    !output.Contains("No installed package", StringComparison.OrdinalIgnoreCase) &&
                    !output.Contains("Nenhum pacote instalado", StringComparison.OrdinalIgnoreCase))
                {
                    wingetInstalled = true;
                }
            }

            // 3) Correspondencia com aplicativos Win32/Desktop instalados (Registro + Menu Iniciar)
            var cleanTitle = CleanStoreTitle(title);
            var win32 = MatchWin32Installed(snap.Win32, title, cleanTitle);
            var startEntry = MatchStartApp(snap.StartApps, title, cleanTitle, packageFamilyName);

            if (wingetInstalled || win32 != null)
            {
                var exePath = ExtractExeFromWin32(win32);
                return new InstalledAppInfo
                {
                    IsInstalled = true,
                    PackageFamilyName = packageFamilyName,
                    LaunchAumid = startEntry?.AppId,
                    LaunchExecutable = exePath,
                    UninstallCommand = win32?.QuietUninstallString ?? win32?.UninstallString,
                    MatchedByWinget = wingetInstalled
                };
            }

            return new InstalledAppInfo { IsInstalled = false };
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Abre o aplicativo instalado usando shell:AppsFolder (AUMID) ou executavel direto.</summary>
    public async Task<bool> LaunchInstalledAsync(
        string productId,
        string title,
        string? packageFamilyName,
        InstalledAppInfo? currentInfo)
    {
        var info = currentInfo;
        if (info == null || (string.IsNullOrWhiteSpace(info.LaunchAumid) && string.IsNullOrWhiteSpace(info.LaunchExecutable)))
        {
            InvalidateInstalledCache();
            info = await CheckInstalledAsync(productId, title, packageFamilyName).ConfigureAwait(false);
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(info.LaunchAumid))
            {
                var appId = info.LaunchAumid!;
                if (File.Exists(appId))
                {
                    Process.Start(new ProcessStartInfo { FileName = appId, UseShellExecute = true });
                    return true;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"shell:AppsFolder\\{appId}\"",
                    UseShellExecute = true
                });
                return true;
            }

            if (!string.IsNullOrWhiteSpace(info.LaunchExecutable) && File.Exists(info.LaunchExecutable))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = info.LaunchExecutable,
                    UseShellExecute = true
                });
                return true;
            }
        }
        catch (Exception ex)
        {
            _log.Write($"   [AVISO] Falha ao abrir {title}: {ex.Message}", LogKind.Warning);
        }

        return false;
    }

    /// <summary>Desinstala o aplicativo em segundo plano via winget, Appx ou desinstalador registrado.</summary>
    public async Task<bool> UninstallAsync(
        string productId,
        string title,
        InstalledAppInfo? info,
        CancellationToken ct)
    {
        var winget = SystemInfo.FindWinget();
        var success = false;

        if (winget != null && !string.IsNullOrWhiteSpace(productId))
        {
            var args = $"uninstall --id \"{productId}\" --exact --silent --accept-source-agreements --disable-interactivity";
            _log.Write($"   [COMANDO] winget {args}", LogKind.Command);

            var code = await Shell.RunAsync(
                winget,
                args,
                line =>
                {
                    var text = line.Trim();
                    if (text.Length > 0) _log.Write($"   {text}", LogKind.Info);
                },
                ct).ConfigureAwait(false);

            success = code == 0;
        }

        // Fallback 1: Pacote MSIX/Appx via Remove-AppxPackage
        if (!success && !string.IsNullOrWhiteSpace(info?.PackageFullName))
        {
            var safePkg = info.PackageFullName.Replace("'", "''");
            _log.Write($"   [COMANDO] Remove-AppxPackage '{safePkg}'", LogKind.Command);

            var psCode = await Shell.RunPowerShellAsync(
                $"Remove-AppxPackage -Package '{safePkg}' -ErrorAction Stop",
                line =>
                {
                    var text = line.Trim();
                    if (text.Length > 0) _log.Write($"   {text}", LogKind.Info);
                },
                ct).ConfigureAwait(false);

            success = psCode == 0;
        }

        // Fallback 2: Comando de desinstalacao registrado no Windows (Win32)
        if (!success && !string.IsNullOrWhiteSpace(info?.UninstallCommand))
        {
            try
            {
                var (exe, cmdArgs) = SplitCommandLine(info.UninstallCommand!);
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    _log.Write($"   [COMANDO] Desinstalador Win32 para {title}", LogKind.Command);
                    using var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = cmdArgs,
                        UseShellExecute = true
                    });
                    if (proc != null)
                    {
                        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                        success = proc.ExitCode == 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Write($"   [AVISO] Desinstalador Win32 falhou: {ex.Message}", LogKind.Warning);
            }
        }

        InvalidateInstalledCache();
        return success;
    }

    public async Task<bool> InstallFromStoreAsync(string productId, CancellationToken ct)
        => await InstallAsync(productId, "msstore", ct).ConfigureAwait(false);

    public async Task<bool> InstallAsync(string id, string? source, CancellationToken ct)
    {
        var winget = SystemInfo.FindWinget();
        if (winget == null)
        {
            _log.Write("   [ERRO] winget não esta instalado. Use o botao 'Instalar winget'.", LogKind.Failed);
            return false;
        }

        var arguments = $"install --id \"{id}\" --accept-package-agreements --accept-source-agreements";
        if (!string.IsNullOrWhiteSpace(source))
        {
            arguments += $" --source {source}";
        }

        _log.Write($"   [COMANDO] winget {arguments}", LogKind.Command);

        var alreadyInstalled = false;
        var code = await Shell.RunAsync(
            winget,
            arguments,
            line =>
            {
                var text = line.Trim();
                if (text.Length == 0) return;
                if (text.Contains("already installed", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("ja instalado", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("já instalado", StringComparison.OrdinalIgnoreCase))
                {
                    alreadyInstalled = true;
                }

                _log.Write($"   {text}", LogKind.Info);
            },
            ct).ConfigureAwait(false);

        var success = code == 0 || alreadyInstalled;
        InvalidateInstalledCache();

        if (success)
        {
            _log.Write($"   [OK] {id} instalado com sucesso via winget ({source ?? "winget"}).", LogKind.Applied);
        }
        else
        {
            _log.Write($"   [ERRO] winget retornou o código {code} para {id}.", LogKind.Failed);
        }

        return success;
    }

    /// <summary>Garante o winget: baixa e instala o App Installer + dependencias.</summary>
    public async Task<bool> EnsureAsync(CancellationToken ct)
    {
        if (IsInstalled)
        {
            RaiseProgress("winget ja esta instalado.");
            return true;
        }

        var folder = Path.Combine(Path.GetTempPath(), "KlyraOptimizer-prereq");
        Directory.CreateDirectory(folder);

        foreach (var (name, url) in Prerequisites)
        {
            ct.ThrowIfCancellationRequested();

            var extension = url.EndsWith(".appx", StringComparison.OrdinalIgnoreCase) ? ".appx" : ".msixbundle";
            var file = Path.Combine(folder, name.Replace(' ', '_').Replace("(", "").Replace(")", "") + extension);

            try
            {
                RaiseProgress($"Baixando {name}...");
                await DownloadAsync(url, file, ct).ConfigureAwait(false);

                RaiseProgress($"Instalando {name}...");
                await Shell.RunPowerShellAsync(
                    $"Add-AppxPackage -Path '{file}' -ForceApplicationShutdown -ErrorAction Stop; 'instalado'",
                    line => _log.Write($"   {line}", LogKind.Info),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Write($"   [AVISO] {name}: {ex.Message}", LogKind.Warning);
            }
        }

        if (IsInstalled)
        {
            _log.Write("   [OK] winget (App Installer) instalado com sucesso.", LogKind.Applied);
            RaiseProgress("winget instalado com sucesso.");
            return true;
        }

        _log.Write(
            "   [ERRO] Não foi possível instalar o winget automaticamente. Instale o 'App Installer' pela Microsoft Store.",
            LogKind.Failed);
        RaiseProgress("Falha ao instalar o winget.");
        return false;
    }

    private async Task DownloadAsync(string url, string destination, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var remote = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var local = File.Create(destination);
        await remote.CopyToAsync(local, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Indexacao em segundo plano de Appx + StartApps + Registro Win32
    // ------------------------------------------------------------------
    private async Task<InstalledSnapshot> GetSnapshotAsync(bool forceRefresh, CancellationToken ct)
    {
        var current = _snapshot;
        if (!forceRefresh && current != null && (DateTime.UtcNow - current.CreatedUtc).TotalSeconds < 45)
        {
            return current;
        }

        await _snapshotLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            current = _snapshot;
            if (!forceRefresh && current != null && (DateTime.UtcNow - current.CreatedUtc).TotalSeconds < 45)
            {
                return current;
            }

            var built = BuildSnapshot();
            _snapshot = built;
            return built;
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    private InstalledSnapshot BuildSnapshot()
    {
        var snap = new InstalledSnapshot();

        // 1) Registro Win32 (instantaneo em memoria < 5ms, zero processo externo)
        ReadWin32Registry(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", snap.Win32);
        ReadWin32Registry(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", snap.Win32);
        ReadWin32Registry(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", snap.Win32);

        // 2) AppxPackage + StartApps em uma unica consulta PowerShell silenciosa
        try
        {
            const string script =
                "$a = @(Get-AppxPackage -ErrorAction SilentlyContinue | Select-Object Name,PackageFamilyName,PackageFullName,InstallLocation);" +
                "$s = @(Get-StartApps -ErrorAction SilentlyContinue | Select-Object Name,AppID);" +
                "@{ Appx = $a; Start = $s } | ConvertTo-Json -Compress -Depth 3";

            var json = Shell.RunPowerShellCapture(script, timeoutMs: 15000);
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("Appx", out var appxArr) && appxArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in appxArr.EnumerateArray())
                    {
                        var name = GetProp(item, "Name");
                        var pfn = GetProp(item, "PackageFamilyName");
                        var full = GetProp(item, "PackageFullName");
                        var loc = GetProp(item, "InstallLocation");
                        if (!string.IsNullOrWhiteSpace(pfn))
                        {
                            snap.Appx.Add(new AppxEntry(name ?? string.Empty, pfn!, full ?? string.Empty, loc ?? string.Empty));
                        }
                    }
                }

                if (root.TryGetProperty("Start", out var startArr) && startArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in startArr.EnumerateArray())
                    {
                        var name = GetProp(item, "Name");
                        var appId = GetProp(item, "AppID");
                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(appId))
                        {
                            snap.StartApps.Add(new StartAppEntry(name!, appId!));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Write($"   [AVISO] Snapshot de apps instalados: {ex.Message}", LogKind.Warning);
        }

        return snap;
    }

    private static void ReadWin32Registry(RegistryKey hive, string subKeyPath, List<Win32Entry> list)
    {
        try
        {
            using var root = hive.OpenSubKey(subKeyPath);
            if (root == null) return;

            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var sub = root.OpenSubKey(name);
                    if (sub == null) continue;
                    if (sub.GetValue("SystemComponent") is int sys && sys == 1) continue;

                    var displayName = sub.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName)) continue;

                    list.Add(new Win32Entry(
                        displayName.Trim(),
                        sub.GetValue("UninstallString") as string,
                        sub.GetValue("QuietUninstallString") as string,
                        sub.GetValue("DisplayIcon") as string,
                        sub.GetValue("InstallLocation") as string));
                }
                catch { }
            }
        }
        catch { }
    }

    private static string? GetProp(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? FindStartAumidByPfn(List<StartAppEntry> startApps, string pfn)
    {
        var prefix = pfn + "!";
        return startApps.FirstOrDefault(s =>
            s.AppId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            s.AppId.Equals(pfn, StringComparison.OrdinalIgnoreCase))?.AppId;
    }

    private static string? ReadManifestAumid(string installLocation, string pfn)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(installLocation)) return null;
            var manifestPath = Path.Combine(installLocation, "AppxManifest.xml");
            if (!File.Exists(manifestPath)) return null;

            var doc = XDocument.Load(manifestPath);
            var appElement = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Application");
            var appId = appElement?.Attribute("Id")?.Value;
            if (!string.IsNullOrWhiteSpace(appId))
            {
                return $"{pfn}!{appId}";
            }
        }
        catch { }

        return null;
    }

    private static string CleanStoreTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var clean = title.Trim();

        foreach (var sep in new[] { " — ", " - ", " – ", ": " })
        {
            var idx = clean.IndexOf(sep, StringComparison.Ordinal);
            if (idx >= 3) clean = clean[..idx].Trim();
        }

        foreach (var suffix in new[] { " for Windows 10", " for Windows", " Desktop", " Official" })
        {
            if (clean.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && clean.Length > suffix.Length + 2)
            {
                clean = clean[..^suffix.Length].Trim();
            }
        }

        return clean;
    }

    private static Win32Entry? MatchWin32Installed(List<Win32Entry> entries, string fullTitle, string cleanTitle)
    {
        if (cleanTitle.Length < 3) return null;

        return entries.FirstOrDefault(e =>
            e.DisplayName.Equals(fullTitle, StringComparison.OrdinalIgnoreCase) ||
            e.DisplayName.Equals(cleanTitle, StringComparison.OrdinalIgnoreCase) ||
            e.DisplayName.StartsWith(cleanTitle + " ", StringComparison.OrdinalIgnoreCase) ||
            (cleanTitle.Equals("Roblox", StringComparison.OrdinalIgnoreCase) &&
             e.DisplayName.StartsWith("Roblox Player", StringComparison.OrdinalIgnoreCase)));
    }

    private static StartAppEntry? MatchStartApp(
        List<StartAppEntry> startApps,
        string fullTitle,
        string cleanTitle,
        string? pfn)
    {
        if (!string.IsNullOrWhiteSpace(pfn))
        {
            var byPfn = FindStartAumidByPfn(startApps, pfn!);
            if (byPfn != null) return new StartAppEntry(fullTitle, byPfn);
        }

        if (cleanTitle.Length < 3) return null;

        return startApps.FirstOrDefault(s =>
                   s.Name.Equals(fullTitle, StringComparison.OrdinalIgnoreCase) ||
                   s.Name.Equals(cleanTitle, StringComparison.OrdinalIgnoreCase))
               ?? startApps.FirstOrDefault(s =>
                   s.Name.StartsWith(cleanTitle + " ", StringComparison.OrdinalIgnoreCase) &&
                   !s.Name.Contains("Uninstall", StringComparison.OrdinalIgnoreCase) &&
                   !s.Name.Contains("Documentation", StringComparison.OrdinalIgnoreCase) &&
                   !s.Name.Contains("FAQ", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExtractExeFromWin32(Win32Entry? entry)
    {
        if (entry == null) return null;

        if (!string.IsNullOrWhiteSpace(entry.DisplayIcon))
        {
            var iconPath = entry.DisplayIcon.Trim().Trim('"');
            var commaIdx = iconPath.IndexOf(',');
            if (commaIdx > 0) iconPath = iconPath[..commaIdx].Trim().Trim('"');
            if (iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                !iconPath.Contains("unins", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(iconPath))
            {
                return iconPath;
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.InstallLocation) && Directory.Exists(entry.InstallLocation))
        {
            try
            {
                var candidate = Directory.EnumerateFiles(entry.InstallLocation, "*.exe", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(f =>
                        !Path.GetFileName(f).Contains("unins", StringComparison.OrdinalIgnoreCase) &&
                        !Path.GetFileName(f).Contains("update", StringComparison.OrdinalIgnoreCase) &&
                        !Path.GetFileName(f).Contains("crash", StringComparison.OrdinalIgnoreCase));
                if (candidate != null) return candidate;
            }
            catch { }
        }

        return null;
    }

    private static (string Exe, string Args) SplitCommandLine(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0) return (string.Empty, string.Empty);

        if (trimmed.StartsWith('"'))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            if (endQuote > 1)
            {
                return (trimmed[1..endQuote], trimmed[(endQuote + 1)..].Trim());
            }
        }

        var space = trimmed.IndexOf(' ');
        return space > 0 ? (trimmed[..space], trimmed[(space + 1)..].Trim()) : (trimmed, string.Empty);
    }
}
