# Tests

```
dotnet test Rackside.slnx                              # all (about 10 minutes)
dotnet test Rackside.slnx --filter "Category!=Slow"    # without the IOS update simulators
```

Everything generated goes to `tests/out/dotnet` (ignored by git).

## Requirements

- .NET 10 SDK.
- For the simulators: Python 3 with `pip install -r tests/sim/requirements.txt` (paramiko, xmodem).
- The console simulators use a **pty**: Linux or WSL. On native Windows those tests are skipped.

## What they cover

| Tests | What they check |
|---|---|
| `RestorePushSnapshotTests` | restore of the backups in `fixtures/backups` on simulated targets (configuration, change report, interface mapping, mode) and the push preview, lines per switch and validation messages |
| `ConfigureSnapshotTests`, `UpgradeSnapshotTests`, `RecordsSnapshotTests` | switch document, push verification, inventory import, update plans and HTML records |
| `SecurityFilterTests`, `PushPlannerTests`, `SecretMaskerTests` | security filter, push validation, masked secrets |
| `Operations/` | each operation end to end: restore (`FakeCatalyst`), factory reset (`FakeResetConsole`), configure over SSH, with a rejected line in Spanish (`sim/configure.py`), update IOS by USB, Xmodem and SCP (`sim/console_upgrade.py`, `sim/console_xmodem_2960.py`, `sim/console_scp.py` + `sim/ssh_bridge.py`; `Category=Slow`) |
| `Transport/` | console session, Xmodem, SSH fingerprint policy and store, SSH and SCP against the simulators |
| `LocateTests`, `Operations/LocateOperationTests` | MAC/ARP parsing and hop decision; the trace end to end against `sim/locate.py` (4 switches, one per loopback address): hops, unmanaged switch, ARP refresh with ping, unreachable switch, unknown switch MAC, read-only commands |
| `SettingsTests` | creation of `settings.json`, custom and invalid values, broken JSON, saving the language |
| `CoreTextsTests`, `AppTextsTests`, `TranslatorTests` | every text has Spanish (or is in `fixtures/untranslated.txt`), the tables keep only used texts, the same `{n}` placeholders and HTML |

## Snapshots

`tests/snapshots/` holds the expected outputs. When a change of behavior is intentional, regenerate them
and review the diff before committing:

```
RACKSIDE_UPDATE_SNAPSHOTS=1 dotnet test Rackside.slnx --filter "Category!=Slow"
git diff tests/snapshots
```

## Files

- `sim/*.py`: simulators. They write logs, pty and keys to `SIM_DIR` (the tests set it to their output folder).
- `fixtures/backups/*.txt`, `fixtures/configure/*`, `fixtures/running/*`: **synthetic** data (no real network).
- `tools/scan_spanish.py`: looks for Spanish text that leaked into records generated in English.
