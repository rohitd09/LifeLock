using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using LifeLock.Core;

namespace LifeLock.Service;

sealed class Engine(bool isService, Action stop)
{
    readonly object gate = new();
    readonly State state = Store.Load();
    readonly Dictionary<string, string?> keyCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, long> lastPopup = [];
    DateTime lastWall;
    long lastMono, lastSave;

    TimeSpan Offset { get => TimeSpan.FromTicks(state.OffsetTicks); set => state.OffsetTicks = value.Ticks; }
    // Trusted time: wall clock minus any detected manual clock jumps.
    DateTime EffUtc => DateTime.UtcNow - Offset;
    DateTime Now => EffUtc.ToLocalTime();

    public void Start()
    {
        if (EffUtc < state.LastUtc)
        {
            Offset = DateTime.UtcNow - state.LastUtc;
            Log($"Clock is behind last-seen time by {-Offset}; compensating.");
        }
        lastWall = DateTime.UtcNow;
        lastMono = Environment.TickCount64;
        new Thread(Loop) { IsBackground = true, Name = "enforce" }.Start();
        _ = Task.Run(Serve);
        Log($"Started with {state.Rules.Count} rule(s).");
    }

    void Loop()
    {
        while (true)
        {
            try { Tick(); } catch (Exception e) { Log("tick: " + e); }
            Thread.Sleep(1000);
        }
    }

    void Tick()
    {
        var wall = DateTime.UtcNow;
        var mono = Environment.TickCount64; // includes sleep/hibernate, so only manual clock changes show as skew
        var elapsed = TimeSpan.FromMilliseconds(mono - lastMono);
        var skew = (wall - lastWall) - elapsed;
        lastWall = wall;
        lastMono = mono;
        var procs = Snapshot();

        lock (gate)
        {
            if (skew.Duration() > TimeSpan.FromMinutes(2))
            {
                Offset += skew;
                Log($"System clock jumped by {skew}; keeping locks on trusted time.");
            }
            else if (Offset != TimeSpan.Zero)
            {
                // ponytail: offset heals at 1 min/hour so legit NTP corrections don't skew time forever;
                // a large forward tamper therefore drains slowly. Use a network time source if that matters.
                var step = elapsed / 60;
                Offset = Offset.Duration() <= step ? TimeSpan.Zero : Offset - (Offset > TimeSpan.Zero ? step : -step);
            }

            var now = Now;
            var today = DateOnly.FromDateTime(now);
            foreach (var r in state.Rules)
            {
                var hits = procs.Where(p => Matches(r, p.Path)).ToList();
                if (r.Mode == Mode.Allowance)
                {
                    if (r.UsageDate != today) { r.UsageDate = today; r.UsedSecondsToday = 0; }
                    if (hits.Count > 0 && Rules.LockedUntil(r, now) == null)
                        r.UsedSecondsToday += Math.Min(elapsed.TotalSeconds, 5);
                }
                if (hits.Count == 0) continue;
                var until = Rules.LockedUntil(r, now);
                if (until == null) continue;
                foreach (var p in hits) Kill(p.Pid);
                Popup(r, until.Value, hits[0].Session, mono);
            }

            state.LastUtc = EffUtc;
            if (mono - lastSave > 15000) Save();
        }
    }

    bool Matches(Rule r, string path)
    {
        if (string.Equals(path, r.ExePath, StringComparison.OrdinalIgnoreCase)) return true;
        if (r.Key == null) return false;
        if (!keyCache.TryGetValue(path, out var key)) keyCache[path] = key = Rules.Identity(path);
        return key == r.Key;
    }

    record Proc(int Pid, string Path, int Session);

