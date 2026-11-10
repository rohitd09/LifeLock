# LifeLock

App lock for Windows. Pick an installed app and lock it:

- **Timed lock**: locked for X hours/minutes starting now.
- **Daily allowance**: X minutes of use per day, then locked until midnight.
- **Time window**: locked from a start time to an end time. The window can cross midnight, e.g. 22:00–06:00.

Allowances and windows can be set the same for every day, or separately for weekdays and weekends.

A locked app is closed as soon as it starts, and you get a popup such as "Steam is locked until 18:00".

**Lockdown rules.** While an app is locked:
- its lock can't be removed
- its lock can only be made stricter: longer, wider or with less allowance
- LifeLock refuses to uninstall

## Build & install

```sh
dotnet publish src/App -c Release -o publish
dotnet publish src/Service -c Release -o publish
publish\LifeLock.exe --install     # asks for admin; installs to C:\Program Files\LifeLock
```

Then open **LifeLock** from the Start Menu. To uninstall, use Settings → Apps → LifeLock. While any lockdown is active, the uninstall is refused.

## How it works

| Piece | Role |
|---|---|
| `src/Core` | Rule model and `Rules.LockedUntil`, the single definition of "locked". Also loosening detection, the denylist and the JSON store. |
| `src/Service` | Windows service running as SYSTEM. Every second it scans processes and kills locked apps, matched by path and by version info, so renamed copies are caught too. It counts allowance usage, owns `C:\ProgramData\LifeLock\rules.json`, and serves the `LifeLock` named pipe. |
| `src/App` | WinForms UI. It lists Start Menu apps (with a "Browse for .exe" fallback) and edits rules over the pipe. It also handles `--install` and `--uninstall`. |
| `tests/Check` | `dotnet run --project tests/Check` runs assert checks on the lock logic. |

Tamper resistance:
- The service can't be stopped (`CanStop=false`) and is restarted on failure.
- Its security descriptor denies Administrators stop, reconfigure and delete.
- The program and data folders are writable only by SYSTEM.
- Changes to the system clock are detected and compensated for.
- Windows system programs and LifeLock itself can't be locked.

To run without installing (for development): start `LifeLock.Service.exe --console`, then run `LifeLock.exe`.

## Known limits

This stops impulsive bypassing, not a determined administrator. An admin can still get around it via Safe Mode, by taking ownership of the files, or by editing the service's registry key. Using a standard (non-admin) Windows account for daily use closes most of that gap.

- Store/UWP apps don't appear in the list. Use "Browse for .exe".
- Detection runs every second, so a locked app may flash on screen briefly before it closes.
