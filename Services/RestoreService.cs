using System.IO;
using KlyraOptimizer.Core;
using Microsoft.Win32;

namespace KlyraOptimizer.Services;

public sealed class RestorePointInfo
{
    public int Sequence { get; init; }

    public string Description { get; init; } = string.Empty;

    public string Created { get; init; } = string.Empty;
}

/// <summary>
/// Ponto de restauracao: verificacao automatica da Protecao do Sistema,
/// ativacao forcada na unidade do sistema, criacao garantida via WMI SystemRestore
/// (com fallback Checkpoint-Computer), listagem e restauracao.
/// </summary>
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

    /// <summary>
    /// Garante que os servicos VSS/System Restore estejam ativos, desbloqueia politicas,
    /// remove o limite de 24h entre pontos e ativa a Protecao do Sistema na unidade do Windows.
    /// </summary>
    public void EnsureProtection()
    {
        _log.Write("   [*] Verificando e garantindo que a Proteção do Sistema esteja ativa...", LogKind.Info);

        var driveRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var driveColon = driveRoot.Length >= 2 ? driveRoot[..2] : "C:";

        // Desbloqueia politicas de grupo e zera o intervalo minimo de 24h entre pontos
        try
        {
            using var polKey = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore", writable: true);
            polKey?.DeleteValue("DisableSR", throwOnMissingValue: false);
            polKey?.DeleteValue("DisableConfig", throwOnMissingValue: false);
        }
        catch { }

        try
        {
            using var srKey = Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", writable: true);
            srKey?.SetValue("SystemRestorePointCreationFrequency", 0, RegistryValueKind.DWord);
            srKey?.DeleteValue("DisableSR", throwOnMissingValue: false);
        }
        catch { }

        // Inicia os servicos essenciais e aguarda ficarem prontos, ativando a unidade do sistema
        var safeDrive = driveRoot.Replace("'", "''");
        var script = $@"
foreach ($svc in @('swprv','VSS','srservice')) {{
    $mode = if ($svc -eq 'srservice') {{ 'Automatic' }} else {{ 'Manual' }}
    try {{ Set-Service -Name $svc -StartupType $mode -ErrorAction SilentlyContinue }} catch {{}}
    try {{ Start-Service -Name $svc -ErrorAction SilentlyContinue }} catch {{}}
}}
for ($i = 0; $i -lt 15; $i++) {{
    $sr = Get-Service -Name 'srservice' -ErrorAction SilentlyContinue
    if ($sr -and $sr.Status -eq 'Running') {{ break }}
    Start-Sleep -Milliseconds 250
}}
try {{ Enable-ComputerRestore -Drive '{safeDrive}' -ErrorAction Stop; Write-Output 'ENABLE_OK' }}
catch {{ Write-Output ('ENABLE_WARN: ' + $_.Exception.Message) }}
";
        Shell.RunPowerShellAsync(
            script,
            line =>
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("ENABLE_WARN:", StringComparison.Ordinal))
                {
                    _log.Write($"   [AVISO] {trimmed}", LogKind.Warning);
                }
            }).GetAwaiter().GetResult();

        Shell.Run("vssadmin.exe", $"resize shadowstorage /for={driveColon} /on={driveColon} /maxsize=5%", 45000);

        _system.Refresh();

        if (_system.RestoreProtectionEnabled)
        {
            _log.Write($"   [OK] Proteção do Sistema ativa em {driveRoot}.", LogKind.Applied);
        }
        else
        {
            _log.Write($"   [AVISO] O Windows reportou que a Proteção do Sistema pode ainda estar restrita em {driveRoot}.", LogKind.Warning);
        }
    }

    /// <summary>
    /// Fluxo completo e obrigatorio antes de qualquer otimizacao:
    /// verifica se a Protecao do Sistema esta ligada ou desligada, liga automaticamente
    /// caso necessario e cria o Ponto de Restauracao sem depender de limite de 24h ou wuauserv.
    /// </summary>
    public async Task<bool> EnsureAndCreatePointAsync(string description, CancellationToken ct = default)
    {
        _log.Phase("PONTO DE RESTAURAÇÃO AUTOMÁTICO");
        await Task.Run(EnsureProtection, ct).ConfigureAwait(false);
        return await CreatePointAsync(description, ct).ConfigureAwait(false);
    }

    public async Task<bool> CreatePointAsync(string description, CancellationToken ct = default)
    {
        _log.Write($"   [*] Criando ponto de restauração '{description}' (aguarde)...", LogKind.Info);

        var safeDesc = (string.IsNullOrWhiteSpace(description) ? "Klyra Optimizer" : description)
            .Replace("'", "''");

        // Usa diretamente a classe WMI SystemRestore (tipo 12 = MODIFY_SETTINGS, evento 100 = BEGIN_SYSTEM_CHANGE),
        // que respeita SystemRestorePointCreationFrequency=0 imediatamente e nao falha quando o Windows Update
        // (wuauserv) foi desativado em otimizacoes anteriores. Mantem Checkpoint-Computer como fallback.
        var script = $@"
try {{
    Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore' -Name 'SystemRestorePointCreationFrequency' -Type DWord -Value 0 -Force -ErrorAction SilentlyContinue
}} catch {{}}

foreach ($svc in @('swprv','VSS','srservice')) {{
    try {{ Start-Service -Name $svc -ErrorAction SilentlyContinue }} catch {{}}
}}

$created = $false
try {{
    $wmi = [wmiclass]'\\.\root\default:SystemRestore'
    $res = $wmi.CreateRestorePoint('{safeDesc}', 12, 100)
    if ($res -and $res.ReturnValue -eq 0) {{
        $created = $true
        Write-Output 'WMI_CREATE_OK'
    }} else {{
        Write-Output ('WMI_RC=' + $res.ReturnValue)
    }}
}} catch {{
    Write-Output ('WMI_ERR: ' + $_.Exception.Message)
}}

if (-not $created) {{
    $wuStart = (Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\wuauserv' -Name 'Start' -ErrorAction SilentlyContinue).Start
    if ($wuStart -eq 4) {{
        try {{ sc.exe config wuauserv start= demand | Out-Null }} catch {{}}
    }}
    try {{
        Checkpoint-Computer -Description '{safeDesc}' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop
        $created = $true
        Write-Output 'CHECKPOINT_OK'
    }} catch {{
        Write-Output ('CHECKPOINT_ERR: ' + $_.Exception.Message)
    }} finally {{
        if ($wuStart -eq 4) {{
            try {{ sc.exe config wuauserv start= disabled | Out-Null }} catch {{}}
        }}
    }}
}}

if ($created) {{
    $latest = Get-ComputerRestorePoint -ErrorAction SilentlyContinue | Sort-Object SequenceNumber -Descending | Select-Object -First 1
    if ($latest) {{
        Write-Output ('SEQ=' + $latest.SequenceNumber)
    }}
    exit 0
}}
exit 1
";

        string? createdSeq = null;
        var code = await Shell.RunPowerShellAsync(
            script,
            line =>
            {
                var text = line.Trim();
                if (text.Length == 0) return;
                if (text.StartsWith("SEQ=", StringComparison.Ordinal))
                {
                    createdSeq = text[4..];
                }
                else if (text.StartsWith("WMI_ERR:", StringComparison.Ordinal) ||
                         text.StartsWith("CHECKPOINT_ERR:", StringComparison.Ordinal))
                {
                    _log.Write($"   {text}", LogKind.Warning);
                }
            },
            ct).ConfigureAwait(false);

        _system.Refresh();

        if (code == 0)
        {
            var seqSuffix = !string.IsNullOrWhiteSpace(createdSeq) ? $" (#{createdSeq})" : string.Empty;
            _log.Write($"   [OK] Ponto de restauração criado com sucesso{seqSuffix}.", LogKind.Applied);
            return true;
        }

        _log.Write("   [AVISO] Não foi possível criar o ponto de restauração agora (verifique espaço livre no disco).", LogKind.Warning);
        return false;
    }

    public async Task<List<RestorePointInfo>> GetPointsAsync(CancellationToken ct = default)
    {
        var points = new List<RestorePointInfo>();

        const string script = @"
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
            _log.Write("   [OK] Restauração iniciada. O Windows será reiniciado.", LogKind.Applied);
            return true;
        }

        _log.Write("   [ERRO] O Windows não aceitou iniciar a restauração.", LogKind.Failed);
        return false;
    }

    public void OpenRestoreWizard()
    {
        _log.Write("   [*] Abrindo o utilitário gráfico de Restauração do Sistema (rstrui.exe)...", LogKind.Info);
        Shell.Open("rstrui.exe");
    }
}
