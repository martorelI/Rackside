# Changelog

## 1.1.1 (2026-11-25)

- "Configure switches" works only over SSH, with the switches checked in the list. The console option was
  removed: it only reached the switch on the cable and ignored the checked ones, which was confusing for an
  inventory or a push. The console is still used by Restore, Factory reset and Update IOS.

## 1.1.0 (2026-11-25)

- New section "Locate IP": finds the switch and port where an IP or MAC address is connected. It reads the
  ARP table of the start switch (the gateway of the VLAN) to get the MAC of the address and of the other
  switches of the list, and then follows the MAC tables switch by switch over SSH until the port where the
  MAC is learned with no other switch behind it. Layer 2 only (no CDP/LLDP), read only: it sends nothing
  but `show` commands and, if asked, two pings from the start switch to refresh the ARP. Warns about ports
  with several MACs, trunks, port-channels and switches whose MAC is unknown. Evidence in
  `Inventory\Locate\LOCATE_<date>_<address>`.

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
