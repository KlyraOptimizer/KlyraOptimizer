using Microsoft.Win32;

namespace KlyraOptimizer.Core;

/// <summary>
/// Motor de aplicacao de ajustes com as mesmas garantias anti-conflito do
/// script original (:SAFE_REG / :SAFE_SC / :SAFE_BCD_SET / :SAFE_BCD_DEL):
/// le o valor atual, pula o que ja esta aplicado e contabiliza tudo.
/// </summary>
public sealed class TweakEngine
{
    private readonly AppLog _log;

    public TweakEngine(AppLog log) => _log = log;

    public int Applied { get; private set; }

    public int Skipped { get; private set; }

    public int Failed { get; private set; }

    public event Action? CountersChanged;

    /// <summary>
    /// Publica os contadores sempre na thread da interface: os ajustes rodam
    /// em segundo plano e os assinantes do evento atualizam controles WPF.
    /// </summary>
    private void RaiseCounters()
    {
        var handler = CountersChanged;
        if (handler == null) return;

        Ui.Post(handler);
    }

    public void Reset()
    {
        Applied = 0;
        Skipped = 0;
        Failed = 0;
        RaiseCounters();
    }

    // ------------------------------------------------------------------
    // Registro
    // ------------------------------------------------------------------
    public void SetValue(string keyPath, string name, RegistryValueKind kind, object data, string description)
    {
        var current = ReadValue(keyPath, name);

        if (ValuesEqual(current, kind, data))
        {
            Skip($"{description} ({name} = {data})");
            return;
        }

        try
        {
            WriteValue(keyPath, name, kind, data);
            Applied++;
            _log.Write($"   [APLICADO] {description} ({name} -> {data})", LogKind.Applied);
        }
        catch (Exception ex)
        {
            Failed++;
            _log.Write($"   [BLOQUEADO/ERRO] {description} ({name}) — {Describe(ex)}", LogKind.Failed);
        }

        RaiseCounters();
    }

    public void RemoveValue(string keyPath, string name, string description)
    {
        if (ReadValue(keyPath, name) == null)
        {
            Skip($"{description} (ja estava no padrão)");
            return;
        }

        try
        {
            var (root, sub) = Split(keyPath);
            using var key = root.OpenSubKey(sub, writable: true);
            if (key == null)
            {
                Skip($"{description} (chave inexistente)");
                return;
            }

            key.DeleteValue(name, throwOnMissingValue: false);
            Applied++;
            _log.Write($"   [REMOVIDO] {description} ({name})", LogKind.Applied);
        }
        catch (Exception ex)
        {
            Failed++;
            _log.Write($"   [BLOQUEADO/ERRO] {description} ({name}) — {Describe(ex)}", LogKind.Failed);
        }

        RaiseCounters();
    }

    // ------------------------------------------------------------------
    // Servicos do Windows
    // ------------------------------------------------------------------
    public void SetServiceStart(string service, int start, string? description = null)
    {
        description ??= $"Serviço {service}";
        const string root = @"HKLM\SYSTEM\CurrentControlSet\Services";

        if (ReadValue($@"{root}\{service}", "Start") == null)
        {
            // Servico nao existe nesta edicao do Windows: ignora em silencio.
            return;
        }

        var current = ReadValue($@"{root}\{service}", "Start");
        if (current != null && Convert.ToInt64(current) == start)
        {
            Skip($"{description} (Start = {start})");
            return;
        }

        try
        {
            WriteValue($@"{root}\{service}", "Start", RegistryValueKind.DWord, start);
            Applied++;
            _log.Write($"   [SERVICO AJUSTADO] {description} (Start -> {start})", LogKind.Applied);
            RaiseCounters();
            return;
        }
        catch
        {
            // Alguns servicos protegidos recusam escrita direta: tenta via sc.exe
        }

        var mode = start switch
        {
            2 => "auto",
            3 => "demand",
            4 => "disabled",
            _ => "demand"
        };

        var output = Shell.Run("sc.exe", $"config \"{service}\" start= {mode}");
        if (output.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("SUCESSO", StringComparison.OrdinalIgnoreCase))
        {
            Applied++;
            _log.Write($"   [SERVICO AJUSTADO] {description} (sc config start= {mode})", LogKind.Applied);
        }
        else
        {
            Failed++;
            _log.Write($"   [BLOQUEADO/ERRO] {description} (serviço protegido pelo sistema)", LogKind.Failed);
        }

        RaiseCounters();
    }

    public void StopService(string service)
    {
        try
        {
            Shell.Run("sc.exe", $"stop \"{service}\"", 20000);
        }
        catch
        {
            // ignorado: alguns servicos ja estao parados
        }
    }

