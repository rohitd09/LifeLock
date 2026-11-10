using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LifeLock.Core;

public record GameEntry(string Name, string Store, string Folder, string? Exe);

/// <summary>
/// Finds installed games on any PC: store records (registry/config files, whichever drive they point at)
/// plus a marker-file scan of every fixed drive. Nothing machine-specific is hard-coded.
/// </summary>
public static class GameScanner
{
    const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
    public const int ScanDepth = 4;

    public static List<GameEntry> Scan()
    {
        var found = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
        void Add(Func<IEnumerable<GameEntry>> detector)
        {
            try
            {
                foreach (var g in detector())
                {
                    string dir;
                    try { dir = Path.GetFullPath(g.Folder).TrimEnd('\\'); } catch { continue; }
                    if (!IsNotAGame(g.Name) && Directory.Exists(dir) && Rules.DenyFolderReason(dir) == null)
                        found.TryAdd(dir, g with { Folder = dir }); // store records run first and win
                }
            }
            catch { /* one broken store must not hide the others */ }
        }

        Add(Steam); Add(Epic); Add(Ubisoft); Add(Gog); Add(RegistryInstallDirs); Add(Riot); Add(Xbox);
        foreach (var drive in FixedDrives()) Add(() => MarkerScan(drive, ScanDepth));

        return [.. found.Values
            .Select(g => g with { Exe = g.Exe != null && File.Exists(g.Exe) ? g.Exe : GuessExe(g.Folder) })
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Launchers and store services that carry the same markers as games.</summary>
    static readonly string[] NotGames =
        ["Epic Online Services", "Epic Games Launcher", "Ubisoft Game Launcher", "EA app", "EA Desktop", "Origin",
         "Battle.net", "Riot Client", "Steam", "GOG Galaxy", "Xbox", "Rockstar Games Launcher", "Launcher"];

    static bool IsNotAGame(string name) => NotGames.Any(n => name.Equals(n, OIC));

    // ---------- Store records ----------

    static IEnumerable<GameEntry> Steam()
    {
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is not string steam) yield break;
        steam = steam.Replace('/', '\\');
        var libs = Read(Path.Combine(steam, "steamapps", "libraryfolders.vdf")) is { } vdf ? SteamLibraries(vdf) : [];
        libs.Add(steam);
        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                if (Read(acf) is not { } text) continue;
                var (name, dir) = SteamManifest(text);
                if (name != null && dir != null && !IsSteamTool(name))
                    yield return new(name, "Steam", Path.Combine(apps, "common", dir), null);
            }
        }
    }

    public static List<string> SteamLibraries(string vdf) =>
        [.. Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace(@"\\", @"\"))];

    public static (string? Name, string? InstallDir) SteamManifest(string acf) => (Vdf(acf, "name"), Vdf(acf, "installdir"));

    static string? Vdf(string s, string key)
    {
        var m = Regex.Match(s, $"\"{key}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    static bool IsSteamTool(string n) =>
        n.Contains("Redistributable", OIC) || n.StartsWith("Steamworks", OIC) || n.StartsWith("Proton", OIC)
        || n.StartsWith("Steam Linux Runtime", OIC) || n.StartsWith("SteamVR", OIC) || n.Contains("Dedicated Server", OIC);

    static IEnumerable<GameEntry> Epic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;
        foreach (var item in Directory.EnumerateFiles(dir, "*.item"))
        {
            if (Json(Read(item)) is not { } j) continue;
            var name = Str(j, "DisplayName");
            var loc = Str(j, "InstallLocation");
            var exe = Str(j, "LaunchExecutable");
            if (name != null && loc != null && !string.IsNullOrEmpty(exe))
                yield return new(name, "Epic", loc, Path.Combine(loc, exe));
        }
    }

    static IEnumerable<GameEntry> Ubisoft()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
        if (k == null) yield break;
        foreach (var id in k.GetSubKeyNames())
        {
            using var g = k.OpenSubKey(id);
            if (g?.GetValue("InstallDir") is not string dir) continue;
            var name = Registry.GetValue(
                $@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Uplay Install {id}",
                "DisplayName", null) as string;
            yield return new(name ?? FolderName(dir), "Ubisoft", dir, null);
        }
    }

    static IEnumerable<GameEntry> Gog()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (k == null) yield break;
        foreach (var id in k.GetSubKeyNames())
        {
            using var g = k.OpenSubKey(id);
            if (g?.GetValue("path") is not string dir) continue;
            yield return new(g.GetValue("gameName") as string ?? FolderName(dir), "GOG", dir, g.GetValue("exe") as string);
        }
    }

    /// <summary>EA/Origin games are recorded as HKLM\SOFTWARE\WOW6432Node\&lt;EA publisher key&gt;\&lt;Game&gt; "Install Dir".</summary>
    static IEnumerable<GameEntry> RegistryInstallDirs()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node");
        if (root == null) yield break;
        foreach (var vendor in root.GetSubKeyNames())
        {
            // Only EA's publisher keys: other vendors use "Install Dir" for non-game software.
            if (!(vendor.Equals("EA", OIC) || vendor.StartsWith("EA ", OIC) || vendor.Contains("Electronic Arts", OIC)
                  || vendor.StartsWith("Origin Games", OIC))) continue;
            RegistryKey? v;
            try { v = root.OpenSubKey(vendor); } catch { continue; }
            if (v == null) continue;
            using (v)
            {
                foreach (var game in v.GetSubKeyNames())
                {
                    string? dir;
                    try { using var g = v.OpenSubKey(game); dir = g?.GetValue("Install Dir") as string; } catch { continue; }
                    if (dir == null) continue;
                    yield return new(game, "EA", dir, null);
                }
            }
        }
    }

    static IEnumerable<GameEntry> Riot()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Riot Games", "RiotClientInstalls.json");
        if (Json(Read(file)) is not { } j || !j.TryGetProperty("associated_client", out var ac) || ac.ValueKind != JsonValueKind.Object)
            yield break;
        foreach (var p in ac.EnumerateObject())
        {
            var dir = p.Name.Replace('/', '\\').TrimEnd('\\');
            var name = FolderName(dir);
            if (name.Equals("live", OIC) || name.StartsWith("pbe", OIC)) name = FolderName(Path.GetDirectoryName(dir) ?? dir);
            yield return new(name, "Riot", dir, null);
        }
    }

    /// <summary>Microsoft Store / Xbox app games: each drive's .GamingRoot names its XboxGames folder.</summary>
    static IEnumerable<GameEntry> Xbox()
    {
        foreach (var drive in FixedDrives())
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(Path.Combine(drive, ".GamingRoot")); } catch { continue; }
            foreach (var rel in ParseGamingRoot(bytes))
            {
                var root = Path.Combine(drive, rel.TrimStart('\\'));
                if (!Directory.Exists(root)) continue;
                foreach (var game in Directory.EnumerateDirectories(root))
                {
                    var cfg = Path.Combine(game, "Content", "MicrosoftGame.config");
                    if (!File.Exists(cfg)) continue;
                    yield return new(XboxName(Read(cfg)) ?? FolderName(game), "Microsoft Store", game, null);
                }
            }
        }
    }

    /// <summary>.GamingRoot = "RGBX", uint32 count, then null-terminated UTF-16 paths relative to the drive root.</summary>
    public static List<string> ParseGamingRoot(byte[] b)
    {
        if (b.Length < 8 || Encoding.ASCII.GetString(b, 0, 4) != "RGBX") return [];
        var count = BitConverter.ToInt32(b, 4);
        var len = (b.Length - 8) / 2 * 2;
        return [.. Encoding.Unicode.GetString(b, 8, len).Split('\0', StringSplitOptions.RemoveEmptyEntries).Take(count)];
    }

    public static string? XboxName(string? config)
    {
        if (config == null) return null;
        var m = Regex.Match(config, "DefaultDisplayName=\"([^\"]+)\"");
        return m.Success && !m.Groups[1].Value.StartsWith("ms-resource", OIC) ? m.Groups[1].Value : null;
    }

    // ---------- Marker scan: games in any folder on any drive ----------

    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "Windows", "$Recycle.Bin", "System Volume Information", "AppData", "node_modules", ".git",
          "ProgramData", "Recovery", "PerfLogs", "$WinREAgent", "Config.Msi" };

    // ponytail: depth-limited walk; a game nested deeper with no store record needs "Browse for folder…".
    public static IEnumerable<GameEntry> MarkerScan(string root, int depth)
    {
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (dir, d) = stack.Pop();
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(dir).GetFileSystemInfos(); } catch { continue; }
            if (d > 0 && Marker(dir, entries) is { } store)
            {
                yield return new(FolderName(dir), store, dir, null);
                continue; // don't look for games inside a game
            }
            if (d == depth) continue;
            foreach (var e in entries)
                if (e is DirectoryInfo sub && !sub.Attributes.HasFlag(FileAttributes.ReparsePoint) && !SkipDirs.Contains(sub.Name))
                    stack.Push((sub.FullName, d + 1));
        }
    }

    static string? Marker(string dir, FileSystemInfo[] entries)
    {
        var names = entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Contains("__Installer") && File.Exists(Path.Combine(dir, "__Installer", "installerdata.xml"))) return "EA";
        if (names.Contains(".egstore")) return "Epic";
        if (names.Contains(".build.info")) return "Battle.net";
        if (names.Contains("uplay_install.state")) return "Ubisoft";
        if (names.Any(n => n.StartsWith("goggame-", OIC) && n.EndsWith(".info", OIC))) return "GOG";
        if (names.Contains("Content") && File.Exists(Path.Combine(dir, "Content", "MicrosoftGame.config"))) return "Microsoft Store";
        return null;
    }

    // ---------- Helpers ----------

    static IEnumerable<string> FixedDrives() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName);

    /// <summary>Best exe for the icon: the largest one up to 3 levels down, skipping installers and helpers.</summary>
    static string? GuessExe(string folder)
    {
        try
        {
            string[] junk = ["unins", "setup", "redist", "crash", "report", "helper", "vc_", "dxsetup", "installer", "touchup", "cleanup"];
            return Directory.EnumerateFiles(folder, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true })
                .Where(f => !junk.Any(j => Path.GetFileName(f).Contains(j, OIC))
                            && !f.Contains(@"\__Installer\", OIC) && !f.Contains(@"\_CommonRedist\", OIC))
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    static string FolderName(string dir) => Path.GetFileName(dir.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : dir;

    static string? Read(string path)
    {
        try { return File.ReadAllText(path); } catch { return null; }
    }

    static JsonElement? Json(string? text)
    {
        if (text == null) return null;
        try { return JsonDocument.Parse(text).RootElement; } catch { return null; }
    }

    static string? Str(JsonElement? j, string prop) =>
        j is { } e && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---------- Cache (per user) ----------

    static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LifeLock", "games.json");

    public static List<GameEntry> LoadCache()
    {
        try { return JsonSerializer.Deserialize<List<GameEntry>>(File.ReadAllText(CachePath)) ?? []; } catch { return []; }
    }

    public static void SaveCache(List<GameEntry> games)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(games));
        }
        catch { }
    }
}
