using System.IO;
using KlyraOptimizer.Core;

namespace KlyraOptimizer.Services;

public sealed class RestorePointInfo
{
    public int Sequence { get; init; }

    public string Description { get; init; } = string.Empty;

    public string Created { get; init; } = string.Empty;
}

/// <summary>Ponto de restauracao: verificacao, criacao, listagem e restauracao.</summary>
public sealed class RestoreService
{
    private readonly AppLog _log;
    private readonly SystemInfo _system;

    public RestoreService(AppLog log, SystemInfo system)
    {
        _log = log;
        _system = system;
    }

    public bool ProtectionEnabled => _system.RestoreProtectionEnabled;

    /// <summary>Mesma sequencia do :ENSURE_SYSTEM_RESTORE do script original.</summary>
    public void EnsureProtection()
    {
        _log.Write("   [*] Verificando se a Proteção do Sistema esta ativa...", LogKind.Info);

        Shell.Run("sc.exe", "config swprv start= demand", 20000);
        Shell.Run("sc.exe", "start swprv", 20000);
        Shell.Run("sc.exe", "config VSS start= demand", 20000);
        Shell.Run("sc.exe", "start VSS", 20000);
        Shell.Run("sc.exe", "config srservice start= auto", 20000);
        Shell.Run("sc.exe", "start srservice", 20000);

        using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                   @"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore", writable: true))
        {
            key?.DeleteValue("DisableSR", throwOnMissingValue: false);
            key?.DeleteValue("DisableConfig", throwOnMissingValue: false);
        }

        using (var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                   @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", writable: true))
        {
            key?.SetValue("SystemRestorePointCreationFrequency", 0, Microsoft.Win32.RegistryValueKind.DWord);
        }

        _system.Refresh();

        if (!_system.RestoreProtectionEnabled)
        {
            _log.Write("   [!] Proteção do Sistema estava desativada. Ativando na unidade do sistema...", LogKind.Warning);

            var drive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            Shell.RunPowerShellAsync(
                $"Enable-ComputerRestore -Drive '{drive}' -ErrorAction SilentlyContinue; 'ok'",
                line => _log.Write($"   {line}", LogKind.Info)).GetAwaiter().GetResult();

            Shell.Run("vssadmin.exe", $"resize shadowstorage /for={drive[..2]} /on={drive[..2]} /maxsize=5%", 60000);
            _log.Write($"   [OK] Proteção do Sistema ativada em {drive}.", LogKind.Applied);
        }
        else
        {
            _log.Write("   [OK] Proteção do Sistema ja estava ativa.", LogKind.Applied);
        }
    }

    public async Task<bool> CreatePointAsync(string description, CancellationToken ct = default)
    {
        _log.Write($"   [*] Criando ponto de restauração '{description}' (aguarde)...", LogKind.Info);

        var code = await Shell.RunPowerShellAsync(
            $"Checkpoint-Computer -Description '{description}' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop; 'ok'",
            line => _log.Write($"   {line}", LogKind.Info),
            ct).ConfigureAwait(false);

        _system.Refresh();

        if (code == 0)
        {
            _log.Write("   [OK] Ponto de restauração criado com sucesso.", LogKind.Applied);
            return true;
        }

        _log.Write("   [AVISO] Não foi possível criar um ponto agora (verifique o espaço livre).", LogKind.Warning);
        return false;
    }

    public async Task<List<RestorePointInfo>> GetPointsAsync(CancellationToken ct = default)
    {
        var points = new List<RestorePointInfo>();

        var script = @"
$rp = Get-ComputerRestorePoint -ErrorAction SilentlyContinue;
foreach ($p in $rp) {
    Write-Output ($p.SequenceNumber.ToString() + '|' + $p.Description + '|' + $p.ConvertToDateTime($p.CreationTime).ToString('dd/MM/yyyy HH:mm'))
}";

        var code = await Shell.RunPowerShellAsync(
            script,
            line =>
            {
                var text = line.Trim();
                if (text.Length == 0 || !text.Contains('|')) return;

                var parts = text.Split('|', 3);
                if (parts.Length < 3) return;
                if (!int.TryParse(parts[0], out var sequence)) return;

                points.Add(new RestorePointInfo
                {
                    Sequence = sequence,
                    Description = parts[1],
                    Created = parts[2]
                });
            },
            ct).ConfigureAwait(false);

        if (code != 0)
        {
            _log.Write(
                Shell.IsAdmin
                    ? "   [AVISO] Não foi possível ler a lista de pontos (pode não haver nenhum ponto criado)."
                    : "   [AVISO] Listar pontos de restauração exige privilégios de administrador.",
                LogKind.Warning);
        }

        return points.OrderByDescending(p => p.Sequence).ToList();
    }

    public async Task<bool> RestoreToPointAsync(int sequence, CancellationToken ct = default)
    {
        _log.Write($"   [*] Iniciando a Restauração do Sistema para o ponto #{sequence}...", LogKind.Warning);

        var code = await Shell.RunPowerShellAsync(
            $"Restore-Computer -RestorePoint {sequence} -Confirm:$false -ErrorAction Stop; 'ok'",
            line => _log.Write($"   {line}", LogKind.Info),
            ct).ConfigureAwait(false);

        if (code == 0)
        {
            _log.Write("   [OK] Restauração iniciada. O Windows sera reiniciado.", LogKind.Applied);
            return true;
        }

        _log.Write("   [ERRO] O Windows não aceitou iniciar a restauração.", LogKind.Failed);
        return false;
    }

    public void OpenRestoreWizard()
    {
        _log.Write("   [*] Abrindo o utilitario gráfico de Restauração do Sistema (rstrui.exe)...", LogKind.Info);
        Shell.Open("rstrui.exe");
    }
}