    // ------------------------------------------------------------------
    // BCD (boot configuration)
    // ------------------------------------------------------------------
    public void BcdSet(string property, string value, string description)
    {
        var enumCurrent = Shell.Run("bcdedit.exe", "/enum {current}", 20000);
        if (enumCurrent.Contains(property, StringComparison.OrdinalIgnoreCase) &&
            enumCurrent.Contains(value, StringComparison.OrdinalIgnoreCase))
        {
            Skip($"BCD {description} ({property} = {value})");
            return;
        }

        var result = Shell.RunCapture("bcdedit.exe", $"/set {property} {value}", 20000);
        if (result.Code == 0)
        {
            Applied++;
            _log.Write($"   [BCD APLICADO] {description} ({property} -> {value})", LogKind.Applied);
        }
        else
        {
            Failed++;
            _log.Write($"   [BLOQUEADO/ERRO] BCD recusou {property} (Secure Boot / BitLocker podem proteger este valor)", LogKind.Failed);
        }

        RaiseCounters();
    }

    public void BcdDelete(string property, string description)
    {
        var enumCurrent = Shell.Run("bcdedit.exe", "/enum {current}", 20000);
        if (!enumCurrent.Contains(property, StringComparison.OrdinalIgnoreCase))
        {
            Skip($"BCD {description} ({property} ja estava no padrão)");
            return;
        }

        var result = Shell.RunCapture("bcdedit.exe", $"/deletevalue {property}", 20000);
        if (result.Code == 0)
        {
            Applied++;
            _log.Write($"   [BCD REMOVIDO] {description} ({property})", LogKind.Applied);
        }
        else
        {
            Failed++;
            _log.Write($"   [BLOQUEADO/ERRO] BCD recusou remover {property}", LogKind.Failed);
        }

        RaiseCounters();
    }

    // ------------------------------------------------------------------
    // Ferramentas de linha de comando (compact, powercfg, netsh...)
    // ------------------------------------------------------------------
    public void RunTool(string fileName, string arguments, string description, bool countAsApplied = true)
    {
        try
        {
            Shell.Run(fileName, arguments, 120000);
            if (countAsApplied)
            {
                Applied++;
                _log.Write($"   [EXECUTADO] {description}", LogKind.Applied);
                RaiseCounters();
            }
            else
            {
                _log.Write($"   [EXECUTADO] {description}", LogKind.Info);
            }
        }
        catch (Exception ex)
        {
            Failed++;
            _log.Write($"   [ERRO] {description} — {Describe(ex)}", LogKind.Failed);
            RaiseCounters();
        }
    }

    // ------------------------------------------------------------------
    // Apoio
    // ------------------------------------------------------------------
    public void Skip(string message)
    {
        Skipped++;
        _log.Write($"   [PULADO - JA ATIVO] {message}", LogKind.Skipped);
        RaiseCounters();
    }

    /// <summary>Registra uma acao externa (script PowerShell, AppX...) nos contadores.</summary>
    public void MarkApplied(string description)
    {
        Applied++;
        _log.Write($"   [EXECUTADO] {description}", LogKind.Applied);
        RaiseCounters();
    }

    public static object? ReadValue(string keyPath, string name)
    {
        try
        {
            var (root, sub) = Split(keyPath);
            using var key = root.OpenSubKey(sub);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch
        {
            return null;
        }
    }

    public static int? ReadInt(string keyPath, string name)
    {
        var value = ReadValue(keyPath, name);
        if (value == null) return null;
        try { return Convert.ToInt32(value); } catch { return null; }
    }

    public static string? ReadString(string keyPath, string name)
    {
        var value = ReadValue(keyPath, name);
        return value == null ? null : Convert.ToString(value);
    }

    private static void WriteValue(string keyPath, string name, RegistryValueKind kind, object data)
    {
        var (root, sub) = Split(keyPath);
        using var key = root.CreateSubKey(sub, writable: true)
                        ?? throw new InvalidOperationException("não foi possível abrir a chave");
        key.SetValue(name, data, kind);
    }

    public static (RegistryKey Root, string SubKey) Split(string path)
    {
        var index = path.IndexOf('\\');
        var rootName = index < 0 ? path : path[..index];
        var sub = index < 0 ? string.Empty : path[(index + 1)..];

        RegistryKey root = rootName.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
            "HKCR" or "HKEY_CLASSES_ROOT" => Registry.ClassesRoot,
            "HKU" or "HKEY_USERS" => Registry.Users,
            "HKCC" or "HKEY_CURRENT_CONFIG" => Registry.CurrentConfig,
            _ => Registry.LocalMachine
        };

        return (root, sub);
    }

    private static bool ValuesEqual(object? current, RegistryValueKind kind, object target)
    {
        if (current == null) return false;

        switch (kind)
        {
            case RegistryValueKind.DWord:
            case RegistryValueKind.QWord:
                try { return Convert.ToInt64(current) == Convert.ToInt64(target); }
                catch { return false; }

            case RegistryValueKind.Binary:
                return current is byte[] a && target is byte[] b && a.SequenceEqual(b);

            default:
                return string.Equals(
                    Convert.ToString(current)?.Trim(),
                    Convert.ToString(target)?.Trim(),
                    StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Describe(Exception ex)
    {
        if (ex is UnauthorizedAccessException) return "acesso negado (chave protegida pelo Windows)";
        return ex.Message;
    }
}
