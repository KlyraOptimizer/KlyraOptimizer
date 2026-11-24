using System.IO;
using System.Text;
using Microsoft.Win32;

namespace KlyraOptimizer.Core;

/// <summary>
/// Teste de sanidade do motor de ajustes. Executa apenas em uma chave
/// descartavel (HKCU\Software\KlyraOptimizer\SelfTest) e nunca altera
/// configuracoes reais do Windows. Usado para validar builds.
/// </summary>
public static class SelfTest
{
    private const string TestKey = @"HKCU\Software\KlyraOptimizer\SelfTest";

    public static bool Run(string reportFile)
    {
        var report = new StringBuilder();
        var passed = true;

        void Check(string name, bool condition, string detail)
        {
            report.AppendLine($"{(condition ? "PASS" : "FALHA")} | {name} | {detail}");
            if (!condition) passed = false;
        }

        var log = new AppLog();
        var engine = new TweakEngine(log);

        try
        {
            engine.SetValue(TestKey, "DwordValue", RegistryValueKind.DWord, 42, "self-test dword");
            Check("escrita DWORD", engine.Applied == 1, $"applied={engine.Applied}");

            engine.SetValue(TestKey, "DwordValue", RegistryValueKind.DWord, 42, "self-test dword");
            Check("anti-conflito DWORD", engine.Skipped == 1, $"skipped={engine.Skipped}");

            engine.SetValue(TestKey, "StringValue", RegistryValueKind.String, "abc", "self-test string");
            Check("escrita REG_SZ", TweakEngine.ReadString(TestKey, "StringValue") == "abc",
                $"valor={TweakEngine.ReadString(TestKey, "StringValue")}");

            Check("leitura DWORD", TweakEngine.ReadInt(TestKey, "DwordValue") == 42,
                $"valor={TweakEngine.ReadInt(TestKey, "DwordValue")}");

            var before = engine.Skipped;
            engine.SetServiceStart("klyra-optimizer-servico-inexistente", 4);
            Check("serviço inexistente ignorado", engine.Skipped == before && engine.Failed == 0,
                $"skipped={engine.Skipped} failed={engine.Failed}");

            engine.RemoveValue(TestKey, "StringValue", "self-test remoção");
            Check("remoção de valor", TweakEngine.ReadValue(TestKey, "StringValue") == null,
                "valor removido");

            engine.BcdSet("propriedade-invalida", "0", "self-test bcd");
            Check("BCD invalido não derruba o app", engine.Failed == 1, $"failed={engine.Failed}");

            try
            {
                var block = new System.Windows.Controls.TextBlock();
                var received = 0;
                var deliveredOnUiThread = false;

                void OnCounters()
                {
                    deliveredOnUiThread = Ui.IsOnUiThread;
                    received = engine.Applied;
                    block.Text = $"aplicados={received}";
                }

                engine.CountersChanged += OnCounters;

                Task.Run(() => engine.SetValue(TestKey, "ThreadValue", RegistryValueKind.DWord, 7, "self-test thread")).Wait();

                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    () => { }, System.Windows.Threading.DispatcherPriority.Background);

                Check("contadores atualizados a partir de segundo plano",
                    deliveredOnUiThread && received > 0 && block.Text.Length > 0,
                    deliveredOnUiThread
                        ? "evento do motor entregue na thread da interface"
                        : "evento chegou na thread errada (risco de acesso cruzado)");

                engine.CountersChanged -= OnCounters;
            }
            catch (Exception ex)
            {
                Check("contadores atualizados a partir de segundo plano", false, ex.GetBaseException().Message);
            }

            Check("arquivo de log em disco", File.Exists(log.FilePath), log.FilePath);

            report.AppendLine($"Contadores finais: aplicados={engine.Applied} pulados={engine.Skipped} erros={engine.Failed}");
        }
        catch (Exception ex)
        {
            report.AppendLine("EXCECAO | " + ex);
            passed = false;
        }
        finally
        {
            try
            {
                var (root, sub) = TweakEngine.Split(TestKey);
                root.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
            }
            catch
            {
                // chave descartavel
            }
        }

        report.AppendLine(passed ? "RESULTADO: OK" : "RESULTADO: FALHOU");

        try
        {
            File.WriteAllText(reportFile, report.ToString());
        }
        catch
        {
            // sem arquivo, sem problema
        }

        return passed;
    }
}
