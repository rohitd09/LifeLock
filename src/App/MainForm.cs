using LifeLock.Core;

namespace LifeLock.App;

record AppEntry(string Name, string Path);

sealed class MainForm : Form
{
    readonly TextBox search = new() { PlaceholderText = "Search apps and games…", Dock = DockStyle.Top };
    readonly ListView apps = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.None };
    readonly ListView locks = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, Dock = DockStyle.Fill };
    readonly ImageList icons = new() { ImageSize = new Size(20, 20), ColorDepth = ColorDepth.Depth32Bit };
    readonly Label status = new() { Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0) };
    readonly Button addBtn = new() { Text = "Lock selected app…", AutoSize = true };
    readonly Button browseBtn = new() { Text = "Browse for .exe…", AutoSize = true };
    readonly ListView games = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, Dock = DockStyle.Fill };
    readonly Label gameStatus = new() { Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText };
    readonly Button lockGameBtn = new() { Text = "Lock selected game…", AutoSize = true };
    readonly Button browseFolderBtn = new() { Text = "Browse for folder…", AutoSize = true };
    readonly Button rescanBtn = new() { Text = "Rescan", AutoSize = true };
    readonly Button editBtn = new() { Text = "Edit…", AutoSize = true };
    readonly Button removeBtn = new() { Text = "Remove", AutoSize = true };
    List<AppEntry> all = [];
    List<GameEntry> allGames = [];
    List<RuleView> views = [];

    public MainForm()
    {
        Text = "LifeLock";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Font = new Font("Segoe UI", 9.5f);
        Size = new Size(1100, 640);
        StartPosition = FormStartPosition.CenterScreen;

        apps.Columns.Add("App", 380);
        apps.SmallImageList = icons;
        locks.Columns.Add("App", 180);
        locks.Columns.Add("Lock", 270);
        locks.Columns.Add("Status", 300);
        locks.SmallImageList = icons;
        games.Columns.Add("Game", 190);
        games.Columns.Add("Store", 110);
        games.Columns.Add("Folder", 320);
        games.SmallImageList = icons;
        games.ShowItemToolTips = locks.ShowItemToolTips = true;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Page("Apps", apps, Buttons(addBtn, browseBtn)));
        tabs.TabPages.Add(Page("Games", games, Buttons(lockGameBtn, browseFolderBtn, rescanBtn), gameStatus));

        var split = new SplitContainer { Dock = DockStyle.Fill };
        split.Panel1.Controls.Add(Group("1. Pick an app or game", tabs, search, null));
        split.Panel2.Controls.Add(Group("2. Locks", locks, null, Buttons(editBtn, removeBtn)));
        Controls.Add(split);
        Controls.Add(status);

        search.TextChanged += (_, _) => { FillApps(); FillGames(); };
        games.DoubleClick += (_, _) => LockSelectedGame();
        lockGameBtn.Click += (_, _) => LockSelectedGame();
        browseFolderBtn.Click += (_, _) => BrowseFolder();
        rescanBtn.Click += async (_, _) => await ScanGames();
        apps.DoubleClick += (_, _) => AddSelected();
        addBtn.Click += (_, _) => AddSelected();
        browseBtn.Click += (_, _) => Browse();
        locks.DoubleClick += (_, _) => EditSelected();
        editBtn.Click += (_, _) => EditSelected();
        removeBtn.Click += (_, _) => RemoveSelected();
        locks.SelectedIndexChanged += (_, _) => UpdateButtons();

        var timer = new System.Windows.Forms.Timer { Interval = 5000 };
        timer.Tick += async (_, _) => await RefreshLocks();
        timer.Start();

        Load += async (_, _) =>
        {
            split.SplitterDistance = 420;
            status.Text = "Scanning installed apps…";
            all = await Task.Run(ScanApps);
            FillApps();
            await RefreshLocks();
            allGames = GameScanner.LoadCache();
            FillGames();
            await ScanGames();
        };
    }

    static Control Group(string title, Control fill, Control? top, Control? bottom)
    {
        var g = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(8) };
        g.Controls.Add(fill);
        if (top != null) g.Controls.Add(top);
        if (bottom != null) g.Controls.Add(bottom);
        return g;
    }

    static TabPage Page(string title, Control fill, params Control[] bottom)
    {
        var p = new TabPage(title) { Padding = new Padding(4) };
        p.Controls.Add(fill);
        p.Controls.AddRange(bottom);
        return p;
    }

    static Control Buttons(params Button[] bs)
    {
        var f = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        f.Controls.AddRange(bs);
        return f;
    }

    /// <summary>Installed apps = Start Menu shortcuts that point at an .exe.</summary>
    static List<AppEntry> ScanApps()
    {
        var found = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var root in new[] { Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.StartMenu })
        {
            foreach (var lnk in Directory.EnumerateFiles(Environment.GetFolderPath(root), "*.lnk", opts))
            {
                try
                {
                    string target = shell.CreateShortcut(lnk).TargetPath;
                    var name = Path.GetFileNameWithoutExtension(lnk);
                    if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
                        && !Path.GetFileName(target).StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                        && Rules.DenyReason(target) == null)
                        found.TryAdd(target, new AppEntry(name, target));
                }
                catch { }
            }
        }
        return [.. found.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)];
    }

    string IconKey(string path)
    {
        if (!icons.Images.ContainsKey(path))
        {
            try { using var ic = Icon.ExtractAssociatedIcon(path); if (ic != null) icons.Images.Add(path, ic); }
            catch { }
        }
        return path;
    }

    void FillApps()
    {
        apps.BeginUpdate();
        apps.Items.Clear();
        foreach (var a in all.Where(a => a.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase)))
            apps.Items.Add(new ListViewItem(a.Name, IconKey(a.Path)) { Tag = a, ToolTipText = a.Path });
        apps.EndUpdate();
        status.Text = $"{all.Count} apps found.";
    }

    async Task RefreshLocks()
    {
        var r = await Task.Run(() => Pipe.Send(new Request("List")));
        if (!r.Ok) { status.Text = "⚠ " + r.Error; return; }
        var selected = Selected()?.Rule.Id;
        views = r.Rules!;
        locks.BeginUpdate();
        locks.Items.Clear();
        foreach (var v in views)
        {
            var item = new ListViewItem(v.Rule.DisplayName, IconKey(v.Rule.ExePath)) { Tag = v, ToolTipText = v.Rule.Folder ?? v.Rule.ExePath };
            item.SubItems.Add(Describe(v.Rule));
            item.SubItems.Add(Status(v));
            if (v.LockedUntil != null) item.ForeColor = Color.Firebrick;
            locks.Items.Add(item);
            item.Selected = v.Rule.Id == selected;
        }
        locks.EndUpdate();
        UpdateButtons();
    }

    static string Describe(Rule r)
    {
        if (r.Mode == Mode.Duration) return "Timed lock";
        string P(Plan? p) => p == null ? "no limit"
            : r.Mode == Mode.Window ? $"locked {p.Start:HH\\:mm}–{p.End:HH\\:mm}" : $"{p.AllowanceMinutes} min/day";
        return r.Split ? $"Weekdays: {P(r.Weekday)} · Weekends: {P(r.Weekend)}" : $"Every day: {P(r.Weekday)}";
    }

    static string Status(RuleView v)
    {
        if (v.LockedUntil is { } u)
        {
            var left = u - DateTime.Now;
            var span = left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes}m" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m";
            return $"🔒 Locked until {u:ddd HH:mm} ({span} left)";
        }
        if (v.Rule.Mode == Mode.Allowance && v.Rule.UsageDate == DateOnly.FromDateTime(DateTime.Now))
            return $"Open · {(int)(v.Rule.UsedSecondsToday / 60)} min used today";
        return "Open";
    }

    RuleView? Selected() => locks.SelectedItems.Count > 0 ? (RuleView)locks.SelectedItems[0].Tag! : null;

    void UpdateButtons()
    {
        var v = Selected();
        editBtn.Enabled = v != null;
        removeBtn.Enabled = v != null && v.LockedUntil == null;
    }

    void AddSelected()
    {
        if (apps.SelectedItems.Count == 0) { status.Text = "Select an app first."; return; }
        var a = (AppEntry)apps.SelectedItems[0].Tag!;
        Edit(new Rule { ExePath = a.Path, DisplayName = a.Name, Mode = Mode.Duration }, null);
    }

    void Browse()
    {
        using var d = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Choose a program to lock" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        if (Rules.DenyReason(d.FileName) is { } why) { MessageBox.Show(this, why, "LifeLock"); return; }
        Edit(new Rule { ExePath = d.FileName, DisplayName = Path.GetFileNameWithoutExtension(d.FileName), Mode = Mode.Duration }, null);
    }

    async Task ScanGames()
    {
        rescanBtn.Enabled = false;
        gameStatus.Text = "Scanning stores and drives for games…";
        var found = await Task.Run(GameScanner.Scan);
        GameScanner.SaveCache(found);
        allGames = found;
        FillGames();
        rescanBtn.Enabled = true;
    }

    void FillGames()
    {
        games.BeginUpdate();
        games.Items.Clear();
        foreach (var g in allGames.Where(g => g.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase)
                                           || g.Store.Contains(search.Text, StringComparison.OrdinalIgnoreCase)))
        {
            var item = new ListViewItem(g.Name, g.Exe == null ? "" : IconKey(g.Exe)) { Tag = g, ToolTipText = g.Folder };
            item.SubItems.Add(g.Store);
            item.SubItems.Add(g.Folder);
            games.Items.Add(item);
        }
        games.EndUpdate();
        gameStatus.Text = $"{allGames.Count} games found. Not listed? Use Browse for folder.";
    }

    void LockSelectedGame()
    {
        if (games.SelectedItems.Count == 0) { status.Text = "Select a game first."; return; }
        var g = (GameEntry)games.SelectedItems[0].Tag!;
        Edit(new Rule { Folder = g.Folder, ExePath = g.Exe ?? "", DisplayName = g.Name, Mode = Mode.Duration }, null);
    }

    void BrowseFolder()
    {
        using var d = new FolderBrowserDialog { Description = "Choose the game's install folder", UseDescriptionForTitle = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        if (Rules.DenyFolderReason(d.SelectedPath) is { } why) { MessageBox.Show(this, why, "LifeLock"); return; }
        var name = Path.GetFileName(d.SelectedPath.TrimEnd(Path.DirectorySeparatorChar));
        Edit(new Rule { Folder = d.SelectedPath, DisplayName = name, Mode = Mode.Duration }, null);
    }

    void EditSelected()
    {
        if (Selected() is { } v) Edit(v.Rule, v.LockedUntil);
    }

    async void Edit(Rule rule, DateTime? lockedUntil)
    {
        using var f = new RuleForm(rule, lockedUntil);
        if (f.ShowDialog(this) != DialogResult.OK) return;
        var r = await Task.Run(() => Pipe.Send(new Request("Upsert", f.Result)));
        if (!r.Ok) MessageBox.Show(this, r.Error, "LifeLock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        await RefreshLocks();
    }

    async void RemoveSelected()
    {
        if (Selected() is not { } v) return;
        if (MessageBox.Show(this, $"Remove the lock on {v.Rule.DisplayName}?", "LifeLock", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        var r = await Task.Run(() => Pipe.Send(new Request("Remove", Id: v.Rule.Id)));
        if (!r.Ok) MessageBox.Show(this, r.Error, "LifeLock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        await RefreshLocks();
    }
}
