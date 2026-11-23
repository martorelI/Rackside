import os as _os
SIM_DIR = _os.environ.get('SIM_DIR') or _os.path.join(_os.path.dirname(_os.path.abspath(__file__)), '..', 'out', 'sim')
_os.makedirs(SIM_DIR, exist_ok=True)
def _path(n): return _os.path.join(SIM_DIR, n)
import os, pty, tty, select, sys, time, hashlib, io
from xmodem import XMODEM
master, slave = os.openpty()
tty.setraw(slave)
with open(os.environ.get('PTYFILE', _path('pty_scp.txt')),'w') as f: f.write(os.ttyname(slave))
MODE = os.environ.get('MODE','classic')
host = 'SW07'
usb = {}
if MODE == 'classic':
    usb = {'c2960x-universalk9-tar.152-7.E10.tar': b'TAR' * 1000}
elif MODE == 'xe':
    usb = {'cat9k_lite_iosxe.17.09.05.SPA.bin': b'XE' * 4000}
img_current = 'c2960x-universalk9-mz.152-7.E3'
FREE = int(os.environ.get('FREE', '60000000'))
OLD_IMAGES = {}
if os.environ.get('OLD_IMAGES') == '1':
    OLD_IMAGES = {'c2960x-universalk9-mz.152-2.E8': 30000000, 'update': 5000000}
deleted = []
pend_copy = ['']
version = '15.2(7)E3' if MODE == 'classic' else '17.09.04a'
boot = 'flash:/c2960x-universalk9-mz.152-7.E3/c2960x-universalk9-mz.152-7.E3.bin' if MODE == 'classic' else 'flash:packages.conf'
boot_manual = False
files = {}            # name -> bytes
# RUNNING_BIN=<name>.bin: the running image is that loose .bin in the flash root (the tests copy it to flash_scp)
RUNNING_BIN = os.environ.get('RUNNING_BIN', '')
if RUNNING_BIN:
    import re as _re
    img_current = RUNNING_BIN
    boot = 'flash:' + RUNNING_BIN
    _m = _re.search(r'-mz\.(\d{2})(\d)-(\d+)\.([A-Za-z]+\d+[a-z]?)', RUNNING_BIN)
    if _m: version = f'{_m.group(1)}.{_m.group(2)}({_m.group(3)}){_m.group(4)}'
FLASH_DISK = _path('flash_scp')
os.makedirs(FLASH_DISK, exist_ok=True)
def sync_disk():
    for n in os.listdir(FLASH_DISK):
        if n not in files: files[n] = open(os.path.join(FLASH_DISK, n), 'rb').read()
