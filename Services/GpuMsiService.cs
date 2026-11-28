using System.Text.Json;
using KlyraOptimizer.Core;
using Microsoft.Win32;

namespace KlyraOptimizer.Services;

/// <summary>
/// Porte corrigido do modulo GpuMsi do FunçõesDoOtimizador.bat: ativa o modo MSI nas GPUs PCI
/// diretamente em HKLM\SYSTEM\CurrentControlSet\Enum\..., guarda o valor anterior em
/// HKLM\SOFTWARE\KlyraOptimizer\MSI e permite reverter exatamente como estava.
/// </summary>
public sealed class GpuMsiService
{
    private const string RecordKey = @"SOFTWARE\KlyraOptimizer\MSI";
    private const string LegacyRecordKey = @"SOFTWARE\AzureOptimizer\MSI";
    private const string Suffix = @"\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";

    private readonly AppLog _log;

    public GpuMsiService(AppLog log) => _log = log;

    public sealed class GpuInfo
    {
        public string Name { get; init; } = string.Empty;

        public string PnpId { get; init; } = string.Empty;

        public string Vendor => PnpId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
            : PnpId.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) || PnpId.Contains("VEN_1022", StringComparison.OrdinalIgnoreCase) ? "AMD"
            : PnpId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) ? "Intel"
            : "Desconhecida";
    }

    public List<GpuInfo> GetGpus()
    {
        var list = new List<GpuInfo>();

        try
        {
            var json = Shell.RunPowerShellCapture(
                "Get-CimInstance Win32_VideoController | Where-Object { $_.PNPDeviceID -like 'PCI\\*' } | " +
                "Select-Object Name,PNPDeviceID | ConvertTo-Json -Compress",
                45000);

            var objectIndex = json.IndexOf('{');
            var arrayIndex = json.IndexOf('[');
            var start = objectIndex < 0
                ? arrayIndex
                : arrayIndex < 0 ? objectIndex : Math.Min(objectIndex, arrayIndex);

            if (start < 0)
            {
                _log.Write("   [AVISO] Nenhuma GPU PCI retornada pela consulta WMI.", LogKind.Info);
                return list;
            }

            var payload = json[start..];
            var end = Math.Max(payload.LastIndexOf('}'), payload.LastIndexOf(']'));
            if (end >= 0) payload = payload[..(end + 1)];

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in root.EnumerateArray())
                {
                    AddGpu(element, list);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                AddGpu(root, list);
            }
        }
        catch (Exception ex)
        {
            _log.Write($"   [AVISO] Não foi possível listar as GPUs: {ex.Message}", LogKind.Warning);
        }

        return list;
    }

    private static void AddGpu(JsonElement element, List<GpuInfo> list)
    {
        var name = element.TryGetProperty("Name", out var n) ? n.GetString() : null;
        var pnp = element.TryGetProperty("PNPDeviceID", out var p) ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(pnp)) return;

        list.Add(new GpuInfo { Name = name ?? "GPU", PnpId = pnp });
    }

    public void Apply(TweakEngine engine)
    {
        // Limpa eventual subchave orfa "HKLM\HKLM" criada por versoes antigas
        try { Registry.LocalMachine.DeleteSubKeyTree("HKLM", throwOnMissingSubKey: false); } catch { }

        var gpus = GetGpus();
        if (gpus.Count == 0)
        {
            _log.Write("   [AVISO] Nenhuma GPU PCI detectada - modo MSI ignorado.", LogKind.Warning);
            return;
        }

        foreach (var gpu in gpus)
        {
            _log.Write($"   [GPU] {gpu.Name} [{gpu.Vendor}]", LogKind.Info);

            // Caminho relativo a HKLM (sem prefixo "HKLM\") para CreateSubKey/OpenSubKey
            var subKey = $@"SYSTEM\CurrentControlSet\Enum\{gpu.PnpId}{Suffix}";
            var fullPath = $@"HKLM\{subKey}";
            var current = TweakEngine.ReadInt(fullPath, "MSISupported");

            if (current == 1)
            {
                engine.Skip($"Modo MSI {gpu.Vendor} (ja ativo)");
                continue;
            }

            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(subKey, writable: true)
                                ?? throw new InvalidOperationException("chave PCI do dispositivo não encontrada");
                key.SetValue("MSISupported", 1, RegistryValueKind.DWord);

                using var record = Registry.LocalMachine.CreateSubKey(RecordKey, writable: true);
                var name = string.Concat(gpu.PnpId.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
                record?.SetValue(name, $"{gpu.PnpId}|{(current?.ToString() ?? "absent")}", RegistryValueKind.String);

                engine.MarkApplied($"Modo MSI {gpu.Vendor} (reinicie para efetivar)");
            }
            catch (Exception ex)
            {
                engine.MarkFailed($"Modo MSI {gpu.Vendor} — {ex.Message}");
            }
        }
    }

    public void Revert(TweakEngine engine)
    {
        try { Registry.LocalMachine.DeleteSubKeyTree("HKLM", throwOnMissingSubKey: false); } catch { }

        var revertedAny = RevertFromRecordKey(RecordKey, engine) | RevertFromRecordKey(LegacyRecordKey, engine);
        if (!revertedAny)
        {
            _log.Write("   Nenhum ajuste de MSI Mode registrado (nada a reverter).", LogKind.Skipped);
        }
    }

    private bool RevertFromRecordKey(string recordKeyPath, TweakEngine engine)
    {
        using var record = Registry.LocalMachine.OpenSubKey(recordKeyPath);
        if (record == null) return false;

        var names = record.GetValueNames();
        if (names.Length == 0) return false;

        var any = false;
        foreach (var name in names)
        {
            var value = record.GetValue(name) as string;
            if (string.IsNullOrEmpty(value)) continue;

            var parts = value.Split('|');
            if (parts.Length < 2) continue;

            var subKey = $@"SYSTEM\CurrentControlSet\Enum\{parts[0]}{Suffix}";

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(subKey, writable: true);
                if (key != null)
                {
                    if (parts[1] == "absent")
                    {
                        key.DeleteValue("MSISupported", throwOnMissingValue: false);
                    }
                    else if (int.TryParse(parts[1], out var prevVal))
                    {
                        key.SetValue("MSISupported", prevVal, RegistryValueKind.DWord);
                    }
                }

                using var writable = Registry.LocalMachine.OpenSubKey(recordKeyPath, writable: true);
                writable?.DeleteValue(name, throwOnMissingValue: false);

                engine.MarkApplied($"Modo MSI revertido ({parts[0]})");
                any = true;
            }
            catch (Exception ex)
            {
                engine.MarkFailed($"Modo MSI de {parts[0]} — {ex.Message}");
            }
        }

        return any;
    }
}