    static List<Proc> Snapshot()
    {
        var list = new List<Proc>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                var path = Native.ImagePath(p.Id);
                if (path == null) continue;
                try { list.Add(new(p.Id, path, p.SessionId)); } catch { }
            }
        }
        return list;
    }

    static void Kill(int pid)
    {
        try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
    }

    void Popup(Rule r, DateTime until, int session, long mono)
    {
        if (lastPopup.TryGetValue(r.Id, out var last) && mono - last < 30000) return;
        lastPopup[r.Id] = mono;
        var when = until.Date == Now.Date ? until.ToString("HH:mm") : until.ToString("ddd d MMM HH:mm");
        Native.Message(session, "LifeLock", $"{r.DisplayName} is locked until {when}.");
        Log($"Blocked {r.DisplayName} (locked until {until:g}).");
    }

    async Task Serve()
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));

        while (true)
        {
            try
            {
                using var pipe = NamedPipeServerStreamAcl.Create(Pipe.Name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, sec);
                await pipe.WaitForConnectionAsync();
                using var cts = new CancellationTokenSource(5000);
                var line = await new StreamReader(pipe).ReadLineAsync(cts.Token);
                Request? req = null;
                try { req = line == null ? null : JsonSerializer.Deserialize<Request>(line); } catch (JsonException) { }
                var resp = req == null ? Fail("Bad request.") : Handle(req);
                await new StreamWriter(pipe) { AutoFlush = true }.WriteLineAsync(JsonSerializer.Serialize(resp));
                pipe.WaitForPipeDrain();
                if (resp.Ok && req!.Cmd == "PrepareUninstall") { Log("Uninstall approved; stopping."); stop(); return; }
            }
            catch (Exception e) { Log("pipe: " + e.Message); await Task.Delay(500); }
        }
    }

    Response Handle(Request q)
    {
        lock (gate)
        {
            var now = Now;
            switch (q.Cmd)
            {
                case "List":
                    return Listing(now);

                case "Upsert":
                    return q.Rule == null ? Fail("Missing rule.") : Upsert(q.Rule, now);

                case "Remove":
                {
                    var r = state.Rules.FirstOrDefault(x => x.Id == q.Id);
                    if (r == null) return Fail("No such lock.");
                    var until = Rules.LockedUntil(r, now);
                    if (until != null) return Fail($"{r.DisplayName} is on lockdown until {until:g}. It can be removed after that.");
                    state.Rules.Remove(r);
                    Save();
                    Log($"Removed lock on {r.DisplayName}.");
                    return Listing(now);
                }

                case "PrepareUninstall":
                {
                    foreach (var r in state.Rules)
                        if (Rules.LockedUntil(r, now) is { } until)
                            return Fail($"Lockdown active: {r.DisplayName} is locked until {until:g}.\nLifeLock can't be uninstalled until every lockdown has ended.");
                    Save();
                    if (isService) Unprotect();
                    return new Response { Ok = true };
                }

                default:
                    return Fail("Unknown command.");
            }
        }
    }

    Response Upsert(Rule n, DateTime now)
    {
        if (Rules.DenyReason(n.ExePath) is { } deny) return Fail(deny);
        n.ExePath = Path.GetFullPath(n.ExePath);
        n.Key = Rules.Identity(n.ExePath);
        var i = state.Rules.FindIndex(x => x.Id == n.Id);
        var old = i >= 0 ? state.Rules[i] : null;

        if (n.Mode == Mode.Duration)
            n.LockUntilUtc = n.DurationMinutes > 0 ? EffUtc.AddMinutes(n.DurationMinutes) : old?.LockUntilUtc ?? default;
        n.DurationMinutes = 0;
        if (n.Mode == Mode.Duration && n.LockUntilUtc <= EffUtc) return Fail("Pick a lock duration.");
        if (n.Mode != Mode.Duration && n.Weekday == null && n.Weekend == null) return Fail("Pick at least one restricted day type.");
        if (n.Mode == Mode.Window && new[] { n.Weekday, n.Weekend }.Any(p => p != null && p.Start == p.End))
            return Fail("Start and end time are the same. Pick different times.");

        if (old != null)
        {
            n.UsedSecondsToday = old.UsedSecondsToday; // usage is the service's, never the client's
            n.UsageDate = old.UsageDate;
            if (Rules.LockedUntil(old, now) is { } until && Rules.IsLoosening(old, n))
                return Fail($"{old.DisplayName} is on lockdown until {until:g}. Until then you can only make the lock stricter.");
            state.Rules[i] = n;
        }
        else
        {
            n.UsageDate = DateOnly.FromDateTime(now);
            n.UsedSecondsToday = 0;
            state.Rules.Add(n);
        }
        Save();
        Log($"Saved lock on {n.DisplayName} ({n.Mode}).");
        return Listing(now);
    }

    Response Listing(DateTime now) => new()
    {
        Ok = true,
        Rules = state.Rules.Select(r => new RuleView(r, Rules.LockedUntil(r, now))).ToList(),
    };

    static Response Fail(string e) => new() { Error = e };

    void Save()
    {
        try { Store.Save(state); lastSave = Environment.TickCount64; }
        catch (Exception e) { Log("save: " + e.Message); }
    }

    /// <summary>Hand control back to Administrators so the uninstaller can stop/delete the service and files.</summary>
    static void Unprotect()
    {
        Native.Run("sc.exe", $"sdset LifeLock {Sddl.Open}");
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        foreach (var dir in new[] { AppContext.BaseDirectory, Store.Dir })
        {
            try
            {
                var di = new DirectoryInfo(dir);
                var ds = di.GetAccessControl();
                ds.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                di.SetAccessControl(ds);
            }
            catch (Exception e) { Store.Log($"unprotect {dir}: {e.Message}"); }
        }
    }

    void Log(string msg)
    {
        Store.Log(msg);
        if (!isService) Console.WriteLine($"{DateTime.Now:HH:mm:ss} {msg}");
    }
}

static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool WTSSendMessageW(IntPtr server, int session, string title, int titleLen,
        string msg, int msgLen, int style, int timeout, out int response, bool wait);

    public static string? ImagePath(int pid)
    {
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    // A service lives in session 0 and can't show UI itself; WTSSendMessage pops a box on the user's desktop.
    public static void Message(int session, string title, string text) =>
        WTSSendMessageW(IntPtr.Zero, session, title, title.Length * 2, text, text.Length * 2,
            0x30 /*ICONWARNING*/ | 0x40000 /*TOPMOST*/ | 0x10000 /*SETFOREGROUND*/, 15, out _, false);

    public static void Run(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit(10000);
    }
}
