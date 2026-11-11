using System.Windows;

namespace SemiconductorCsvAnalyzer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Window window = new MainWindow();
        MainWindow = window; window.Show();
        if (e.Args.Length == 2 && e.Args[0] == "--smoke-test" && window is MainWindow main)
            Dispatcher.BeginInvoke(new Action(async () => await main.RunRejudgeSmokeAsync(e.Args[1])));
    }
}
