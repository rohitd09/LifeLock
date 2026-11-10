using System.ServiceProcess;
using LifeLock.Service;

if (args.Contains("--console"))
{
    new Engine(isService: false, stop: () => Environment.Exit(0)).Start();
    Console.WriteLine("LifeLock engine running in console mode. Ctrl+C to quit.");
    Thread.Sleep(Timeout.Infinite);
}
else
{
    ServiceBase.Run(new LockService());
}

sealed class LockService : ServiceBase
{
    public LockService()
    {
        ServiceName = "LifeLock";
        CanStop = false; // SCM refuses "sc stop"; only an approved uninstall stops us (via Stop() below)
        CanShutdown = true;
    }

    protected override void OnStart(string[] args) => new Engine(isService: true, stop: Stop).Start();
}
