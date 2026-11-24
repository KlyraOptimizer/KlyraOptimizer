using System.IO;
using KlyraOptimizer.Core;
using Microsoft.Win32;

namespace KlyraOptimizer.Services;

public enum CompactMode
{
    Skip,
    Never,
    Always
}

public enum UpdateChoice
{
    KeepActive,
    Disable
}

public enum StoreChoice
{
    Keep,
    ServicesOnly,
    RemoveApp
}

public sealed class OptimizerOptions
{
    public ActiveMode Mode { get; set; } = ActiveMode.Standard;

    public CompactMode Compact { get; set; } = CompactMode.Skip;

    public UpdateChoice Update { get; set; } = UpdateChoice.KeepActive;

    public StoreChoice Store { get; set; } = StoreChoice.Keep;

    public bool DisableBluetooth { get; set; }

    public bool CreateRestorePoint { get; set; } = true;

    public string ModeName => Mode switch
    {
        ActiveMode.Standard => "Modo Standard (leve e seguro)",
        ActiveMode.Medium => "Modo Medium (debloat, rede e serviços)",
        ActiveMode.Advanced => "Modo Avancado (kernel, GPU/CPU, extremo)",
        _ => "Nenhum"
    };
}

public sealed class OptimizerSummary
{
    public int Applied { get; init; }

    public int Skipped { get; init; }

    public int Failed { get; init; }
}

public enum ToggleId
{
    WindowsUpdate,
    MicrosoftStore,
    Bluetooth,
    GameDvr,
    Copilot,
    Transparency,
    SysMain,
    VbsHvci,
    Hibernation
}

public sealed class ConflictItem
{
    public string Title { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    public bool Detected { get; init; }
}

/// <summary>
/// Todas as camadas de otimizacao do Klyra Optimizer:
/// Modo Standard, Medium e Avancado, escolhas interativas, reversao total,
/// funcoes individuais ao vivo, scanner de conflitos e utilitarios extras.
/// </summary>
public sealed class OptimizationService
{
    private readonly AppLog _log;
    private readonly TweakEngine _engine;
    private readonly SystemInfo _system;
    private readonly GpuMsiService _gpu;

    private const string Mm = @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const string Prefetch = @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters";
    private const string IoSystem = @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\I/O System";
    private const string FileSystem = @"HKLM\SYSTEM\CurrentControlSet\Control\FileSystem";
    private const string Dwm = @"HKLM\SOFTWARE\Microsoft\Windows\DWM";
    private const string GraphicsDrivers = @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string GpuScheduler = @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Scheduler";
    private const string ServicesKey = @"HKLM\SYSTEM\CurrentControlSet\Services";

    public OptimizationService(AppLog log, TweakEngine engine, SystemInfo system, GpuMsiService gpu)
    {
        _log = log;
        _engine = engine;
        _system = system;
        _gpu = gpu;
    }

    public bool Busy { get; private set; }

    // ==================================================================
    // EXECUCAO DOS MODOS
    // ==================================================================
    public OptimizerSummary Apply(OptimizerOptions options, CancellationToken ct)
    {
        Busy = true;
        _engine.Reset();

        try
        {
            _log.Phase("VERIFICAÇÃO DE CONFLITOS");
            foreach (var item in ScanConflicts())
            {
                _log.Write(
                    item.Detected
                        ? $"  [CONFLITO] {item.Title} — {item.Detail}"
                        : $"  [OK] {item.Title} — {item.Detail}",
                    item.Detected ? LogKind.Warning : LogKind.Info);
            }

            ApplyStandard(options, ct);
            if (options.Mode >= ActiveMode.Medium) ApplyMedium(ct);
            if (options.Mode >= ActiveMode.Advanced) ApplyAdvanced(ct);
            ApplyInteractive(options, ct);

            _system.SetActiveMode(options.Mode);

            _log.Phase("OTIMIZAÇÃO CONCLUIDA");
            _log.Write(
                $"   Aplicados: {_engine.Applied}  •  Ja ativos (pulados): {_engine.Skipped}  •  Bloqueios/erros: {_engine.Failed}",
                LogKind.Applied);

            return new OptimizerSummary
            {
                Applied = _engine.Applied,
                Skipped = _engine.Skipped,
                Failed = _engine.Failed
            };
        }
        finally
        {
            Busy = false;
        }
    }

