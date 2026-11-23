import os as _os
SIM_DIR = _os.environ.get('SIM_DIR') or _os.path.join(_os.path.dirname(_os.path.abspath(__file__)), '..', 'out', 'sim')
_os.makedirs(SIM_DIR, exist_ok=True)
def _path(n): return _os.path.join(SIM_DIR, n)
import os, pty, tty, select, sys, time, hashlib, io
from xmodem import XMODEM
master, slave = os.openpty()
tty.setraw(slave)
with open(_os.environ.get('PTYFILE', _path('pty_xmodem.txt')),'w') as f: f.write(os.ttyname(slave))
host = 'SW07'
img_current = 'c2960x-universalk9-mz.152-7.E3'
version = '15.2(7)E3'
boot = 'flash:/c2960x-universalk9-mz.152-7.E3/c2960x-universalk9-mz.152-7.E3.bin'
boot_manual = False
files = {}            # name -> bytes
state = 'idle'       # idle, user, pass, priv, config, savq, confq, bl
buf = b''
bl_updated = False
log = open(_path('console_xmodem_2960.log'),'w')

def w(s):
    if isinstance(s, str): s = s.encode()
    os.write(master, s)
def prompt():
    if state == 'bl': w(b'\r\nswitch: ')
    elif state == 'config': w(f'\r\n{host}(config)#')
    else: w(f'\r\n{host}#')

def getc(size, timeout=1):
    r,_,_ = select.select([master],[],[],timeout)
    if not r: return None
    return os.read(master, size)
def putc(data, timeout=1):
    os.write(master, data); return len(data)

def ver_of(name):
    import re
    m = re.search(r'-mz\.(\d{2})(\d)-(\d+)\.([A-Za-z]+\d+[a-z]?)', name)
    return f'{m.group(1)}.{m.group(2)}({m.group(3)}){m.group(4)}' if m else '?'

def boot_ios():
    global state
    w('\r\nLoading "%s"...@@@@@@@@@@\r\n\r\nRestricted Rights Legend\r\n' % boot)
    time.sleep(0.5)
    w('\r\n\r\nPress RETURN to get started!\r\n')
    state = 'idle'

