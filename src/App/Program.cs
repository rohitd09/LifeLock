namespace LifeLock.App;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--install")) Installer.Install();
        else if (args.Contains("--uninstall")) Installer.Uninstall();
        else if (args.Length == 2 && args[0] == "--list-games") // debug: write detected games to a file
            File.WriteAllLines(args[1], Core.GameScanner.Scan().Select(g => $"{g.Store}\t{g.Name}\t{g.Folder}\t{g.Exe}"));
        else Application.Run(new MainForm());
    }
}
