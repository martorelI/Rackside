# Architecture

`Rackside.slnx` (.NET 10). Each layer only depends on the ones above it; the window holds no logic.

| Project | What it holds |
|---|---|
| `src/Rackside.Core` | Pure logic, no I/O to the switch. `Restore/`: backup parser (`SourceConfig`, `ConfigBlock`), interfaces and their mapping, IOS-XE <-> classic IOS rules (`RuleSets`), security filter (`SecurityFilter`: allow list, block list, extra commands from `settings.json`), `RestorePlanner` and its texts. `Push/`: parameters, validation, lines per platform (`PushPlanner`) and verification (`PushVerifier`). `Upgrade/`: what is read from the switch and the update plan (`UpgradePlanner`). `Configure/`: inventory CSV and the switch document. `Security/SecretMasker`, `Settings/AppSettings` (+ `DataPaths`), `Localization/Translator` + `es.tsv`, `Validators`, `Csv`, `Ci` (case-insensitive regex and comparisons, invariant formatting). |
| `src/Rackside.Transport` | `Console/`: `IConsoleChannel` (serial port or SSH shell), `ConsoleSession` (prompts, login and enable, commands, configuration lines answering only the expected questions, plan application), `SessionTranscript`, `Xmodem`. `Ssh/`: `FingerprintPolicy` (security rule 2), `FingerprintStore` (the CSV), `SshConnector` (SSH.NET, pinned to the verified fingerprint), `Scp` (`scp -t flash:<file>`). |
| `src/Rackside.Records` | `HtmlRecords`: the printable HTML records (restore, factory reset, IOS update, push, reduced record, switch list) from `RecordData`, with the embedded logo. |
| `src/Rackside.Operations` | One class per section (`RestoreOperation`, `ResetOperation`, `UpgradeOperation`, `ConfigureOperation`): review/query, confirmations through `IOperatorUi`, execution, evidence folder and record (`RecordWriter`), log (`OperationLog`) and `SwitchConnection`. They can be stopped through their `Cancellation`. |
| `src/Rackside.App` | WinForms (`net10.0-windows`): `Ui/MainForm` (sidebar, operations in the background with Stop), `Ui/` (theme, controls, dialogs, `OperatorBridge`, connection card, texts with `Resources/ui.es.tsv`) and one page per section in `Pages/`. |
| `tests/Rackside.Tests` | xUnit: snapshots, operations against simulated switches, transport, settings and texts (see `tests/README.md`). |

## Texts and language

The code is written in English. `Translator.T` returns the Spanish from `src/Rackside.Core/Localization/es.tsv`
(English TAB Spanish, `\n` / `\r` escaped; the key must be identical to the literal) and the window looks
first in `src/Rackside.App/Resources/ui.es.tsv`. Internal states (`SUCCESSFUL`, `FAILED`, `SKIPPED`,
`WITH FAILURES`, `DIRECT`, `CONVERT`...) are English codes; only their presentation is translated. The
language (`Language` in `settings.json`, `en` by default) is applied on restart.

Everything sent to the switch uses the invariant culture; commands and configuration lines are compared
ignoring case, as the switch does.

## Common flow of the operations

1. Reusable connection panel (SSH / serial / USB) with a single username, password and enable.
2. "Review" or "Query": reads the device without changing anything and builds a visible plan.
3. Operator confirmation (dialogs with specific warnings).
4. Execution with a live log, a full session transcript and masked secrets.
5. Post-check and a printable HTML record (with the embedded logo), in one evidence folder per
   operation: `DRP_<host>_<date>`, `RESET_<host>_<date>`, `UPDATE_<host>_<date>`,
   `PUSH_<date>_<ticket>` (one subfolder per switch). The queries of "Configure switches" go to
   `Inventory/Queries/QUERY_<date>`.

## Data files

- `Settings/settings.json` (UTF-8 without BOM). Keys: `Language` (`en`/`es`), `DataFolder`, `Folders`
  {Restores, FactoryResets, Upgrades, Backups, SSHFingerprints, Inventory, Pushes}, `PreferredCOMPort`,
  `Push` {LoginBanner, MotdBanner, TimeZone, UtcOffset, SummerTime, VtyIdleTimeout},
  `ExtraAllowedCommands` (list of regex). Invalid value => the default is used and reported in the
  Settings section; broken JSON => defaults and the file is left untouched.
- `SSHFingerprints/ssh_fingerprints.csv`: Device (`ip:port`), Algorithm, Fingerprint (`SHA256:...`),
  Hostname, Serial, Date, How, Operator.
- `Inventory/switches.csv`: Name, IP, Port, Hostname, Model, Platform, Version, Serial, LLDP, CDP, VTP,
  STP, Domain, SNMP, NTP, Syslog, Banner, SSH, Status, Date. The descriptive columns are saved in the
  language the app had when querying. The import accepts CSV with `,` or `;` and alternative headers
  (Switch, Address, Nombre, Direccion IP...).
- Evidence files: `log.txt`, `console_session.txt` / `ssh_session.txt`, `report.txt`,
  `applied_config.txt`, `interface_mapping.csv`, `running_before.txt`, `sent_lines.txt`,
  `parameters.txt` and the HTML records (`Report_DRP_*`, `Report_Reset_*`, `Report_Upgrade_*`,
  `push_report.html`).
