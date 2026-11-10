using LifeLock.Core;

namespace LifeLock.App;

sealed class RuleForm : Form
{
    readonly RadioButton modeDuration = new() { Text = "Lock for a set time (starting now)", AutoSize = true };
    readonly RadioButton modeAllowance = new() { Text = "Daily usage allowance (locked once used up, until midnight)", AutoSize = true };
    readonly RadioButton modeWindow = new() { Text = "Lock during a time window (start → end)", AutoSize = true };
    readonly NumericUpDown hours = new() { Maximum = 24 * 60, Width = 70 };
    readonly NumericUpDown minutes = new() { Maximum = 59, Width = 60 };
    readonly RadioButton sameDays = new() { Text = "Same every day", AutoSize = true, Checked = true };
    readonly RadioButton splitDays = new() { Text = "Weekdays and weekends separately", AutoSize = true };
    readonly FlowLayoutPanel durationRow, daysRow;
    readonly PlanRow row1 = new("Every day"), row2 = new("Weekends (Sat–Sun)");
    readonly Rule original;

    public Rule Result { get; private set; } = new();

    public RuleForm(Rule r, DateTime? lockedUntil)
    {
        original = r;
        Text = $"Lock {r.DisplayName}";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var modes = Column(modeDuration, modeAllowance, modeWindow);
        durationRow = Row(new Label { Text = "Lock for", AutoSize = true, Anchor = AnchorStyles.Left }, hours,
            new Label { Text = "hours", AutoSize = true, Anchor = AnchorStyles.Left }, minutes,
            new Label { Text = "minutes", AutoSize = true, Anchor = AnchorStyles.Left });
        daysRow = Row(sameDays, splitDays);
        var ok = new Button { Text = "Save lock", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        AcceptButton = ok;
        CancelButton = cancel;

        var body = Column(
            new Label { Text = r.Folder != null ? $"Game folder: {r.Folder}" : r.ExePath, AutoSize = true, ForeColor = SystemColors.GrayText },
            modes, durationRow, daysRow, row1, row2);
        if (lockedUntil != null)
            body.Controls.Add(new Label { Text = $"🔒 On lockdown until {lockedUntil:ddd HH:mm} — you can only make it stricter.", AutoSize = true, ForeColor = Color.Firebrick });
        body.Controls.Add(Row(ok, cancel));
        Controls.Add(body);

        // Load existing values.
        (r.Mode switch { Mode.Allowance => modeAllowance, Mode.Window => modeWindow, _ => modeDuration }).Checked = true;
        if (r.Mode == Mode.Duration && lockedUntil is { } u)
        {
            var total = Math.Max(1, (int)Math.Ceiling((u - DateTime.Now).TotalMinutes));
            hours.Value = Math.Min(hours.Maximum, total / 60);
            minutes.Value = total % 60;
        }
        else if (r.Mode == Mode.Duration) hours.Value = 1;
        splitDays.Checked = r.Split;
        row1.Load(r.Weekday);
        row2.Load(r.Split ? r.Weekend : null);

        foreach (var rb in new[] { modeDuration, modeAllowance, modeWindow, sameDays, splitDays })
            rb.CheckedChanged += (_, _) => Sync();
        ok.Click += (_, _) => { if (!Build()) DialogResult = DialogResult.None; };
        Sync();
    }

    Mode SelectedMode => modeAllowance.Checked ? Mode.Allowance : modeWindow.Checked ? Mode.Window : Mode.Duration;

    void Sync()
    {
        var m = SelectedMode;
        durationRow.Visible = m == Mode.Duration;
        daysRow.Visible = row1.Visible = m != Mode.Duration;
        row2.Visible = m != Mode.Duration && splitDays.Checked;
        row1.SetLabel(splitDays.Checked ? "Weekdays (Mon–Fri)" : "Every day");
        row1.SetMode(m);
        row2.SetMode(m);
    }

    bool Build()
    {
        var m = SelectedMode;
        var r = new Rule { Id = original.Id, ExePath = original.ExePath, Folder = original.Folder, DisplayName = original.DisplayName, Mode = m, Split = splitDays.Checked };
        if (m == Mode.Duration)
        {
            r.DurationMinutes = (int)hours.Value * 60 + (int)minutes.Value;
            if (r.DurationMinutes <= 0) return Warn("Pick how long to lock for.");
        }
        else
        {
            r.Weekday = row1.Plan(m);
            r.Weekend = splitDays.Checked ? row2.Plan(m) : r.Weekday;
            if (r.Weekday == null && r.Weekend == null) return Warn("Tick at least one row to restrict.");
            if (m == Mode.Window && new[] { r.Weekday, r.Weekend }.Any(p => p != null && p.Start == p.End))
                return Warn("Start and end time are the same. Pick different times.");
        }
        var longest = Rules.LongestLock(r);
        if (longest > TimeSpan.FromHours(16))
        {
            var how = longest >= TimeSpan.FromDays(7) ? "with no break at all — it never unlocks"
                : $"for up to {(longest.Days > 0 ? $"{longest.Days} day(s) " : "")}{longest.Hours}h {longest.Minutes}m in a row";
            var answer = MessageBox.Show(this,
                $"This will lock {original.DisplayName} {how}.\n\n" +
                "While it's locked you can't remove the lock, loosen it, or uninstall LifeLock.\n\nLock anyway?",
                "Long lock", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return false;
        }
        Result = r;
        return true;
    }

    bool Warn(string msg)
    {
        MessageBox.Show(this, msg, "LifeLock");
        return false;
    }

    static FlowLayoutPanel Column(params Control[] cs)
    {
        var f = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };
        f.Controls.AddRange(cs);
        return f;
    }

    static FlowLayoutPanel Row(params Control[] cs)
    {
        var f = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };
        f.Controls.AddRange(cs);
        return f;
    }

