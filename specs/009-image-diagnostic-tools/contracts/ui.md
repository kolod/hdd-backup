# UI Contract: Tools Button, SSH Check, Startup and Scheduled Tasks

Draft mockups: [spec.md](../spec.md#layout-mockup-draft-to-be-approved). Extends the Files tab of
feature 008; the menu, tabs and operation panel are otherwise unchanged.

## Tools button (Files tab)

| Item | Behavior |
|------|----------|
| **Tools ▾** (left of **Extract…**) | click shows a context menu under the button; tooltip `Tooltip_Tools` |
| **SSH check…** | starts the wizard (`Tools_Ssh`) |
| **Startup and scheduled tasks…** | opens the list window (`Tools_Startup`) |
| Enabled when | a file system is open, no extraction and no other tool is running |
| Disabled when | nothing is open, the source cannot be read, an extraction runs; the tooltip says why |

## Starting a tool

1. The button area shows `Tools_Analysing` ("Analysing…") with **Cancel** while `DiagnosticsRunner`
   works (usually 1–3 s); the tab stays usable except for **Extract…** and **Tools**.
2. The window opens with the result; a read error shows `Files_ReadError` and no window.
3. Both windows are modal over the main window, close with **Close** or Esc.

## SSH check wizard

| Page | Content |
|------|---------|
| Header | `Ssh_Title` "SSH check — {0}" ({0} = source name); under it the not-cleanly-unmounted warning (`Files_StateNeedsRepair`) when it applies; the step list "1 Server · 2 Settings · 3 Accounts · 4 Result", current bold |
| 1 Server | findings `ServerInstalled/Missing/Enabled/OnDemand/NotEnabled/Masked/Legacy` and `BootFlagNotRead`, each with a glyph ✔ ✖ ⚠ ℹ and its evidence paths in grey below |
| 2 Settings | findings for port, administrator login, password login, key login, empty passwords, restrictions, defaults, conditional blocks; each says where the value comes from ("/etc/ssh/sshd_config.d/50-x.conf, line 3" or "default") |
| 3 Accounts | a `ListView`: Account, Shell, Password (set / locked / none / empty), Keys (count), Can log in (password / key / no + reason); check box "Show system accounts" (off by default) |
| 4 Result | the verdict in a large line (`Ssh_V_<Code>`), then "Why" (the reasons, fix-first order), "Could not check" (the notes), and **Copy** / **Save as…** |
| Buttons | **◂ Back** (from page 2), **Next ▸** (to page 4; becomes disabled on 4), **Close**; Enter = Next, Esc = Close |
| Not a Linux root | the first page shows `Ssh_NotLinux` and only **Close** is enabled |

Verdict colors: Works = green banner, NotEnabled / NobodyCanLogIn / NoServer = red,
NotEnabledBootFlagPossible / Undetermined = amber. Colors are never the only signal (glyph + text).

## Startup and scheduled tasks window

| Control | Behavior |
|---------|----------|
| Kind filter | All, Services, Timers, Boot scripts, Desktop session, Cron |
| Search | filters rows whose name, command, user or source contains the text (case-insensitive) |
| "Only what is active" | checked by default; unchecked adds inactive entries, greyed, with the reason in **Status** |
| List | columns Kind, Name, What it runs / When, User, Source, Status; sortable by clicking a header; schedule shown in words, original text in the tooltip |
| Count line | "{0} entries ({1} inactive)" or with a filter "{0} of {1} entries" |
| **Copy** / **Save as…** | the visible rows as tab-separated text; Save as… asks before replacing a file |
| Notes | below the list, e.g. "Cron is not installed on this system.", "The list of {0} was cut at 5 000 entries." |

## Strings (English; all six languages required)

Keys are grouped; the English text of the finding, verdict, schedule-word and reason keys is in
[data-model.md](../data-model.md) (finding table) and below. Placeholders are `{0}…`.

| Group | Keys |
|-------|------|
| Button and menu | `Tools_Button` "Tools", `Tools_Ssh` "SSH check…", `Tools_Startup` "Startup and scheduled tasks…", `Tooltip_Tools` "Tools that read the opened Linux system and report what they find. Nothing is changed.", `Tools_Analysing` "Analysing…" |
| Wizard chrome | `Ssh_Title` "SSH check — {0}", `Ssh_Step1..4` "Server", "Settings", "Accounts", "Result", `Ssh_Back` "◂ Back", `Ssh_Next` "Next ▸", `Ssh_NotLinux` "No system configuration was found on this partition. It is probably not the root of a Linux system.", `Ssh_ShowSystemAccounts` "Show system accounts", `Ssh_Why` "Why", `Ssh_CouldNotCheck` "Could not check", `Ssh_SettingFrom` "{0}, line {1}", `Ssh_FromDefault` "default" |
| Findings | `Ssh_F_<Code>` for each code of the finding table (≈ 25) |
| Account columns | `Ssh_ColAccount`, `Ssh_ColShell`, `Ssh_ColPassword`, `Ssh_ColKeys`, `Ssh_ColCanLogIn`; values `Ssh_Pw_Set` "set", `Ssh_Pw_Locked` "locked", `Ssh_Pw_NoPassword` "none", `Ssh_Pw_Empty` "empty", `Ssh_Pw_Unknown` "unknown"; `Ssh_With_Password` "a password", `Ssh_With_Key` "a key", `Ssh_With_Both` "a password and a key" |
| Account reasons | `Ssh_R_NoShell` "it has no login shell", `Ssh_R_PasswordLocked` "its password is locked", `Ssh_R_NoPasswordNoKey` "it has no password and no authorized key", `Ssh_R_RootNotAllowed` "the server does not allow the administrator account to log in", `Ssh_R_NotInAllowUsers` "it is not in the list of allowed users", `Ssh_R_Denied` "it is denied by the server settings", `Ssh_R_PasswordAuthOffNoKey` "password login is off and it has no authorized key", `Ssh_R_KeyAuthOffNoPassword` "key login is off and it has no usable password", `Ssh_R_EmptyPasswordRefused` "its password is empty and the server refuses empty passwords", `Ssh_R_LockedRefused` "the server refuses locked accounts", `Ssh_R_MethodsNeedBoth` "the server requires a password and a key together" |
| Verdicts | `Ssh_V_Works` "SSH should work: {0}.", `Ssh_V_NotEnabled` "SSH will not start by itself: the server is not set to start at boot.", `Ssh_V_NotEnabledBootFlagPossible` "SSH is not enabled in the Linux system. It may still be switched on at the first boot by a file on the boot partition, which was not checked.", `Ssh_V_NoServer` "No SSH server is installed.", `Ssh_V_NobodyCanLogIn` "The server is set up, but nobody can log in.", `Ssh_V_Undetermined` "Cannot be determined: {0}." |
| Startup window | `Startup_Title` "Startup and scheduled tasks — {0}", `Startup_Filter` "Show", `Startup_All` "All", `Startup_KindService` "Services", `Startup_KindTimer` "Timers", `Startup_KindBoot` "Boot scripts", `Startup_KindDesktop` "Desktop session", `Startup_KindCron` "Cron", `Startup_Search` "Search", `Startup_OnlyActive` "Only what is active", `Startup_Counts` "{0} entries ({1} inactive)", `Startup_CountsFiltered` "{0} of {1} entries", `Startup_ColKind`, `Startup_ColName`, `Startup_ColRuns` "What it runs / When", `Startup_ColUser`, `Startup_ColSource`, `Startup_ColStatus`, `Startup_Active` "active" |
| Inactive reasons | `Startup_R_Disabled`, `Startup_R_Masked`, `Startup_R_UnitMissing`, `Startup_R_TargetNotReached`, `Startup_R_Hidden`, `Startup_R_NotExecutable`, `Startup_R_IgnoredName`, `Startup_R_NothingRunsIt`, `Startup_R_NoSuchUser`, `Startup_R_NoCron`, `Startup_R_Malformed`, `Startup_R_Unreadable` |
| Schedule words | `Sched_AtBoot` "at boot", `Sched_EveryMinute` "every minute", `Sched_EveryNMinutes` "every {0} minutes", `Sched_HourlyAt` "every hour at :{0}", `Sched_DailyAt` "every day at {0}", `Sched_WeeklyOn` "every {0} at {1}", `Sched_MonthlyOn` "on day {0} of every month at {1}", `Sched_YearlyOn` "every year on {0} at {1}", `Sched_Custom` "on the schedule {0}", `Sched_TimeList` "{0} and {1}" |
| Notes | `Startup_N_NoCron` "Cron is not installed on this system.", `Startup_N_CronNotEnabled` "The cron service is not enabled; scheduled tasks run only when it is started.", `Startup_N_ListCut` "The list of {0} was cut at {1} entries.", `Startup_N_Truncated` "{0} is larger than 1 MiB and was read only in part." |
| Report | `Report_Copy` "Copy", `Report_Save` "Save as…", `Report_Header` "{0} — {1}" (tool, source), `Report_Date` "Date: {0}", `Report_Saved` "Saved to {0}" |

Texts are short; explanations are in the findings themselves, and the tooltips repeat the longer
ones, so that German, French and Japanese do not clip (checked with the harness at 100%).

## Behavior notes

- Neither window has a button that changes the image; advice is words ("set `PasswordAuthentication
  yes` in /etc/ssh/sshd_config", "create an empty file named ssh on the boot partition") and is
  marked as advice, not as an action.
- Command lines are shown as they are in the image; the window and the README say so.
- A language switch re-renders an open window (it keeps the structured result).
