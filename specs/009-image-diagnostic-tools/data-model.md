# Data Model: Diagnostic Tools

All types are in `HddBackup.Core.Diagnostics` and are public, language-neutral DTOs: text is built
by the App (research R-10). No type has a field that can hold a secret (R-6).

## DiagnosticsSource

| Field | Type | Notes |
|-------|------|-------|
| `ImagePath` / `Device` | string / `DeviceInfo` | exactly one; opened read-only by `DiagnosticsRunner` |
| `PartitionNumber` | int | the ext4 partition open on the Files tab |
| `DisplayName` | string | e.g. "rootfs of raspberry-pi.img" for the window headers and reports |
| `State` | `FileSystemState` | feature 006/008 state, for the "not cleanly unmounted" warning |
| `HasUnreadBootPartition` | bool | the source also has a FAT partition (Q1, FR-009) |

## SystemReader (helper, not a DTO)

`Exists(path)`, `Stat(path)`, `ReadText(path, maxBytes)`, `ReadLines(path)`, `List(path)`,
`ReadLink(path)`, `Resolve(path)`; absolute image paths; links followed inside the image (≤ 40);
caches for the run; `Truncated`/`Unreadable` reported through the returned value, not exceptions.

## SSH check

### SshSettings (effective view)

| Field | Type | Notes |
|-------|------|-------|
| `Ports` | list of int | default 22 |
| `PermitRootLogin` | enum `Yes`, `ProhibitPassword`, `ForcedCommandsOnly`, `No` | |
| `PasswordAuthentication`, `KbdInteractive`, `UsePam`, `PubkeyAuthentication`, `PermitEmptyPasswords` | `SettingValue<bool>` | |
| `AuthorizedKeysFiles` | list of string | tokens expanded per account |
| `AllowUsers`, `DenyUsers`, `AllowGroups`, `DenyGroups` | list of pattern | |
| `AuthenticationMethods` | list of method lists | empty = any |
| `HasAuthorizedKeysCommand` | bool | |
| `ConditionalKeywords` | list of string | keywords that also occur inside `Match` blocks |

`SettingValue<T>`: `Value`, `Source` (`Default` or file + line), `Conditional` flag.

### ServerState

`Installed` (bool), `Unit` (name), `Enabled` (`Yes`, `No`, `Masked`, `OnDemand`, `Legacy`), `Evidence`
(paths of the link/unit/binary), `FirstBootSwitchPresent` (bool).

### Account

| Field | Type | Notes |
|-------|------|-------|
| `Name`, `Uid`, `Gid`, `Home`, `Shell` | | from passwd |
| `HasUsableShell` | bool | R-6 |
| `Password` | enum `Set`, `Locked`, `NoPassword`, `Empty`, `Unknown` | never the field itself |
| `HasExpiry` | bool | note only |
| `KeyCount` | int | valid key lines found in the authorized-keys files |
| `KeysUnknown` | bool | `AuthorizedKeysCommand` or an unreadable file |
| `CanLogInWithPassword`, `CanLogInWithKey` | enum `Yes`, `No`, `Unknown` | with `PasswordReason` / `KeyReason` codes |
| `IsSystemAccount` | bool | uid < 1000 and not root |

### Finding

| Field | Type | Notes |
|-------|------|-------|
| `Code` | `FindingCode` | one per statement (table below) |
| `Status` | enum `Ok`, `Problem`, `Unknown`, `Info` | glyph ✔ ✖ ⚠ ℹ |
| `Args` | list of string | user names, paths, values — data only |
| `Evidence` | list of string | image paths the finding rests on |
| `Page` | int | 1 Server, 2 Settings, 3 Accounts, 4 Result |

### Verdict

`Code` (`Works`, `NotEnabled`, `NotEnabledBootFlagPossible`, `NoServer`, `NobodyCanLogIn`,
`Undetermined`, `NotALinuxRoot`), `Users` (list of user and method), `Reasons` (list of finding
codes in fix-first order), `Notes` (list of finding codes: not checked / conditional).

### FindingCode (the text of each is a resource string `Ssh_F_<Code>`)

