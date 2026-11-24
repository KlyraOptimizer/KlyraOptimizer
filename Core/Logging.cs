using System.IO;
using System.Text;

namespace KlyraOptimizer.Core;

public enum LogKind
{
    Info,
    Applied,
    Skipped,
    Failed,
    Warning,
    Phase,
    Command
}

/// <summary>
/// Log silencioso da sessao em segundo plano: grava tudo em arquivo UTF-8 na
/// pasta %ProgramData%\KlyraOptimizer\logs sem ocupar espaço na interface.
/// </summary>
public sealed class AppLog
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly object _sync = new();

    public AppLog()
    {
        Directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "KlyraOptimizer",
            "logs");

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
        }
        catch
        {
            // sem permissao: cai para a pasta local do usuario
            Directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KlyraOptimizer",
                "logs");
            System.IO.Directory.CreateDirectory(Directory);
        }

        FilePath = Path.Combine(Directory, $"klyra_{DateTime.Now:yyyyMMdd_HHmmss}.log");
    }

    public string Directory { get; }

    public string FilePath { get; }

    /// <summary>Ultima mensagem relevante gravada (util para barras de status discretas).</summary>
    public string LastStatus { get; private set; } = string.Empty;

    public event Action<string, LogKind>? Logged;

    public void Write(string message, LogKind kind = LogKind.Info)
    {
        try
        {
            lock (_sync)
            {
                File.AppendAllText(
                    FilePath,
                    $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}",
                    Utf8NoBom);
            }
        }
        catch
        {
            // o log em arquivo nunca deve derrubar a aplicacao
        }

        var trimmed = message?.Trim() ?? string.Empty;
        if (trimmed.Length > 0)
        {
            LastStatus = trimmed;
            var handler = Logged;
            if (handler != null)
            {
                Ui.Post(() => handler(trimmed, kind));
            }
        }
    }

    public void Phase(string title)
    {
        Write(string.Empty);
        Write(title, LogKind.Phase);
    }
}
