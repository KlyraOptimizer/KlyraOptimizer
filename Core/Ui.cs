using System.Windows;
using System.Windows.Threading;

namespace KlyraOptimizer.Core;

/// <summary>
/// Ponte segura para a thread da interface.
///
/// Os servicos (motor de ajustes, winget, GPU...) rodam em segundo plano via
/// Task.Run para nao travar a janela. Como qualquer assinante de evento pode
/// tocar em controles WPF, todo evento disparado por eles passa por aqui:
/// se a chamada ja estiver na thread da UI executa direto, senao enfileira.
/// </summary>
public static class Ui
{
    public static bool IsOnUiThread
    {
        get
        {
            var dispatcher = Application.Current?.Dispatcher;
            return dispatcher == null || dispatcher.CheckAccess();
        }
    }

    public static void Post(Action action)
    {
        if (action == null) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;

        dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    public static void Post(Action action, DispatcherPriority priority)
    {
        if (action == null) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;

        dispatcher.BeginInvoke(action, priority);
    }
}