def line(l):
    global state, boot_manual, boot, version, host
    log.write(f'[{state}] {l!r}\n'); log.flush()
    if state == 'idle':
        w('\r\n\r\nUser Access Verification\r\n\r\nUsername: '); state='user'; return
    if state == 'user':
        w('\r\nPassword: '); state='pass'; return
    if state == 'pass':
        state='priv'; prompt(); return
    if state == 'savq':
        w('\r\nProceed with reload? [confirm]'); state='confq'; return
    if state == 'confq':
        w('\r\n\r\n*Mar  1 00:10:00: %SYS-5-RELOAD: Reload requested\r\nBoot Sector Filesystem (bs) installed\r\n')
        time.sleep(0.5)
        if boot_manual:
            w('The system is not configured to boot automatically.  The\r\nfollowing commands will initialize the flash file system,\r\nand finally boot the operating system software:\r\n\r\n    flash_init\r\n    boot\r\n')
            state='bl'; prompt()
        else:
            boot_ios()
        return
    if state == 'bl':
        if l == 'flash_init': w('\r\nInitializing Flash...\r\nflashfs[0]: done'); prompt(); return
        if l.startswith('set BAUD'): prompt(); return
        if l == 'set MANUAL_BOOT no':
            if os.environ.get('SIM_IGNORE_MB') != '1': boot_manual = False
            prompt(); return
        if l.startswith('set BOOT '): boot = l.split(' ',2)[2]; prompt(); return
        if l.startswith('copy xmodem: flash:'):
            name = l.split('flash:',1)[1]
            w('\r\nBegin the Xmodem or Xmodem-1K transfer now...\r\n')
            data = io.BytesIO()
            x = XMODEM(getc, putc)
            n = x.recv(data, crc_mode=1, retry=60, timeout=10)
            files[name] = data.getvalue()
            log.write(f'received {len(files[name])} bytes (ret {n})\n'); log.flush()
            w(f'\r\nFile "xmodem:" successfully copied to "flash:{name}"'); prompt(); return
        if l == 'boot':
            global bl_updated
            name = boot.split(':',1)[1].lstrip('/')
            version = ver_of(name)
            if not bl_updated:
                bl_updated = True
                w('\r\nLoading "%s"...Image passed digital signature verification\r\n@@@@@@@\r\nCisco IOS Software, Version %s\r\nChecking for Bootloader upgrade..New version = Version 15.2(7r)E\r\nUpgrading Boot Loader...\r\n writing boot sectors..\r\nBoot Upgrade image auto-rebooting ... done.\r\nThe system will now restart\r\nCPU rev: B\r\nInitializing Flash...\r\n...done Initializing Flash.\r\n' % (boot, version))
                time.sleep(0.5)
                if boot_manual:
                    w('\r\nThe system is not configured to boot automatically.  The\r\nfollowing command will finish loading the operating system\r\nsoftware:\r\n\r\n    boot\r\n')
                    prompt(); return
            boot_ios(); return
        if l == '': prompt(); return
        w('\r\nunknown cmd: ' + l); prompt(); return
    if state == 'config':
        if l == 'end': state='priv'; prompt(); return
        if l == 'boot manual': boot_manual = True
        elif l == 'no boot manual': boot_manual = False
        elif l.startswith('boot system '): boot = l.split(' ',2)[2]
        prompt(); return
    # priv
    if l.startswith('terminal') or l == '': prompt(); return
    if l == 'show version':
        w(f'\r\nCisco IOS Software, C2960X Software (C2960X-UNIVERSALK9-M), Version {version}, RELEASE SOFTWARE (fc1)\r\n{host} uptime is 2 weeks, 3 days\r\nSystem image file is "{boot}"\r\nModel number                    : WS-C2960X-48FPD-L\r\nSystem serial number            : FOC1234X0AB'); prompt(); return
    if l == 'show switch':
        w('\r\nSwitch#  Role   Mac Address     Priority Version  State\r\n----------------------------------------------------------\r\n*1       Master 7010.5c11.2233     1      4       Ready'); prompt(); return
    if l == 'show boot':
        w(f'\r\nBOOT path-list      : {boot}\r\nConfig file         : flash:/config.text\r\nManual Boot         : {"yes" if boot_manual else "no"}'); prompt(); return
    if l.startswith('dir usbflash1') and os.environ.get('USB_BAD') == '1':
        w('\r\n%Error opening usbflash1:/ (Invalid DOS media or no media in slot)'); prompt(); return
    if l.startswith('dir usbflash'):
        w("\r\n% Invalid input detected at '^' marker."); prompt(); return
    if l.startswith('dir flash:/c2960x'):
        w('\r\nDirectory of flash:/c2960x-universalk9-mz.152-7.E3/\r\n\r\n    4  -rwx    26789632   Mar 1 1993 00:08:51 +00:00  c2960x-universalk9-mz.152-7.E3.bin\r\n    5  drwx        8192   Mar 1 1993 00:08:51 +00:00  html\r\n\r\n122185728 bytes total (60000000 bytes free)'); prompt(); return
    if l.startswith('dir'):
        extra = ''.join(f'\r\n    9  -rwx    {len(v)}   Mar 1 1993 02:00:00 +00:00  {k}' for k,v in files.items())
        w(f'\r\nDirectory of flash:/\r\n\r\n    3  drwx         512   Mar 1 1993 00:08:51 +00:00  {img_current}\r\n    6  -rwx         736   Mar 1 1993 00:03:10 +00:00  vlan.dat{extra}\r\n\r\n122185728 bytes total (60000000 bytes free)'); prompt(); return
    if l == 'configure terminal': state='config'; w('\r\nEnter configuration commands, one per line.  End with CNTL/Z.'); prompt(); return
    if l == 'write memory': w('\r\nBuilding configuration...\r\n[OK]'); prompt(); return
    if l == 'reload': w('\r\n\r\nSystem configuration has been modified. Save? [yes/no]: '); state='savq'; return
    if l.startswith('verify /md5 flash:'):
        name = l.split('flash:',1)[1]
        h = hashlib.md5(files.get(name, b'')).hexdigest()
        w(f'\r\n..........Done!\r\nverify /md5 (flash:{name}) = {h}'); prompt(); return
    if l.startswith('show running-config'): prompt(); return
    w("\r\n% Invalid input detected at '^' marker."); prompt()

while True:
    r,_,_ = select.select([master],[],[],1)
    if not r: continue
    try: d = os.read(master, 4096)
    except OSError: break
    for b in d:
        c = bytes([b])
        if c == b'\r':
            l = buf.decode(errors='replace'); buf = b''
            if state not in ('pass',): pass
            line(l)
        elif c == b'\n': pass
        else:
            buf += c
            if state != 'pass': w(c)