| Page | Code | Status | English text (placeholders in braces) |
|------|------|--------|----------------------------------------|
| 1 | `ServerInstalled` | Ok | The SSH server is installed ({0}). |
| 1 | `ServerMissing` | Problem | No SSH server is installed. |
| 1 | `ServerEnabled` | Ok | The server is set to start at boot ({0} is enabled). |
| 1 | `ServerOnDemand` | Ok | The server starts when someone connects ({0}). |
| 1 | `ServerNotEnabled` | Problem | The server is installed but not set to start at boot. |
| 1 | `ServerMasked` | Problem | The server is masked: it cannot start. |
| 1 | `ServerLegacy` | Info | Started by the old init scripts of this system ({0}). |
| 1 | `BootFlagNotRead` | Unknown | Raspberry Pi OS can switch SSH on at the first boot with a file on the FAT boot partition. This tool reads only the Linux file system and did not check that. |
| 2 | `Port` | Info | The server listens on port {0}. |
| 2 | `RootLogin` | Info/Problem | The administrator account {0}. |
| 2 | `PasswordAuthOn` / `PasswordAuthOff` | Ok / Problem | Password login is allowed / not allowed. |
| 2 | `KeyAuthOn` / `KeyAuthOff` | Ok / Problem | Key login is allowed / not allowed. |
| 2 | `EmptyPasswords` | Info | Accounts with an empty password {0}. |
| 2 | `UserRestriction` | Info | Login is restricted to {0}. |
| 2 | `SettingDefault` | Info | {0} is not set; the default is used. |
| 2 | `ConditionalSettings` | Unknown | {0} also appears in Match blocks; the result may differ for some users or addresses. |
| 2 | `KeysFromCommand` | Unknown | Keys may come from a command; they cannot be checked. |
| 3 | `AccountCanLogIn` | Ok | {0} can log in with {1}. |
| 3 | `AccountCannot` | Problem | {0} cannot log in: {1}. |
| 3 | `NoRegularAccount` | Info | No account other than the administrator exists; Raspberry Pi OS creates one at the first boot. |
| 3 | `ShadowUnreadable` | Unknown | Password information could not be read. |
| 4 | `Verdict…` | | see `VerdictCode`; one resource string per code |

Account "cannot" reasons (`{1}`): `NoShell`, `PasswordLocked`, `NoPasswordNoKey`, `RootNotAllowed`,
`NotInAllowUsers`, `Denied`, `PasswordAuthOffNoKey`, `KeyAuthOffNoPassword`, `EmptyPasswordRefused`,
`LockedPasswordRefusedByServer`, `MethodsNeedBoth`.

## Startup and scheduled tasks

### StartupEntry

| Field | Type | Notes |
|-------|------|-------|
| `Kind` | enum `Service`, `Timer`, `Socket`, `BootScript`, `Desktop`, `UserService`, `Cron` | `Cron` covers scheduled tasks |
| `Name` | string | unit, script, desktop entry or task name |
| `Command` | string | what it runs, verbatim from the image |
| `Schedule` | `CronSchedule` | cron entries and timers; `AtBoot` for boot-time starts |
| `ScheduleText` | string | the original schedule text (`OnCalendar=`, the five fields) |
| `User` | string | empty when unknown |
| `Source` | string | file or target the entry comes from (`multi-user.target`, `/etc/crontab`) |
| `Active` | bool | true for the main list |
| `InactiveReason` | enum | `Disabled`, `Masked`, `UnitMissing`, `TargetNotReached`, `HiddenEntry`, `NotExecutable`, `IgnoredName`, `NothingRunsIt`, `NoSuchUser`, `NoCron`, `Malformed`, `Unreadable` |
| `Evidence` | list of string | paths |

### CronSchedule

`Class` (`AtBoot`, `EveryMinute`, `EveryNMinutes`, `HourlyAt`, `DailyAt`, `WeeklyOn`, `MonthlyOn`,
`YearlyOn`, `Custom`), `Minutes`/`Hours`/`DaysOfMonth`/`Months`/`DaysOfWeek` (sets), `Times` (list of
time of day, for the classes that have them), `Raw` (original text).

### StartupResult

`Entries` (list of `StartupEntry`, active first), `Counts` per kind and active/inactive, `Notes`
(codes: cron missing, list cut, truncated file, unreadable source), `Source`.

## Validation rules (from the spec)

| Rule | Where | Spec |
|------|-------|------|
| no secret in any result type | types hold states and counts only; parsers drop the strings | FR-010, SC-004 |
| nothing written | `SystemReader` has no write path; tools run on a read-only source | FR-003, SC-005 |
| every "no" has a reason | `Account` reason codes are required when a login answer is `No` | FR-007 |
| no "SSH is off" when something unread could matter | verdict `NotEnabledBootFlagPossible` | FR-009 |
| inactive entries kept out of the main list | `Active` false until the user asks | FR-013 |
| one unreadable entry does not stop the list | `Unreadable` inactive entry | FR-014 |
