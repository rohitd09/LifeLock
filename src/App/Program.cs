namespace LifeLock.App;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--install")) Installer.Install();
        else if (args.Contains("--uninstall")) Installer.Uninstall();
        else Application.Run(new MainForm());
    }
}
