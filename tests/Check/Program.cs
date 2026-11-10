using LifeLock.Core;

static void Eq(object? want, object? got, string what)
{
    if (!Equals(want, got)) throw new Exception($"FAIL {what}: want {want}, got {got}");
    Console.WriteLine($"ok   {what}");
}

static DateTime At(int day, int h, int m = 0) => new(2026, 11, day, h, m, 0, DateTimeKind.Local); // 13 = Friday

// Window crossing midnight, weekdays only: Fri 22:00 -> Sat 06:00 is owned by Friday.
var night = new Rule { Mode = Mode.Window, Split = true,
    Weekday = new Plan { Start = new(22, 0), End = new(6, 0) }, Weekend = null };
Eq(At(14, 6), Rules.LockedUntil(night, At(13, 23)), "Fri 23:00 locked until Sat 06:00");
Eq(At(14, 6), Rules.LockedUntil(night, At(14, 5)), "Sat 05:00 still locked (Friday owns it)");
Eq(null, Rules.LockedUntil(night, At(14, 7)), "Sat 07:00 open");
Eq(null, Rules.LockedUntil(night, At(14, 23)), "Sat 23:00 open (weekend unrestricted)");
Eq(null, Rules.LockedUntil(night, At(16, 5)), "Mon 05:00 open (Sunday unrestricted)");
Eq(At(17, 6), Rules.LockedUntil(night, At(16, 22)), "Mon 22:00 locked");

// Same-day window, split weekday/weekend.
var split = new Rule { Mode = Mode.Window, Split = true,
    Weekday = new Plan { Start = new(9, 0), End = new(17, 0) },
    Weekend = new Plan { Start = new(12, 0), End = new(14, 0) } };
Eq(At(13, 17), Rules.LockedUntil(split, At(13, 10)), "Fri 10:00 locked (weekday 9-17)");
Eq(null, Rules.LockedUntil(split, At(14, 10)), "Sat 10:00 open (weekend 12-14)");
Eq(At(14, 14), Rules.LockedUntil(split, At(14, 13)), "Sat 13:00 locked");

// Allowance: exhausted today -> locked to midnight; yesterday's usage doesn't count.
var allow = new Rule { Mode = Mode.Allowance,
    Weekday = new Plan { AllowanceMinutes = 30 }, Weekend = new Plan { AllowanceMinutes = 30 },
    UsageDate = new DateOnly(2026, 11, 13), UsedSecondsToday = 1800 };
Eq(At(14, 0), Rules.LockedUntil(allow, At(13, 15)), "allowance used up -> locked till midnight");
Eq(null, Rules.LockedUntil(allow, At(14, 9)), "next day resets");
allow.UsedSecondsToday = 1799;
Eq(null, Rules.LockedUntil(allow, At(13, 15)), "1s left -> open");

// Duration.
var dur = new Rule { Mode = Mode.Duration, LockUntilUtc = At(13, 18).ToUniversalTime() };
Eq(At(13, 18), Rules.LockedUntil(dur, At(13, 17)), "duration locked");
Eq(null, Rules.LockedUntil(dur, At(13, 18)), "duration expired");

// Loosening.
Rule W(int s, int e) => new() { Mode = Mode.Window, Weekday = new Plan { Start = new(s, 0), End = new(e, 0) } };
Eq(false, Rules.IsLoosening(W(22, 6), W(21, 7)), "wider window is not loosening");
Eq(true, Rules.IsLoosening(W(22, 6), W(23, 6)), "narrower window is loosening");
Eq(true, Rules.IsLoosening(W(22, 6), new Rule { Mode = Mode.Window }), "dropping weekday side is loosening");
Eq(true, Rules.IsLoosening(W(22, 6), new Rule { Mode = Mode.Duration }), "mode change is loosening");
Eq(true, Rules.IsLoosening(dur, new Rule { Mode = Mode.Duration, LockUntilUtc = dur.LockUntilUtc.AddMinutes(-1) }), "shorter duration is loosening");
Eq(false, Rules.IsLoosening(dur, new Rule { Mode = Mode.Duration, LockUntilUtc = dur.LockUntilUtc.AddHours(1) }), "longer duration is fine");
var more = new Rule { Mode = Mode.Allowance, Weekday = new Plan { AllowanceMinutes = 31 }, Weekend = new Plan { AllowanceMinutes = 30 } };
Eq(true, Rules.IsLoosening(allow, more), "more allowance is loosening");

// Denylist.
Eq(true, Rules.DenyReason(@"C:\Windows\explorer.exe") != null, "explorer denied");
Eq(true, Rules.DenyReason(@"C:\Windows\System32\notepad.exe") != null, "System32 denied");

Console.WriteLine("All checks passed.");