    // ==================================================================
    // FASE 1 - STANDARD
    // ==================================================================
    public void ApplyStandard(OptimizerOptions options, CancellationToken ct)
    {
        _log.Phase("FASE 1 - MODO STANDARD (leve, seguro e limpeza)");

        if (options.Compact != CompactMode.Skip)
        {
            var value = options.Compact == CompactMode.Always ? "always" : "never";
            _engine.RunTool("compact.exe", $"/compactos:{value}", $"Compactacao do sistema: {value}");
        }

        Set(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, "Desativar Telemetria (Policy)");
        Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", 0, "Desativar Telemetria (CurrentVersion)");
        Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, "Desativar ID de Anuncio");
        Set(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, "Desativar Experiencias Personalizadas");
        Set(@"HKCU\SOFTWARE\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0, "Desativar Coleta de Feedback");
        Set(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1, "Desativar Windows Recall / AI");
        Set(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, "Desativar Copilot (HKLM)");
        Set(@"HKCU\Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, "Desativar Copilot (HKCU)");
        Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowCopilotButton", 0, "Ocultar Botao Copilot");
        Set(@"HKCU\SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, "Desativar Sugestões Web no Explorer");
        Set(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0, "Desativar Bing no Menu Iniciar");
        Set(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0, "Desativar Cortana");
        Set(@"HKCU\System\GameConfigStore", "GameDVR_Enabled", 0, "Desativar Gravacao GameDVR");
        Set(@"HKCU\System\GameConfigStore", "GameDVR_FSEBehaviorMode", 2, "Otimizar Fullscreen (FSE)");
        Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, "Desativar Captura em Segundo Plano");
        Set(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0, "Bloquear Policy GameDVR");
        Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "GameModeEnabled", 1, "Ativar Game Mode");
        Set(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 10, "MMCSS SystemResponsiveness (10%)");
        Set(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority", 8, "MMCSS Games GPU Priority (8)");
        Set(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Priority", 6, "MMCSS Games CPU Priority (6)");
        Set(FileSystem, "NtfsDisableLastAccessUpdate", 1, "NTFS Disable Last Access");
        Set(FileSystem, "NtfsMftZoneReservation", 4, "NTFS MFT Zone Reservation (4)");
        Set(FileSystem, "LongPathsEnabled", 1, "Ativar Caminhos Longos (LongPaths)");
        Set(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0, "Desativar Fast Startup");
        _engine.RunTool("powercfg.exe", "-h off", "Desativar Hibernação (libera espaço em disco)");
        SetString(@"HKCU\Control Panel\Desktop", "AutoEndTasks", "1", "Fechar Apps Travados Automaticamente");
        SetString(@"HKCU\Control Panel\Desktop", "MenuShowDelay", "50", "Reduzir Delay de Menus (50 ms)");
        Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", 0, "Mostrar Extensões de Arquivos");

        ct.ThrowIfCancellationRequested();
        CleanTempFiles(silent: true);
    }

    // ==================================================================
    // FASE 2 - MEDIUM
    // ==================================================================
    public void ApplyMedium(CancellationToken ct)
    {
        _log.Phase("FASE 2 - MODO MEDIUM (rede, serviços e bloatware)");

        _engine.RunTool("netsh.exe", "int tcp set global rss=enabled", "TCP RSS habilitado");
        _engine.RunTool("netsh.exe", "int tcp set global timestamps=disabled", "Desativar TCP timestamps");
        _engine.RunTool("netsh.exe", "int tcp set global nonsackrttresiliency=disabled", "Desativar NonSACK RTT resiliency");
        _engine.RunTool("netsh.exe", "interface isatap set state disabled", "Desativar tunel ISATAP");

        foreach (var service in new[]
                 {
                     "DiagTrack", "dmwappushservice", "WerSvc", "PcaSvc", "SysMain", "RemoteRegistry",
                     "MapsBroker", "RetailDemo", "WMPNetworkSvc", "wisvc", "Fax", "PhoneSvc", "TapiSrv",
                     "AJRouter", "WpcMonSvc", "edgeupdate", "edgeupdatem", "NvTelemetryContainer"
                 })
        {
            _engine.SetServiceStart(service, 4);
        }

        ct.ThrowIfCancellationRequested();

        _log.Write("   [*] Removendo apps pre-instalados desnecessarios (AppX)...", LogKind.Info);
        const string debloat = @"
$apps = @('Microsoft.BingNews','Microsoft.BingWeather','Microsoft.GetHelp','Microsoft.Getstarted',
          'Microsoft.MicrosoftSolitaireCollection','Microsoft.MicrosoftOfficeHub','Microsoft.People',
          'Microsoft.PowerAutomateDesktop','Microsoft.WindowsFeedbackHub','Microsoft.WindowsMaps',
          'Microsoft.SkypeApp','Clipchamp.Clipchamp','Microsoft.Copilot');
$n = 0;
foreach ($a in $apps) {
    $p = Get-AppxPackage -Name $a -ErrorAction SilentlyContinue;
    if ($p) { $p | Remove-AppxPackage -ErrorAction SilentlyContinue; $n++ }
}
Write-Output ('   Pacotes AppX removidos: ' + $n)";

        Shell.RunPowerShellAsync(debloat, line => _log.Write(line, LogKind.Info)).GetAwaiter().GetResult();
        _engine.MarkApplied("Debloat AppX executado");

        Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, "Desativar Transparência Visual");
        Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", 0, "Desativar Animações da Barra de Tarefas");
        Set(@"HKLM\SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", 0, "Bloquear Edge em Segundo Plano");
        Set(@"HKLM\SOFTWARE\Policies\Google\Chrome", "BackgroundModeEnabled", 0, "Bloquear Chrome em Segundo Plano");

        foreach (var process in new[] { "CompatTelRunner.exe", "DeviceCensus.exe", "AggregatorHost.exe" })
        {
            SetString(
                $@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{process}",
                "Debugger",
                "nul",
                $"Bloqueio IFEO {process}");
        }
    }

    // ==================================================================
    // FASE 3 - AVANCADO
    // ==================================================================
    public void ApplyAdvanced(CancellationToken ct)
    {
        _log.Phase("FASE 3 - MODO AVANCADO (kernel, BCD, memória e GPU/CPU)");

        _engine.BcdDelete("useplatformclock", "Timer do sistema (não forcar HPET)");
        _engine.BcdDelete("useplatformtick", "Tick de plataforma");
        _engine.BcdDelete("disabledynamictick", "Dynamic tick");
        _engine.BcdSet("x2apicpolicy", "Enable", "x2APIC");
        _engine.BcdSet("tscsyncpolicy", "Enhanced", "Sincronizacao TSC");

        Set(Mm, "LargeSystemCache", 0, "Memory LargeSystemCache (0)");
        Set(Mm, "DisablePagingExecutive", 1, "Manter Kernel na RAM");
        Set(Mm, "DisableDriverPaging", 1, "Impedir Paginacao de Drivers");
        Set(Mm, "PoolUsageMaximum", 60, "PoolUsageMaximum (60)");
        Set(Prefetch, "EnablePrefetcher", 0, "Desativar Prefetcher");
        Set(Prefetch, "EnableSuperfetch", 0, "Desativar Superfetch");
        Set(IoSystem, "EnableIoRing", 1, "Ativar IoRing I/O");
        Set(IoSystem, "BypassIosEnable", 1, "Ativar BypassIO (DirectStorage)");
        Set(Dwm, "EnableIndependentFlip", 1, "DWM Independent Flip");
        Set(GraphicsDrivers, "HwSchMode", 2, "Hardware Accelerated GPU Scheduling (HAGS)");
        Set(GraphicsDrivers, "TdrDelay", 10, "GPU TDR Delay (10 s)");
        Set(GpuScheduler, "EnableAsyncCompute", 1, "GPU Async Compute");
        Set(@"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 38, "Win32PrioritySeparation (0x26)");
        Set(@"HKLM\SYSTEM\CurrentControlSet\Services\USB", "DisableSelectiveSuspend", 1, "Desativar Suspensao Seletiva USB");
        Set(@"HKLM\SYSTEM\CurrentControlSet\Services\PCI", "EnableMsi", 1, "Ativar MSI no Barramento PCI");
        Set(@"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", 0, "Desativar VBS");
        Set(@"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 0, "Desativar HVCI");

        ct.ThrowIfCancellationRequested();
        _gpu.Apply(_engine);
    }

    // ==================================================================
    // ESCOLHAS INTERATIVAS (Store, Windows Update, Bluetooth)
    // ==================================================================
    public void ApplyInteractive(OptimizerOptions options, CancellationToken ct)
    {
        _log.Phase("ESCOLHAS DO USUARIO (Store, Windows Update e Bluetooth)");

        // Microsoft Store
        switch (options.Store)
        {
            case StoreChoice.RemoveApp:
                _engine.SetServiceStart("InstallService", 4);
                _engine.SetServiceStart("PushToInstall", 4);
                Set(@"HKLM\SOFTWARE\Policies\Microsoft\WindowsStore", "RemoveWindowsStore", 1, "Bloquear Microsoft Store Oficial");
                _log.Write("   [*] Removendo o pacote AppX da Microsoft Store Oficial...", LogKind.Info);
                Shell.RunPowerShellAsync(
                    "Get-AppxPackage *WindowsStore* | Remove-AppxPackage -ErrorAction SilentlyContinue; 'store removida'",
                    line => _log.Write($"   {line}", LogKind.Info)).GetAwaiter().GetResult();
                break;

            case StoreChoice.ServicesOnly:
                _engine.SetServiceStart("InstallService", 4);
                _engine.SetServiceStart("PushToInstall", 4);
                Set(@"HKLM\SOFTWARE\Policies\Microsoft\WindowsStore", "RemoveWindowsStore", 1, "Bloquear Microsoft Store Oficial");
                break;

            default:
                _engine.SetServiceStart("InstallService", 3);
                _engine.SetServiceStart("PushToInstall", 3);
                Set(@"HKLM\SOFTWARE\Policies\Microsoft\WindowsStore", "RemoveWindowsStore", 0, "Liberar Microsoft Store Oficial");
                break;
        }

        // Windows Update
        if (options.Update == UpdateChoice.Disable)
        {
            foreach (var service in new[] { "wuauserv", "usosvc", "bits", "WaaSMedicSvc", "DoSvc" })
            {
                _engine.StopService(service);
                _engine.SetServiceStart(service, 4);
            }

            Set(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", 1, "Desativar Windows Update Automático");
        }
        else
        {
            foreach (var service in new[] { "wuauserv", "usosvc", "bits", "WaaSMedicSvc", "DoSvc" })
            {
                _engine.SetServiceStart(service, 3);
            }

            Set(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 1, "Proteger Drivers no Windows Update");
        }

        // Bluetooth
        if (options.DisableBluetooth)
        {
            foreach (var service in new[] { "bthserv", "BTAGService", "BthAvctpSvc" })
            {
                _engine.StopService(service);
                _engine.SetServiceStart(service, 4);
            }
        }
        else
        {
            _engine.SetServiceStart("bthserv", 2);
            _engine.SetServiceStart("BTAGService", 3);
            _engine.SetServiceStart("BthAvctpSvc", 2);
        }

        ct.ThrowIfCancellationRequested();
    }

    // ==================================================================
    // TROCA DE MODO AO VIVO (Klyra Tool)
    // ==================================================================
    public void SwitchMode(ActiveMode target, CancellationToken ct = default)
    {
        if (_system.ActiveMode == target) return;

        Busy = true;
        _engine.Reset();

        try
        {
            var current = _system.ActiveMode;
            _log.Phase($"TROCANDO MODO: {(current == ActiveMode.None ? "nenhum" : current.ToString())} -> {target}");

            var options = new OptimizerOptions { Mode = target, CreateRestorePoint = false };

            switch (target)
            {
                case ActiveMode.Advanced:
                    ApplyStandard(options, ct);
                    ApplyMedium(ct);
                    ApplyAdvanced(ct);
                    break;

                case ActiveMode.Medium:
                    if (current == ActiveMode.Advanced) RevertAdvanced();
                    ApplyStandard(options, ct);
                    ApplyMedium(ct);
                    break;

                case ActiveMode.Standard:
                    if (current == ActiveMode.Advanced) RevertAdvanced();
                    if (current is ActiveMode.Medium or ActiveMode.Advanced) RevertMedium();
                    ApplyStandard(options, ct);
                    break;
            }

            _system.SetActiveMode(target);
            _log.Write($"   Modo {target} aplicado ao sistema.", LogKind.Applied);
        }
        finally
        {
            Busy = false;
        }
    }

    // ==================================================================
    // REVERSAO
    // ==================================================================
    public void RevertAll()
    {
        Busy = true;
        _engine.Reset();

        try
        {
            _log.Phase("REVERTENDO TUDO PARA O PADRAO DO WINDOWS");
            RevertAdvanced();
            RevertMedium();
            RevertStandard();
            RevertInteractive();
            _system.ClearActiveMode();
            _log.Phase("REVERSAO CONCLUIDA");
            _log.Write("   Reinicie o computador para concluir a reversao.", LogKind.Info);
        }
        finally
        {
            Busy = false;
        }
    }

    public void RevertAdvanced()
    {
        _log.Write("   [*] Revertendo ajustes do Modo Avancado (Kernel, BCD, GPU, VBS)...", LogKind.Info);

        _engine.BcdDelete("x2apicpolicy", "x2APIC");
        _engine.BcdDelete("tscsyncpolicy", "TSC sync");

        Set(Mm, "DisablePagingExecutive", 0, "DisablePagingExecutive padrão");
        Set(Mm, "DisableDriverPaging", 0, "DisableDriverPaging padrão");
        _engine.RemoveValue(Mm, "PoolUsageMaximum", "PoolUsageMaximum removido");
        Set(Prefetch, "EnablePrefetcher", 3, "Prefetcher padrão");
        Set(Prefetch, "EnableSuperfetch", 3, "Superfetch padrão");
        _engine.RemoveValue(IoSystem, "EnableIoRing", "IoRing removido");
        _engine.RemoveValue(IoSystem, "BypassIosEnable", "BypassIO removido");
        _engine.RemoveValue(Dwm, "EnableIndependentFlip", "Independent Flip removido");
        _engine.RemoveValue(GraphicsDrivers, "HwSchMode", "HAGS removido");
        _engine.RemoveValue(GraphicsDrivers, "TdrDelay", "TDR Delay removido");
        _engine.RemoveValue(GpuScheduler, "EnableAsyncCompute", "Async Compute removido");
        Set(@"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 2, "Win32PrioritySeparation padrão");
        Set(@"HKLM\SYSTEM\CurrentControlSet\Services\USB", "DisableSelectiveSuspend", 0, "Suspensao seletiva USB padrão");
        _engine.RemoveValue(@"HKLM\SYSTEM\CurrentControlSet\Services\PCI", "EnableMsi", "MSI do PCI removido");
        Set(@"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", 1, "VBS reativado");
        Set(@"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 1, "HVCI reativado");

        _gpu.Revert(_engine);
    }

    public void RevertMedium()
    {
        _log.Write("   [*] Revertendo ajustes do Modo Medium (serviços, transparência, IFEO)...", LogKind.Info);

        foreach (var service in new[] { "DiagTrack", "SysMain", "PcaSvc", "MapsBroker", "edgeupdate", "NvTelemetryContainer" })
        {
            _engine.SetServiceStart(service, 2);
        }

        foreach (var service in new[]
                 {
                     "WerSvc", "dmwappushservice", "RetailDemo", "WMPNetworkSvc", "wisvc", "Fax", "PhoneSvc",
                     "TapiSrv", "AJRouter", "WpcMonSvc", "edgeupdatem"
                 })
        {
            _engine.SetServiceStart(service, 3);
        }

        Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1, "Transparência reativada");
        Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", 1, "Animações da barra reativadas");
        _engine.RemoveValue(@"HKLM\SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", "Edge em segundo plano liberado");
        _engine.RemoveValue(@"HKLM\SOFTWARE\Policies\Google\Chrome", "BackgroundModeEnabled", "Chrome em segundo plano liberado");

        foreach (var process in new[] { "CompatTelRunner.exe", "DeviceCensus.exe", "AggregatorHost.exe" })
        {
            _engine.RemoveValue(
                $@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{process}",
                "Debugger",
                $"Bloqueio IFEO {process} removido");
        }
    }

    public void RevertStandard()
    {
        _log.Write("   [*] Revertendo ajustes do Modo Standard (telemetria, GameDVR, MMCSS, NTFS)...", LogKind.Info);

        _engine.RemoveValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", "Telemetria (Policy) padrão");
        _engine.RemoveValue(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", "Telemetria (CurrentVersion) padrão");
        Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 1, "ID de anuncio padrão");
        _engine.RemoveValue(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", "Experiencias personalizadas padrão");
        _engine.RemoveValue(@"HKCU\SOFTWARE\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", "Coleta de feedback padrão");
        _engine.RemoveValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", "Windows AI padrão");
        _engine.RemoveValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", "Copilot HKLM padrão");
        _engine.RemoveValue(@"HKCU\Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", "Copilot HKCU padrão");
        Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowCopilotButton", 1, "Botao Copilot padrão");
        _engine.RemoveValue(@"HKCU\SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", "Sugestões web do Explorer padrão");
        Set(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 1, "Bing no menu Iniciar padrão");
        _engine.RemoveValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", "Cortana padrão");
        Set(@"HKCU\System\GameConfigStore", "GameDVR_Enabled", 1, "GameDVR reativado");
        Set(@"HKCU\System\GameConfigStore", "GameDVR_FSEBehaviorMode", 0, "FSE padrão");
        Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 1, "Captura em segundo plano padrão");
        _engine.RemoveValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", "Policy GameDVR padrão");
        Set(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 20, "MMCSS padrão (20%)");
        Set(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Priority", 2, "MMCSS Games Priority padrão");
        _engine.RunTool("fsutil.exe", "behavior set disablelastaccess 2", "NTFS last access padrão", countAsApplied: false);
        _engine.RemoveValue(FileSystem, "NtfsMftZoneReservation", "MFT Zone padrão");
        Set(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 1, "Fast Startup reativado");
        _engine.RunTool("powercfg.exe", "-h on", "Hibernação reativada", countAsApplied: false);
        SetString(@"HKCU\Control Panel\Desktop", "AutoEndTasks", "0", "AutoEndTasks padrão");
        SetString(@"HKCU\Control Panel\Desktop", "MenuShowDelay", "400", "Delay de menus padrão");
    }

    public void RevertInteractive()
    {
        _log.Write("   [*] Revertendo Windows Update, Microsoft Store e Bluetooth...", LogKind.Info);

        foreach (var service in new[] { "wuauserv", "usosvc", "bits", "WaaSMedicSvc", "InstallService", "PushToInstall", "BTAGService" })
        {
            _engine.SetServiceStart(service, 3);
        }

        _engine.SetServiceStart("DoSvc", 2);
        _engine.SetServiceStart("bthserv", 2);
        _engine.SetServiceStart("BthAvctpSvc", 2);

        _engine.RemoveValue(@"HKLM\SOFTWARE\Policies\Microsoft\WindowsStore", "RemoveWindowsStore", "Microsoft Store liberada");
        _engine.RemoveValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", "Windows Update padrão");
        _engine.RemoveValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", "Drivers no WU padrão");

        var storePresent = Shell.RunPowerShellCapture(
            "if (Get-AppxPackage -Name Microsoft.WindowsStore -ErrorAction SilentlyContinue) { 'sim' } else { 'não' }",
            40000).Contains("sim", StringComparison.OrdinalIgnoreCase);

        if (!storePresent)
        {
            _log.Write("   [*] Microsoft Store não encontrada - reinstalando (wsreset -i)...", LogKind.Info);
            Shell.Open("wsreset.exe");
        }
    }

    // ==================================================================
    // FUNCOES INDIVIDUAIS AO VIVO (Klyra Tool)
    // ==================================================================
    public void SetToggle(ToggleId id, bool optimized)
    {
        switch (id)
        {
            case ToggleId.WindowsUpdate:
                if (optimized)
                {
                    foreach (var service in new[] { "wuauserv", "usosvc", "bits", "WaaSMedicSvc", "DoSvc" })
                    {
                        _engine.StopService(service);
                        _engine.SetServiceStart(service, 4);
                    }

                    Set(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", 1, "Windows Update desativado");
                }
                else
                {
                    foreach (var service in new[] { "wuauserv", "usosvc", "bits", "WaaSMedicSvc", "DoSvc" })
                    {
                        _engine.SetServiceStart(service, 3);
                    }

                    _engine.RemoveValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", "Windows Update reativado");
                    Set(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 1, "Proteção de drivers no WU");
                }

                break;

            case ToggleId.MicrosoftStore:
                if (optimized)
                {
                    _engine.SetServiceStart("InstallService", 4);
                    _engine.SetServiceStart("PushToInstall", 4);
                    Set(@"HKLM\SOFTWARE\Policies\Microsoft\WindowsStore", "RemoveWindowsStore", 1, "Microsoft Store Oficial bloqueada");
                }
                else
                {
                    _engine.SetServiceStart("InstallService", 3);
                    _engine.SetServiceStart("PushToInstall", 3);
                    _engine.RemoveValue(@"HKLM\SOFTWARE\Policies\Microsoft\WindowsStore", "RemoveWindowsStore", "Microsoft Store Oficial liberada");
                    Shell.Open("wsreset.exe");
                }

                break;

            case ToggleId.Bluetooth:
                if (optimized)
                {
                    foreach (var service in new[] { "bthserv", "BTAGService", "BthAvctpSvc" })
                    {
                        _engine.StopService(service);
                        _engine.SetServiceStart(service, 4);
                    }
                }
                else
                {
                    _engine.SetServiceStart("bthserv", 2);
                    _engine.SetServiceStart("BTAGService", 3);
                    _engine.SetServiceStart("BthAvctpSvc", 2);
                }

                break;

            case ToggleId.GameDvr:
                if (optimized)
                {
                    Set(@"HKCU\System\GameConfigStore", "GameDVR_Enabled", 0, "GameDVR desativado");
                    Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, "Captura desativada");
                    Set(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0, "Policy GameDVR bloqueada");
                }
                else
                {
                    Set(@"HKCU\System\GameConfigStore", "GameDVR_Enabled", 1, "GameDVR reativado");
                    Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 1, "Captura reativada");
                    _engine.RemoveValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", "Policy GameDVR padrão");
                }

                break;

            case ToggleId.Copilot:
                if (optimized)
                {
                    Set(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, "Copilot desativado (HKLM)");
                    Set(@"HKCU\Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, "Copilot desativado (HKCU)");
                    Set(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0, "Bing desativado");
                }
                else
                {
                    Set(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 0, "Copilot reativado (HKLM)");
                    Set(@"HKCU\Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 0, "Copilot reativado (HKCU)");
                    Set(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 1, "Bing reativado");
                }

                break;

            case ToggleId.Transparency:
                if (optimized)
                {
                    Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, "Transparência desativada");
                    Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", 0, "Animações da barra desativadas");
                }
                else
                {
                    Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1, "Transparência reativada");
                    Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", 1, "Animações da barra reativadas");
                }

                break;

            case ToggleId.SysMain:
                if (optimized)
                {
                    _engine.StopService("SysMain");
                    _engine.SetServiceStart("SysMain", 4);
                    Set(Prefetch, "EnableSuperfetch", 0, "Superfetch desativado");
                }
                else
                {
                    _engine.SetServiceStart("SysMain", 2);
                    _engine.RunTool("sc.exe", "start SysMain", "SysMain iniciado", countAsApplied: false);
                    Set(Prefetch, "EnableSuperfetch", 3, "Superfetch reativado");
                }

                break;

            case ToggleId.VbsHvci:
                if (optimized)
                {
                    Set(@"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", 0, "VBS desativado (requer reinicio)");
                    Set(@"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 0, "HVCI desativado (requer reinicio)");
                }
                else
                {
                    Set(@"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", 1, "VBS reativado (requer reinicio)");
                    Set(@"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 1, "HVCI reativado (requer reinicio)");
                }

                break;

            case ToggleId.Hibernation:
                if (optimized)
                {
                    _engine.RunTool("powercfg.exe", "-h off", "Hibernação desativada", countAsApplied: false);
                    Set(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0, "Fast Startup desativado");
                }
                else
                {
                    _engine.RunTool("powercfg.exe", "-h on", "Hibernação reativada", countAsApplied: false);
                    Set(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 1, "Fast Startup reativado");
                }

                break;
        }
    }

    /// <summary>Le o estado atual de cada funcao individual (true = otimizada/desativada).</summary>
    public bool IsOptimized(ToggleId id)
    {
        return id switch
        {
            ToggleId.WindowsUpdate => TweakEngine.ReadInt($@"{ServicesKey}\wuauserv", "Start") == 4,
            ToggleId.MicrosoftStore => TweakEngine.ReadInt($@"{ServicesKey}\InstallService", "Start") == 4,
            ToggleId.Bluetooth => TweakEngine.ReadInt($@"{ServicesKey}\bthserv", "Start") == 4,
            ToggleId.GameDvr => TweakEngine.ReadInt(@"HKCU\System\GameConfigStore", "GameDVR_Enabled") == 0,
            ToggleId.Copilot => TweakEngine.ReadInt(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot") == 1,
            ToggleId.Transparency => TweakEngine.ReadInt(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency") == 0,
            ToggleId.SysMain => TweakEngine.ReadInt($@"{ServicesKey}\SysMain", "Start") == 4,
            ToggleId.VbsHvci => TweakEngine.ReadInt(@"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity") == 0,
            ToggleId.Hibernation => !File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "hiberfil.sys")),
            _ => false
        };
    }

    // ==================================================================
    // SCANNER DE CONFLITOS
    // ==================================================================
    public List<ConflictItem> ScanConflicts()
    {
        var items = new List<ConflictItem>();

        var ifeo = TweakEngine.ReadValue(
            @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\CompatTelRunner.exe",
            "Debugger") != null;

        items.Add(new ConflictItem
        {
            Title = "Bloqueio IFEO no CompatTelRunner",
            Detail = ifeo ? "outro script ja alterou a telemetria" : "padrão do Windows",
            Detected = ifeo
        });

        var diagTrack = TweakEngine.ReadInt($@"{ServicesKey}\DiagTrack", "Start");
        items.Add(new ConflictItem
        {
            Title = "Serviço DiagTrack",
            Detail = diagTrack == 4 ? "ja desativado no registro" : "ativo / padrão",
            Detected = diagTrack == 4
        });

        var priority = TweakEngine.ReadInt(@"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation");
        items.Add(new ConflictItem
        {
            Title = "Win32PrioritySeparation",
            Detail = priority == null || priority == 2
                ? "padrão do Windows (0x2)"
                : $"personalizado (valor atual 0x{priority:X})",
            Detected = priority != null && priority != 2
        });

        var hags = TweakEngine.ReadInt(GraphicsDrivers, "HwSchMode");
        items.Add(new ConflictItem
        {
            Title = "HAGS (Hardware GPU Scheduling)",
            Detail = hags == 2 ? "ja ativado (0x2)" : "desativado / padrão",
            Detected = hags == 2
        });

        return items;
    }

    // ==================================================================
    // UTILITARIOS EXTRAS
    // ==================================================================
    public void CleanMemory()
    {
        _log.Phase("LIMPEZA DE MEMÓRIA E ARQUIVOS TEMPORARIOS");
        Shell.RunPowerShellAsync(@"
$n = 0;
Get-Process | ForEach-Object { try { $_.MaxWorkingSet = $_.MaxWorkingSet; $n++ } catch {} };
Write-Output ('   Processos ajustados: ' + $n)", line => _log.Write(line, LogKind.Info)).GetAwaiter().GetResult();

        CleanTempFiles(silent: false);
        _log.Write("   Limpeza rapida de RAM e Temp concluida.", LogKind.Applied);
    }

    public void ResetNetwork()
    {
        _log.Phase("RESET DE REDE E CACHE DNS");
        Shell.Run("ipconfig.exe", "/flushdns", 30000);
        Shell.Run("netsh.exe", "winsock reset", 60000);
        Shell.Run("netsh.exe", "int ip reset", 60000);
        _log.Write("   Rede e cache DNS resetados. Reinicie para concluir.", LogKind.Applied);
    }

    public void ReregisterAppx()
    {
        _log.Phase("RE-REGISTRANDO PACOTES NATIVOS DO WINDOWS");
        Shell.RunPowerShellAsync(@"
Get-AppxPackage -AllUsers | ForEach-Object {
    Add-AppxPackage -DisableDevelopmentMode -Register ($_.InstallLocation + '\AppXManifest.xml') -ErrorAction SilentlyContinue
}
Write-Output '   Pacotes nativos re-registrados.'", line => _log.Write(line, LogKind.Info)).GetAwaiter().GetResult();
    }

    public void CleanTempFiles(bool silent)
    {
        try
        {
            var temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
            var windowsTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");

            foreach (var dir in new[] { temp, windowsTemp })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(file); } catch { }
                }
            }

            if (!silent)
            {
                _log.Write("   Arquivos temporários removidos.", LogKind.Applied);
            }
        }
        catch
        {
            // limpeza e best-effort
        }
    }

    public void Reboot(int delaySeconds = 5)
    {
        _log.Write($"   Reiniciando o computador em {delaySeconds} segundos...", LogKind.Warning);
        Shell.Run("shutdown.exe", $"/r /f /t {delaySeconds}", 10000);
    }

    // ------------------------------------------------------------------
    private void Set(string key, string name, int value, string description)
        => _engine.SetValue(key, name, RegistryValueKind.DWord, value, description);

    private void SetString(string key, string name, string value, string description)
        => _engine.SetValue(key, name, RegistryValueKind.String, value, description);
}
