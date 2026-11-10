using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace LifeLock.Core;

public enum Mode { Duration, Allowance, Window }

/// <summary>Restriction for one kind of day. Window uses Start/End, Allowance uses AllowanceMinutes.</summary>
public class Plan
{
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }
    public int AllowanceMinutes { get; set; }
}

public class Rule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ExePath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>OriginalFilename|ProductName|CompanyName — catches renamed/copied exes.</summary>
    public string? Key { get; set; }
    public Mode Mode { get; set; }
    /// <summary>false = same plan every day (Weekday and Weekend hold the same values).</summary>
    public bool Split { get; set; }
    /// <summary>null = that side of the week is unrestricted.</summary>
    public Plan? Weekday { get; set; }
    public Plan? Weekend { get; set; }
    public DateTime LockUntilUtc { get; set; }
    /// <summary>Request-only: service turns this into LockUntilUtc using its trusted clock.</summary>
    public int DurationMinutes { get; set; }
    public double UsedSecondsToday { get; set; }
    public DateOnly UsageDate { get; set; }
}

public static class Rules
{
    static Plan? PlanFor(Rule r, DateOnly d) =>
        d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? r.Weekend : r.Weekday;

    /// <summary>The single definition of "locked". Returns when the current lock ends, or null if open.</summary>
    public static DateTime? LockedUntil(Rule r, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        switch (r.Mode)
        {
            case Mode.Duration:
                var until = DateTime.SpecifyKind(r.LockUntilUtc, DateTimeKind.Utc).ToLocalTime();
                return now < until ? until : null;

            case Mode.Allowance:
                var p = PlanFor(r, today);
                if (p == null) return null;
                var used = r.UsageDate == today ? r.UsedSecondsToday : 0;
                return used >= p.AllowanceMinutes * 60 ? today.AddDays(1).ToDateTime(TimeOnly.MinValue) : null;

            default: // Window. End <= Start means it crosses midnight; the day it starts on owns it.
                var t = TimeOnly.FromDateTime(now);
                var w = PlanFor(r, today);
                if (w != null)
                {
                    if (w.Start < w.End) { if (t >= w.Start && t < w.End) return today.ToDateTime(w.End); }
                    else if (t >= w.Start) return today.AddDays(1).ToDateTime(w.End);
                }
                var y = PlanFor(r, today.AddDays(-1));
                return y != null && y.End <= y.Start && t < y.End ? today.ToDateTime(y.End) : null;
        }
    }

    /// <summary>
    /// Longest unbroken lock this rule can produce, including windows that chain across days.
    /// Allowance usage isn't known in advance, so only 0-minute allowances (all-day locks) count.
    /// </summary>
    public static TimeSpan LongestLock(Rule r)
    {
        if (r.Mode == Mode.Duration) return TimeSpan.FromMinutes(r.DurationMinutes);
        var probe = new Rule { Mode = r.Mode, Weekday = r.Weekday, Weekend = r.Weekend }; // no usage recorded
        var t = new DateTime(2026, 1, 5); // a Monday; simulate two weeks minute by minute
        int run = 0, best = 0;
        for (int i = 0; i < 14 * 1440; i++, t = t.AddMinutes(1))
        {
            run = LockedUntil(probe, t) != null ? run + 1 : 0;
            best = Math.Max(best, run);
        }
        return TimeSpan.FromMinutes(best);
    }

    /// <summary>True if <paramref name="n"/> would make the lock weaker than <paramref name="o"/> in any way.</summary>
    public static bool IsLoosening(Rule o, Rule n)
    {
        if (o.Mode != n.Mode || !string.Equals(o.ExePath, n.ExePath, StringComparison.OrdinalIgnoreCase)) return true;
        return o.Mode switch
        {
            Mode.Duration => n.LockUntilUtc < o.LockUntilUtc,
            Mode.Allowance => Looser(o.Weekday, n.Weekday, (a, b) => b.AllowanceMinutes > a.AllowanceMinutes)
                           || Looser(o.Weekend, n.Weekend, (a, b) => b.AllowanceMinutes > a.AllowanceMinutes),
            _ => Looser(o.Weekday, n.Weekday, (a, b) => !Covers(b, a))
              || Looser(o.Weekend, n.Weekend, (a, b) => !Covers(b, a)),
        };
    }

    static bool Looser(Plan? o, Plan? n, Func<Plan, Plan, bool> weaker) => o != null && (n == null || weaker(o, n));

    static bool Covers(Plan outer, Plan inner)
    {
        var (s, e) = Span(inner);
        var (S, E) = Span(outer);
        return S <= s && E >= e;
    }

    static (int, int) Span(Plan p)
    {
        int s = p.Start.Hour * 60 + p.Start.Minute, e = p.End.Hour * 60 + p.End.Minute;
        return (s, e <= s ? e + 1440 : e);
    }

    static readonly string[] Protected =
        ["explorer.exe", "winlogon.exe", "csrss.exe", "svchost.exe", "dwm.exe", "taskmgr.exe",
         "lsass.exe", "services.exe", "lifelock.exe", "lifelock.service.exe"];

    /// <summary>Why this exe may not be locked, or null if it may. Stops you bricking your own session.</summary>
    public static string? DenyReason(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch { return "Invalid path."; }
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        if (full.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return "Windows system programs can't be locked.";
        if (Protected.Contains(Path.GetFileName(full).ToLowerInvariant())) return "This program is protected and can't be locked.";
        if (!File.Exists(full)) return "File not found.";
        return null;
    }

    public static string? Identity(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrWhiteSpace(v.OriginalFilename) ? null
                : $"{v.OriginalFilename}|{v.ProductName}|{v.CompanyName}".ToLowerInvariant();
        }
        catch { return null; }
    }
}

public class State
{
    public List<Rule> Rules { get; set; } = [];
    public DateTime LastUtc { get; set; }
    public long OffsetTicks { get; set; }
}

public static class Store
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LifeLock");
    static string FilePath => Path.Combine(Dir, "rules.json");

    public static State Load()
    {
        foreach (var f in new[] { FilePath, FilePath + ".bak" })
        {
            try { return JsonSerializer.Deserialize<State>(File.ReadAllText(f)) ?? new(); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException) { }
        }
        return new();
    }

    public static void Save(State s)
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s));
        if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak");
        else File.Move(tmp, FilePath);
    }

    public static void Log(string msg)
    {
        try { Directory.CreateDirectory(Dir); File.AppendAllText(Path.Combine(Dir, "log.txt"), $"{DateTime.Now:s} {msg}\n"); }
        catch { }
    }
}

public record Request(string Cmd, Rule? Rule = null, string? Id = null);
public record RuleView(Rule Rule, DateTime? LockedUntil);

public class Response
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public bool Offline { get; set; }
    public List<RuleView>? Rules { get; set; }
}

public static class Pipe
{
    public const string Name = "LifeLock";

    public static Response Send(Request q)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", Name, PipeDirection.InOut);
            c.Connect(3000);
            var w = new StreamWriter(c) { AutoFlush = true };
            w.WriteLine(JsonSerializer.Serialize(q));
            return JsonSerializer.Deserialize<Response>(new StreamReader(c).ReadLine()!)!;
        }
        catch (Exception e)
        {
            return new Response { Error = $"The LifeLock service isn't reachable ({e.Message}).", Offline = true };
        }
    }
}

/// <summary>Service security descriptors. Locked: Administrators can query/start but not stop, reconfigure or delete.</summary>
public static class Sddl
{
    public const string Locked = "D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCLCSWRPLOCRRC;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";
    public const string Open = "D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";
}
