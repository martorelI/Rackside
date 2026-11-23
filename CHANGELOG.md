# Changelog

## 1.0.0 (2026-11-23)

First release: one self-contained `Rackside.exe` for Windows x64 (.NET 10, it does not need .NET
installed), in English and Spanish.

- Restore from backup (DRP) with IOS-XE <-> classic IOS conversion, interface remapping and a security
  filter (allow list and block list of commands).
- Factory reset with prior backup, hostname confirmation and erase record.
- Update IOS from a USB drive, over the console (Xmodem) or over SSH (SCP), with SSH fingerprint and MD5
  verification. An image already on flash with the expected MD5 is neither deleted nor sent again.
- Configure switches: CSV inventory, query (documentation) and push of common parameters over SSH
  (sweep) or over the console, with prior backup, verification and record.
- Printable HTML record of every operation, live log, full session transcript with masked secrets and a
  Stop button.