state = 'idle'       # idle, user, pass, priv, config, savq, confq, bl, yn_rm, yn_inst
pend = None
buf = b''
log = open(_path('console_scp.log'),'w')
import re
USB_WRITE = os.environ.get('USB_WRITE','ok')
if USB_WRITE != 'ok': usb = {}
if os.environ.get('USBBIN') == '1': usb = {'c2960x-universalk9-mz.152-7.E10.bin': b'BIN' * 3000}
if os.environ.get('LEFTOVER') == '1': files['c2960x-universalk9-mz.152-7.E10.bin'] = b'parcial'
LOGS = {
 'ok': ['%USB_CONSOLE-6-MEDIA_RJ45: Console media-type is RJ45.', '%USBFLASH-5-CHANGE: usbflash0 has been inserted!'],
 'none': ['%USB_CONSOLE-6-MEDIA_RJ45: Console media-type is RJ45.'],
 'sinlog': [],
 'dosmedia': ['%USB_CONSOLE-6-MEDIA_RJ45: Console media-type is RJ45.'],
 'format': ['%USBFLASH-4-FORMAT: usbflash0 contains unexpected values in partition table or boot sector.  Device needs formatting before use!', '%USBFLASH-5-CHANGE: usbflash0 has been inserted!'],
 'rejected': ["%USB_HOST_STACK-3-USB_FLASH_READY_TEST_FAILED: USB flash failed to pass 'Ready' test.", '%USBFLASH-3-DEVSTARTSTOP: usbflash0 MSCD_StartStop failure in usbflash_mscd_scsi_listener!'],
 'withdrawn': ['%USBFLASH-5-CHANGE: usbflash0 has been inserted!', '%USBFLASH-5-CHANGE: usbflash0 has been removed!'],
}
REGISTERED = USB_WRITE in ('ok','dosmedia','format')


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
            if MODE == 'classic' and 'E10' in boot: version = '15.2(7)E10'
            boot_ios()
        return
    if state == 'copydest':
        name = pend_copy[0]
        if name in files:
            w(f'\r\n%Warning:There is a file already existing with this name\r\nDo you want to over write? [confirm]'); state = 'overw'; return
        files[name] = usb[name]; state = 'priv'
        w(f'\r\nCopy in progress...CCCCC\r\n{len(usb[name])} bytes copied in 1.0 secs'); prompt(); return
    if state == 'overw':
        log.write(f'!!! ANSWER TO OVERWRITE: {l!r}\n'); log.flush(); state = 'priv'; prompt(); return
    if state == 'rara':
        log.write(f'ANSWER TO ODD QUESTION: {l!r}\n'); log.flush()
        state = 'priv'; w('\r\n%Aborted.'); prompt(); return
    if state == 'yn_rm':
        w('\r\ninstall_remove: START\r\nSUCCESS: install_remove'); state='priv'; prompt(); return
    if state == 'yn_inst':
        w('\r\n--- Starting Add ---\r\nSUCCESS: Finished Add\r\n--- Starting Activate ---\r\nSUCCESS: Finished Activate\r\n--- Starting Commit ---\r\nSUCCESS: install_add_activate_commit\r\nChassis 1 reloading, reason - Reload command\r\n')
        version = '17.09.05'; time.sleep(1); boot_ios(); return
    if state == 'bl':
        if l == 'flash_init': w('\r\nInitializing Flash...\r\nflashfs[0]: done'); prompt(); return
        if l.startswith('set BAUD'): prompt(); return
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
            name = boot.split(':',1)[1].lstrip('/')
            version = ver_of(name)
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
        if MODE == 'xe':
            w(f'\r\nCisco IOS XE Software, Version {version}\r\nCisco IOS Software [Cupertino], Catalyst L3 Switch Software (CAT9K_LITE_IOSXE), Version {version}, RELEASE SOFTWARE (fc2)\r\n{host} uptime is 1 week\r\nSystem image file is "flash:packages.conf"\r\nModel Number                       : C9200L-48P-4X\r\nSystem Serial Number               : JAE25110XYZ\r\nSwitch Ports Model              SW Version        SW Image              Mode\r\n*    1 52    C9200L-48P-4X      {version}          CAT9K_LITE_IOSXE      INSTALL'); prompt(); return
        w(f'\r\nCisco IOS Software, C2960X Software (C2960X-UNIVERSALK9-M), Version {version}, RELEASE SOFTWARE (fc1)\r\n{host} uptime is 2 weeks, 3 days\r\nSystem image file is "{boot}"\r\nModel number                    : WS-C2960X-48FPD-L\r\nSystem serial number            : FOC1234X0AB'); prompt(); return
    if l == 'show install summary':
        w(f'\r\n[ Switch 1 ] Installed Package(s) Information:\r\nState (St): I - Inactive, U - Activated & Uncommitted,\r\n            C - Activated & Committed, D - Deactivated & Uncommitted\r\n--------------------------------------------------------------------------------\r\nType  St   Filename/Version\r\n--------------------------------------------------------------------------------\r\nIMG   C    {version}.0.1234'); prompt(); return
    if l == 'install remove inactive':
        w('\r\ninstall_remove: START\r\nThe following files will be deleted:\r\n  flash:old.pkg\r\nDo you want to remove the above files? [y/n]'); state='yn_rm'; return
    if l.startswith('install add file '):
        w('\r\ninstall_add_activate_commit: START\r\nThis operation may require a reload of the system. Do you want to proceed? [y/n]'); state='yn_inst'; return
    if l.startswith('archive download-sw') and '/overwrite' in l:
        log.write('!!! /overwrite WAS USED\n'); log.flush()
    if l.startswith('archive download-sw'):
        src = l.split()[-1]
        w('\r\nexamining image...\r\nextracting info (110 bytes)\r\nInstalling (renaming): `flash:update/c2960x-universalk9-mz.152-7.E10\' ->\r\n                                       `flash:c2960x-universalk9-mz.152-7.E10\'\r\nNew software image installed in flash:c2960x-universalk9-mz.152-7.E10\r\nDeleting `flash:c2960x-universalk9-mz.152-7.E3\'\r\nAll software images installed.')
        boot = 'flash:/c2960x-universalk9-mz.152-7.E10/c2960x-universalk9-mz.152-7.E10.bin'; prompt(); return
    if l == 'show switch':
        w('\r\nSwitch#  Role   Mac Address     Priority Version  State\r\n----------------------------------------------------------\r\n*1       Master 7010.5c11.2233     1      4       Ready'); prompt(); return
    if l == 'show boot' and MODE == 'xe':
        w(f'\r\nBOOT variable = flash:packages.conf;\r\nMANUAL_BOOT variable = no'); prompt(); return
    if l == 'show boot':
        w(f'\r\nBOOT path-list      : {boot}\r\nConfig file         : flash:/config.text\r\nManual Boot         : {"yes" if boot_manual else "no"}'); prompt(); return
    if l == 'show file systems':
        extra = '\r\n  7816675328  7000000000   usbflash     rw   usbflash0:' if REGISTERED else ''
        w('\r\nFile Systems:\r\n\r\n     Size(b)     Free(b)      Type  Flags  Prefixes\r\n*  122185728    60000000     flash     rw   flash:\r\n           -           -    opaque     rw   bs:\r\n      524288      508406     nvram     rw   nvram:\r\n           -           -   network     rw   tftp:' + extra); prompt(); return
    if l.startswith('show logging | include '):
        pat = l.split('include ',1)[1]
        hdr = ['Syslog logging: enabled (0 messages dropped, 0 flushes, 0 overruns, xml disabled, filtering disabled)', '    Buffer logging:  disabled, xml disabled,' if USB_WRITE == 'sinlog' else '    Buffer logging:  level debugging, 212 messages logged, xml disabled,']
        lines = hdr + [f'Nov 19 13:{20+i:02d}:01.512: {e}' for i,e in enumerate(LOGS[USB_WRITE])]
        out = ''.join('\r\n' + x for x in lines if re.search(pat, x))
        w(out); prompt(); return
    if l.startswith('dir usbflash') and not REGISTERED:
        w('\r\n' + l + "\r\n    ^\r\n% Invalid input detected at '^' marker."); prompt(); return
    if l.startswith('dir usbflash0') and USB_WRITE in ('dosmedia','format'):
        w('\r\n%Error opening usbflash0:/ (Invalid DOS media or no media in slot)'); prompt(); return
    if l.startswith('copy usbflash0:'):
        name = l.split()[1].split(':',1)[1]
        global FREE
        if name in files:
            w(f'\r\nDestination filename [{name}]? '); state = 'copydest'; pend_copy[0] = name; return
        w(f'\r\nDestination filename [{name}]? '); state = 'copydest'; pend_copy[0] = name; return
    if l.startswith('dir usbflash0'):
        if not usb: w('\r\n%Error opening usbflash0:/ (No such device)'); prompt(); return
        lst = ''.join(f'\r\n    1  -rw-    {int(os.environ.get("USB_SIZE", len(v)))}   Nov 18 2026 10:00:00 +00:00  {k}' for k,v in usb.items())
        w(f'\r\nDirectory of usbflash0:/\r\n{lst}\r\n\r\n7816675328 bytes total (7000000000 bytes free)'); prompt(); return
    if l.startswith('dir usbflash'):
        w('\r\n%Error opening usbflash1:/ (No such device)'); prompt(); return
    if l.startswith('verify /md5 usbflash0:'):
        name = l.split('usbflash0:',1)[1]
        w(f'\r\n.....Done!\r\nverify /md5 (usbflash0:{name}) = {hashlib.md5(usb.get(name,b"")).hexdigest()}'); prompt(); return
    if l.startswith('delete /force'):
        name = l.split('flash:',1)[1].lstrip('/')
        global FREE
        if name in OLD_IMAGES:
            FREE += OLD_IMAGES.pop(name); deleted.append(name); log.write(f'DELETED {name}\n'); log.flush()
        elif name in files:
            if name == img_current: log.write(f'!!! THE RUNNING IMAGE WAS DELETED {name}\n'); log.flush()
            FREE += len(files.pop(name)); deleted.append(name); log.write(f'DELETED {name}\n'); log.flush()
            if os.path.exists(os.path.join(FLASH_DISK, name)): os.remove(os.path.join(FLASH_DISK, name))
        elif img_current in name:
            log.write(f'!!! THE RUNNING IMAGE WAS DELETED {name}\n'); log.flush()
        prompt(); return
    if any(l.startswith('dir flash:/' + v) for v in OLD_IMAGES):
        v = l.split('flash:/',1)[1].strip('/')
        w(f'\r\nDirectory of flash:/{v}/\r\n\r\n    4  -rwx    {OLD_IMAGES[v]-1000000}   Mar 1 1993 00:08:51 +00:00  {v}.bin\r\n    5  -rwx        1000000   Mar 1 1993 00:08:51 +00:00  info\r\n\r\n122185728 bytes total ({FREE} bytes free)'); prompt(); return
    if l.startswith('dir flash:/c2960x'):
        w('\r\nDirectory of flash:/c2960x-universalk9-mz.152-7.E3/\r\n\r\n    4  -rwx    26789632   Mar 1 1993 00:08:51 +00:00  c2960x-universalk9-mz.152-7.E3.bin\r\n    5  drwx        8192   Mar 1 1993 00:08:51 +00:00  html\r\n\r\n122185728 bytes total (60000000 bytes free)'); prompt(); return
    if l.startswith('dir'):
        sync_disk()
        extra = ''.join(f'\r\n    9  -rwx    {len(v)}   Mar 1 1993 02:00:00 +00:00  {k}' for k,v in files.items())
        old_images = ''.join(f'\r\n    7  drwx         512   Mar 1 1993 00:08:51 +00:00  {k}' for k in OLD_IMAGES)
        running = '' if RUNNING_BIN else f'\r\n    3  drwx         512   Mar 1 1993 00:08:51 +00:00  {img_current}'
        w(f'\r\nDirectory of flash:/\r\n{running}{old_images}\r\n    6  -rwx         736   Mar 1 1993 00:03:10 +00:00  vlan.dat\r\n    8  -rwx        2885   Mar 1 1993 00:03:10 +00:00  config.text{extra}\r\n\r\n122185728 bytes total ({FREE} bytes free)'); prompt(); return
    if l == 'configure terminal': state='config'; w('\r\nEnter configuration commands, one per line.  End with CNTL/Z.'); prompt(); return
    if l.startswith('show running-config | include ^aaa'):
        w('\r\naaa new-model\r\naaa authorization exec default local '); prompt(); return
    if l == 'write memory' and os.environ.get('ODD_QUESTION') == '1':
        w('\r\n% Warning: Saving this config to nvram may corrupt any network management or security files stored at the end of nvram.\r\nContinue? [no]: '); state='rara'; return
    if l == 'write memory': w('\r\nBuilding configuration...\r\n[OK]'); prompt(); return
    if l == 'reload': w('\r\n\r\nSystem configuration has been modified. Save? [yes/no]: '); state='savq'; return
    if l.startswith('dir') or l.startswith('verify') or l.startswith('install'): sync_disk()
    if l.startswith('verify /md5 flash:'):
        name = l.split('flash:',1)[1]
        h = hashlib.md5(files.get(name, b'')).hexdigest()
        log.write(f'VERIFY {name}\n'); log.flush()
        if name not in files:   # like IOS: a missing file is an error, not the hash of nothing
            w(f'\r\n%Error computing MD5 hash flash:{name} (No such file or directory)'); prompt(); return
        w('\r\n')
        for _ in range(max(10, len(files.get(name, b'')) // 10240) // 50):   # one dot per ~10 KB, like IOS
            w('.' * 50); time.sleep(0.01)
        w(f'Done!\r\nverify /md5 (flash:{name}) = {h}'); prompt(); return
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
        elif c == b'\x03':
            log.write(f'CTRL-C in state {state}\n'); log.flush(); buf = b''
            if state != 'bl': state = 'priv'
            w('^C'); prompt()
        else:
            buf += c
            if state != 'pass': w(c)
