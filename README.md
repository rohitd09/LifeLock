# LifeLock

App lock for Windows. Pick an installed app and lock it:

- **Timed lock**: locked for X hours/minutes starting now.
- **Daily allowance**: X minutes of use per day, then locked until midnight.
- **Time window**: locked from a start time to an end time. A window can cross midnight, e.g. 22:00–06:00.

Allowances and windows can be the same every day, or different for weekdays and weekends.

A locked app is closed the moment it starts, and you see a popup such as "Steam is locked until 18:00".

**Lockdown rules.** While an app is locked:
- its lock can't be removed
- its lock can only be made stricter (longer, wider or less allowance)
- LifeLock refuses to uninstall

Before saving a lock that would last **more than 16 hours in a row**, LifeLock asks you to confirm. This includes daily windows that join into one long lock, e.g. 07:00→07:00 every weekday.

---

## Requirements

| What | Why | When |
|---|---|---|
| **Windows 10 or 11, 64-bit** | LifeLock is Windows-only. | — |
| **Administrator account** (or its password) | The installer creates a Windows service. | At install and uninstall time only. |
| **.NET 8 SDK** (8.0.x, x64) | Builds the app from source. It includes the .NET 8 Desktop Runtime that LifeLock needs to run. | **Before step 2** below. |
| **Git** (optional) | Clones the repo. You can download the ZIP instead. | Before step 1, only if you use `git clone`. |

### Installing the requirements

1. **Git (optional).** Open PowerShell and run:
   ```powershell
   winget install --id Git.Git -e
   ```
   Or download it from https://git-scm.com/download/win. Close and reopen PowerShell afterwards.

2. **.NET 8 SDK.** Open PowerShell and run:
   ```powershell
   winget install --id Microsoft.DotNet.SDK.8 -e
   ```
   Or download **".NET 8.0 SDK – Windows x64 Installer"** from https://dotnet.microsoft.com/download/dotnet/8.0.
   Close and reopen PowerShell, then check that it worked:
   ```powershell
   dotnet --list-sdks     # should list 8.0.xxx
   ```

> Having only the .NET *Runtime* is not enough to build. `dotnet --list-sdks` must show an 8.0 SDK.

---

## Install LifeLock

Run these in **PowerShell** (a normal window, not "Run as administrator"):

```powershell
# 1. Get the code
git clone https://github.com/rohitd09/LifeLock.git
cd LifeLock

# 2. Build
dotnet publish src/App -c Release -o publish
dotnet publish src/Service -c Release -o publish

# 3. Install (a Windows admin prompt appears – click Yes)
.\publish\LifeLock.exe --install
```

The install does the following:
- copies everything to `C:\Program Files\LifeLock`
- starts the LifeLock service, which also starts automatically with Windows
- adds **LifeLock** to the Start Menu and to Settings → Apps

Open **LifeLock** from the Start Menu, pick an app and click **Lock selected app…**.

> **Recommended first test:** before adding any locks, uninstall once (Settings → Apps → LifeLock → Uninstall) and then reinstall. This confirms the uninstall path works on your machine while nothing can block it.

## Uninstall

Go to Settings → Apps → LifeLock → Uninstall. This only works when **no** lock is currently active.

## Updating to a new version

The installed files are protected, so updating means uninstalling and reinstalling:

1. In the cloned folder, run `git pull`.
2. Repeat step 2 (Build) above.
3. Uninstall the old version. This only works while no lock is active.
4. Run `.\publish\LifeLock.exe --install`.

## Emergency removal (admin)

Use this only if the uninstall path is broken or a lock was set by mistake. Run in an **Administrator** PowerShell:

