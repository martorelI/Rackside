# Rackside

Windows desktop tool that automates maintenance tasks on Catalyst switches (classic IOS and IOS-XE)
over the console cable (serial or USB) and over SSH. The interface is available in English and Spanish.

| Section | What it does |
|---|---|
| Restore from backup | Applies the backup of a failed switch to its replacement. If the model changes, it remaps the interfaces and converts between IOS-XE and classic IOS. Filters dangerous commands and generates a DRP record. |
| Factory reset | Backs up, erases the configuration, keys and vlan.dat, keeps the IOS image and reloads without saving. Erase record. |
| Update IOS | Queries the version and loads the new image from a USB drive in the switch, over the console (Xmodem) or over SSH (SCP), with MD5 verification, reload and change record. |
| Configure switches | CSV inventory, a query that documents each switch and a "push" of common parameters (LLDP/CDP, banners, VTP, SNMP, NTP, syslog, DNS, SSH, services, STP, users) over SSH to the checked switches. Prior backup, verification and record. |
| Locate IP | Finds the switch and port where an IP or MAC address is connected: the ARP of the gateway switch gives the MAC, then the MAC tables are followed switch by switch over SSH (read only, layer 2, no CDP/LLDP). |
| Settings | Language (English / Spanish). Everything else is configured in `Settings\settings.json`. |

Tested compatibility (with simulators): Catalyst 2960 / 2960-S / 2960-X / 2960-XR and 9200 / 9200L.

## Quick start

Download `Rackside-<version>-win-x64.zip` from the latest release, unzip it into a folder of its own and
run `Rackside.exe` (Windows x64). It does not need .NET installed. It is not
signed yet: SmartScreen asks once ("More info" > "Run anyway").

Everything it generates (backups, evidence, SSH fingerprints, inventory) is saved in subfolders next to
`Rackside.exe`, or in `DataFolder` from `settings.json`:

```
Settings\settings.json              language, folders and defaults
Restores\DRP_<host>_<date>\         restore evidence and DRP record
FactoryResets\RESET_<host>_<date>\  erase evidence
Upgrades\UPDATE_<host>_<date>\      IOS update evidence
Backups\                            initial folder when browsing for a backup
SSHFingerprints\ssh_fingerprints.csv
Inventory\switches.csv
Pushes\PUSH_<date>\                 push evidence, one subfolder per switch
```

**These folders contain network data: protecting them is the operator's responsibility.**

## Building

With the .NET 10 SDK (Windows, Linux or macOS):

```
dotnet test Rackside.slnx
dotnet publish src/Rackside.App -c Release -o publish
```

`publish/Rackside.exe` is a single self-contained file (~55 MB). To sign it (Windows SDK):

```
signtool sign /fd SHA256 /td SHA256 /tr http://timestamp.digicert.com /sha1 <thumbprint> publish\Rackside.exe
```

The code is described in `docs/ARCHITECTURE.md` and the tests in `tests/README.md`.

## Trademarks

Cisco, Catalyst, IOS and IOS-XE are trademarks of Cisco Systems, Inc. This project is independent: it is
not affiliated with, sponsored or endorsed by Cisco. The trademarks are mentioned only to indicate
compatibility.

## Support

support@martorell.mx

## Author

Jose Martorell. License: to be defined (all rights reserved in the meantime).
