using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using LifeLock.Core;
using Microsoft.Win32;

namespace LifeLock.App;

static class Installer
{
    const string Svc = "LifeLock";
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LifeLock";
    static readonly string Dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LifeLock");
    static readonly string Shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "LifeLock.lnk");

    public static void Install()
    {
        if (!EnsureAdmin("--install")) return;
        if (Run("sc.exe", "query LifeLock").code == 0) { Say("LifeLock is already installed."); return; }

        try
        {
            CopyDir(AppContext.BaseDirectory, Dest);
            Directory.CreateDirectory(Store.Dir);
            var exe = Path.Combine(Dest, "LifeLock.Service.exe");
            Must(Run("sc.exe", $"create {Svc} binPath= \"{exe}\" start= auto DisplayName= \"LifeLock\""));
            Run("sc.exe", $"description {Svc} \"Enforces LifeLock app locks.\"");
            Must(Run("sc.exe", $"failure {Svc} reset= 0 actions= restart/0/restart/0/restart/0"));
            Run("sc.exe", $"failureflag {Svc} 1");

            Protect(Dest, usersCanRead: true);
            Protect(Store.Dir, usersCanRead: false);

            using (var k = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                var ui = Path.Combine(Dest, "LifeLock.exe");
                k.SetValue("DisplayName", "LifeLock");
                k.SetValue("DisplayVersion", "1.0.0");
                k.SetValue("Publisher", "LifeLock");
                k.SetValue("DisplayIcon", ui);
                k.SetValue("InstallLocation", Dest);
                k.SetValue("UninstallString", $"\"{ui}\" --uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            var lnk = shell.CreateShortcut(Shortcut);
            lnk.TargetPath = Path.Combine(Dest, "LifeLock.exe");
            lnk.Save();

            Must(Run("sc.exe", $"start {Svc}"));
            Must(Run("sc.exe", $"sdset {Svc} {Sddl.Locked}"));
            Say("LifeLock is installed and running.\nOpen it from the Start Menu.");
        }
        catch (Exception e) { Say("Install failed: " + e.Message); }
    }

    public static void Uninstall()
    {
        if (!EnsureAdmin("--uninstall")) return;

        if (Run("sc.exe", "query LifeLock").code == 0)
        {
            var r = Pipe.Send(new Request("PrepareUninstall"));
            if (!r.Ok)
            {
                Say(r.Error + (r.Offline ? "\n\nStart the LifeLock service and try again." : ""), MessageBoxIcon.Stop);
                return;
            }
            for (int i = 0; i < 40 && !Run("sc.exe", "query LifeLock").output.Contains("STOPPED"); i++) Thread.Sleep(250);
            Must(Run("sc.exe", $"delete {Svc}"));
        }

        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        try { File.Delete(Shortcut); } catch { }
        try { Directory.Delete(Store.Dir, true); } catch { }
        // We're running from Dest, so delete it after we exit.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{Dest}\"")
            { CreateNoWindow = true, UseShellExecute = false });
        Say("LifeLock has been uninstalled.");
    }

    /// <summary>SYSTEM full control; Administrators (and the owner) read-only, so files/rules can't be edited or deleted.</summary>
    static void Protect(string dir, bool usersCanRead)
    {
        const InheritanceFlags inh = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var ds = new DirectorySecurity();
        ds.SetAccessRuleProtection(true, false);
        void Add(WellKnownSidType sid, FileSystemRights rights) =>
            ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), rights, inh, PropagationFlags.None, AccessControlType.Allow));
        Add(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
        Add(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.ReadAndExecute);
        // OWNER RIGHTS ACE removes the owner's implicit right to rewrite the ACL.
        ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-3-4"), FileSystemRights.ReadAndExecute, inh, PropagationFlags.None, AccessControlType.Allow));
        if (usersCanRead) Add(WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute);
        new DirectoryInfo(dir).SetAccessControl(ds);
    }

    static void CopyDir(string src, string dst)
    {
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(dst, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to, true);
        }
    }

    static bool EnsureAdmin(string arg)
    {
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return true;
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arg) { UseShellExecute = true, Verb = "runas" }); }
        catch { Say("Administrator rights are required."); }
        return false;
    }

    static (int code, string output) Run(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args)
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    static void Must((int code, string output) r)
    {
        if (r.code != 0) throw new Exception(r.output.Trim());
    }

    static void Say(string msg, MessageBoxIcon icon = MessageBoxIcon.Information) =>
        MessageBox.Show(msg, "LifeLock", MessageBoxButtons.OK, icon);
}