```powershell
reg add HKLM\SYSTEM\CurrentControlSet\Services\LifeLock /v Start /t REG_DWORD /d 4 /f   # disable service
# reboot now, then:
reg delete HKLM\SYSTEM\CurrentControlSet\Services\LifeLock /f
reg delete HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LifeLock /f
# reboot again, then:
takeown /f "C:\Program Files\LifeLock" /r /d y; icacls "C:\Program Files\LifeLock" /grant Administrators:F /t
takeown /f "C:\ProgramData\LifeLock" /r /d y;   icacls "C:\ProgramData\LifeLock" /grant Administrators:F /t
Remove-Item -Recurse -Force "C:\Program Files\LifeLock", "C:\ProgramData\LifeLock"
Remove-Item "C:\ProgramData\Microsoft\Windows\Start Menu\Programs\LifeLock.lnk" -ErrorAction SilentlyContinue
```

---

## How it works

| Piece | Role |
|---|---|
| `src/Core` | Rule model and `Rules.LockedUntil`, the single definition of "locked". Also loosening detection, the >16h check (`LongestLock`), the denylist and the JSON store. |
| `src/Service` | Windows service running as SYSTEM. Every second it scans processes and kills locked apps, matched by path and by version info so renamed copies are caught too. It counts allowance usage, owns `C:\ProgramData\LifeLock\rules.json`, and serves the `LifeLock` named pipe. |
| `src/App` | WinForms UI. It lists Start Menu apps (with a "Browse for .exe" fallback) and edits rules over the pipe. It also handles `--install` and `--uninstall`. |
| `tests/Check` | `dotnet run --project tests/Check` runs assert checks on the lock logic. |

Tamper resistance:
- The service can't be stopped (`CanStop=false`) and is restarted on failure.
- Its security descriptor denies Administrators stop, reconfigure and delete.
- The program and data folders are writable only by SYSTEM.
- Changes to the system clock are detected and compensated for.
- Windows system programs and LifeLock itself can't be locked.

To run without installing (for development): start `publish\LifeLock.Service.exe --console`, then run `publish\LifeLock.exe`.

## Known limits

This stops impulsive bypassing, not a determined administrator. Safe Mode, taking ownership of the files, or editing the service registry key all get around it. Using a standard (non-admin) Windows account for daily use closes most of that gap.

- Store/UWP apps don't appear in the list. Use "Browse for .exe".
- Detection runs every second, so a locked app may flash on screen briefly before it closes.

---

## For AI agents (setup quick-ref)

```
GOAL: build + install LifeLock for the user on Windows 10/11 x64.
PREREQ CHECK: `dotnet --list-sdks` must show 8.0.x. If missing:
  winget install --id Microsoft.DotNet.SDK.8 -e --accept-source-agreements --accept-package-agreements
  (then call "C:\Program Files\dotnet\dotnet.exe" directly, or open a new shell, since PATH won't refresh)
CLONE: git clone https://github.com/rohitd09/LifeLock.git && cd LifeLock   (or use an existing checkout)
VERIFY LOGIC: dotnet run --project tests/Check   -> must end with "All checks passed."
BUILD: dotnet publish src/App -c Release -o publish
       dotnet publish src/Service -c Release -o publish
INSTALL: publish\LifeLock.exe --install   (self-elevates; USER must click Yes on the UAC prompt)
VERIFY INSTALL: `sc query LifeLock` -> STATE RUNNING
DO NOT:
  - run --install without asking the user. It creates a SYSTEM service that admins can't stop.
  - create test locks on real apps. Locks can't be removed until they expire.
  - use `sc stop` / `sc delete`; access is denied by design. Uninstall = `"C:\Program Files\LifeLock\LifeLock.exe" --uninstall` (refused while any lock is active).
STUCK / BROKEN INSTALL: see "Emergency removal (admin)" above; needs reboots, so confirm with the user first.
DEV TEST WITHOUT INSTALL: publish\LifeLock.Service.exe --console, then publish\LifeLock.exe (pipe name: LifeLock).
STATE: C:\ProgramData\LifeLock\rules.json (SYSTEM-only), log at C:\ProgramData\LifeLock\log.txt.
```