    /// <summary>One "kind of day" row: [x] label, From/To pickers (window) or minutes (allowance).</summary>
    sealed class PlanRow : FlowLayoutPanel
    {
        readonly CheckBox on = new() { Checked = true, Width = 180, Anchor = AnchorStyles.Left };
        readonly Label fromLbl = new() { Text = "from", AutoSize = true, Anchor = AnchorStyles.Left };
        readonly Label toLbl = new() { Text = "to", AutoSize = true, Anchor = AnchorStyles.Left };
        readonly DateTimePicker from = Picker(18), to = Picker(22);
        readonly NumericUpDown mins = new() { Minimum = 0, Maximum = 1440, Value = 60, Width = 70 };
        readonly Label minsLbl = new() { Text = "minutes per day", AutoSize = true, Anchor = AnchorStyles.Left };

        public PlanRow(string label)
        {
            AutoSize = true;
            WrapContents = false;
            on.Text = label;
            Controls.AddRange([on, fromLbl, from, toLbl, to, mins, minsLbl]);
            on.CheckedChanged += (_, _) => from.Enabled = to.Enabled = mins.Enabled = on.Checked;
        }

        static DateTimePicker Picker(int hour) => new()
        {
            Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 80,
            Value = DateTime.Today.AddHours(hour),
        };

        public void SetLabel(string text) => on.Text = text;

        public void SetMode(Mode m)
        {
            fromLbl.Visible = from.Visible = toLbl.Visible = to.Visible = m == Mode.Window;
            mins.Visible = minsLbl.Visible = m == Mode.Allowance;
        }

        public void Load(Plan? p)
        {
            if (p == null) return;
            from.Value = DateTime.Today.Add(p.Start.ToTimeSpan());
            to.Value = DateTime.Today.Add(p.End.ToTimeSpan());
            mins.Value = Math.Clamp(p.AllowanceMinutes, 0, 1440);
        }

        public Plan? Plan(Mode m) => !on.Checked ? null : new Plan
        {
            Start = TimeOnly.FromDateTime(from.Value),
            End = TimeOnly.FromDateTime(to.Value),
            AllowanceMinutes = m == Mode.Allowance ? (int)mins.Value : 0,
        };
    }
}
