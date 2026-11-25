import os as _os
SIM_DIR = _os.environ.get('SIM_DIR') or _os.path.join(_os.path.dirname(_os.path.abspath(__file__)), '..', 'out', 'sim')
_os.makedirs(SIM_DIR, exist_ok=True)
def _path(n): return _os.path.join(SIM_DIR, n)
# Cisco switch simulator (classic IOS and IOS-XE) for "Configure switches".
# SSH mode: several switches on ports PORT_BASE.. (paramiko). pty mode: one switch on a serial console (transport tests).
import os, sys, socket, threading, select, re, time, hashlib, json
import paramiko

LOG = open(_path('configure.log'), 'a', buffering=1)

class Switch:
    def __init__(self, host, os_='IOS', model='WS-C2960X-48FPD-L', serial='FOC1111X0AA', priv=True, enable='En4ble', ip='10.1.1.1', hp=False):
        self.host, self.os, self.model, self.serial, self.priv0, self.enable_pw, self.ip, self.hp = host, os_, model, serial, priv, enable, ip, hp
        self.lldp = (os_ == 'IOS-XE') and False
        self.cdp = True
        self.vtp = {'mode': 'Server', 'domain': '', 'version': '1', 'password': ''}
        self.stp = 'pvst' if os_ == 'IOS' else 'rapid-pvst'
        self.snmp_users = []
        self.top = [  # top-level lines (in order) and blocks
            'service timestamps debug datetime msec', 'service timestamps log datetime msec',
            'hostname ' + host, 'enable secret 5 $1$abc$xyzxyzxyz',
            'username admin privilege 15 secret 5 $1$def$uvwuvwuvw',
            'ip http server', 'ip http secure-server',
            'snmp-server community viejaRO RO', 'snmp-server community Secreta99 RW 10',
            'snmp-server host 10.9.9.9 version 2c viejaRO',
            'ntp server 10.0.0.5', 'logging host 10.0.0.7',
            'ip access-list standard MANAGEMENT', 'access-list 10 permit 10.0.0.0 0.0.0.255',
        ]
        self.children = {'line con 0': [], 'line vty 0 4': ['login local', 'transport input ssh telnet'], 'line vty 5 15': ['login local', 'transport input ssh telnet'],
                         'ip access-list standard MANAGEMENT': ['permit 10.0.0.0 0.0.0.255'], 'interface Vlan1': ['ip address %s 255.255.255.0' % ip]}
        self.banners = {}
        self.saved = False
        self.ssh_v2 = False

    # ------------------------------------------------------------------ output
    def show_version(self):
        if self.hp:
            return 'Image stamp:    /ws/swbuildm/rel_ukiah_qaoff/code/build/btm(swbuildm_rel_ukiah_qaoff_rel_ukiah)\n                Jun 22 2018 10:53:41\n                WB.16.02.0021\nBoot Image:     Primary'
        if self.os == 'IOS':
            return ('Cisco IOS Software, C2960X Software (C2960X-UNIVERSALK9-M), Version 15.2(7)E3, RELEASE SOFTWARE (fc3)\n'
                    'ROM: Bootstrap program is C2960X boot loader\n%s uptime is 3 weeks, 2 days\n'
                    'System image file is "flash:/c2960x-universalk9-mz.152-7.E3/c2960x-universalk9-mz.152-7.E3.bin"\n'
                    'Model Number                    : %s\nSystem Serial Number            : %s\n' % (self.host, self.model, self.serial))
        return ('Cisco IOS XE Software, Version 17.09.04a\nCisco IOS Software [Cupertino], Catalyst L3 Switch Software (CAT9K_LITE_IOSXE), Version 17.9.4a, RELEASE SOFTWARE (fc3)\n'
                '%s uptime is 1 week\nModel Number                       : %s\nSystem Serial Number               : %s\n' % (self.host, self.model, self.serial))

    def running(self):
        out = ['Building configuration...', '', 'Current configuration : 4321 bytes', '!', 'version ' + ('15.2' if self.os == 'IOS' else '17.9')]
        for l in self.top:
            out.append(l)
            if l in self.children:
                out += [' ' + c for c in self.children[l]]
            out.append('!') if l.startswith('ip access-list') else None
        if self.vtp['mode'] == 'Transparent':
            if self.vtp['domain']: out.append('vtp domain ' + self.vtp['domain'])
            out.append('vtp mode transparent')
        if self.lldp and self.os == 'IOS': out.append('lldp run')
        if (not self.lldp) and self.os == 'IOS-XE': pass
        if self.lldp and self.os == 'IOS-XE': out.append('lldp run')
        if not self.cdp: out.append('no cdp run')
        if self.stp != 'pvst' or self.os == 'IOS-XE': out.append('spanning-tree mode ' + self.stp)
        out.append('interface Vlan1')
        out += [' ' + c for c in self.children['interface Vlan1']]
        for t, txt in self.banners.items():
            out.append('banner %s ^C' % t)
            out += txt
            out.append('^C')
        for k in ('line con 0', 'line vty 0 4', 'line vty 5 15'):
            out.append(k)
            out += [' ' + c for c in self.children[k]]
        out += ['!', 'end']
        return '\n'.join(out)

    def show(self, cmd):
        if cmd == 'show version': return self.show_version()
        if self.hp: return None
        if cmd == 'show running-config': return self.running()
        if cmd == 'show lldp':
            return 'Global LLDP Information:\n    Status: ACTIVE\n    LLDP advertisements are sent every 30 seconds' if self.lldp else '% LLDP is not enabled'
        if cmd == 'show cdp':
            return 'Global CDP information:\n\tSending CDP packets every 60 seconds' if self.cdp else '% CDP is not enabled'
        if cmd == 'show vtp status':
            return ('VTP Version capable             : 1 to 3\nVTP version running             : %s\nVTP Domain Name                 : %s\n'
                    'VTP Pruning Mode                : Disabled\nVTP Operating Mode              : %s\nMaximum VLANs supported locally : 1005' % (self.vtp['version'], self.vtp['domain'], self.vtp['mode']))
        if cmd == 'show spanning-tree summary | include mode':
            return 'Switch is in %s mode' % self.stp
        if cmd == 'show ip interface brief | include ^Vlan':
            return 'Vlan1                  %s       YES NVRAM  up                    up' % self.ip
        if cmd == 'show snmp user':
            return '\n'.join('User name: %s\nEngine ID: 800000090300ABCDEF\nstorage-type: nonvolatile\t active' % u for u in self.snmp_users)
        return None

    # ------------------------------------------------------------------ config
    def set_top(self, line, prefix=None):
        prefix = prefix or line
        self.top = [l for l in self.top if not l.startswith(prefix)] + [line]

    def config(self, l, mode):
        """Returns (output, new_mode)."""
        inv = "\n% Invalid input detected at '^' marker.\n"
        if mode.startswith('config-line:'):
            block = mode.split(':', 1)[1]
            keys = ['line vty 0 4', 'line vty 5 15'] if block == 'line vty 0 15' else [block]
            m = re.match(r'^exec-timeout (\d+) (\d+)$', l)
            if m:
                for k in keys:
                    self.children[k] = [c for c in self.children[k] if not c.startswith('exec-timeout')]
                    if m.group(1) != '10': self.children[k].append(l)
                return '', mode
            if l.startswith('transport input ') or l.startswith('access-class '):
                for k in keys:
                    pref = ' '.join(l.split()[:2]) if l.startswith('transport') else 'access-class'
                    self.children[k] = [c for c in self.children[k] if not c.startswith(pref)] + [l]
                return '', mode
        if l in ('exit',):
            return '', ('config' if mode != 'config' else 'exec')
        if mode != 'config':
            # unknown subline: IOS tries it in global mode
            mode = 'config'
        if l == 'end':
            return '', 'exec'
        m = re.match(r'^line (vty 0 15|con 0)$', l)
        if m:
            return '', 'config-line:' + l
        if l.startswith('interface '):
            self.children.setdefault(l, [])
            if l not in self.top: self.top.append(l)
            return '', 'config-if'
        if l == 'lldp run': self.lldp = True; return '', mode
        if l == 'no lldp run': self.lldp = False; return '', mode
        if l == 'cdp run': self.cdp = True; return '', mode
        if l == 'no cdp run': self.cdp = False; return '', mode
        m = re.match(r'^vtp mode (transparent|server|client|off)$', l)
        if m: self.vtp['mode'] = m.group(1).capitalize(); return 'Setting device to VTP %s mode for VLANS.' % m.group(1).upper(), mode
        m = re.match(r'^vtp domain (\S+)$', l)
        if m: old = self.vtp['domain'] or 'NULL'; self.vtp['domain'] = m.group(1); return 'Changing VTP domain name from %s to %s' % (old, m.group(1)), mode
        m = re.match(r'^vtp version (\d)$', l)
        if m:
            if self.vtp['mode'] == 'Client': return '\n% Cannot modify version in VTP client mode unless the system is in VTP version 3\n', mode
            self.vtp['version'] = m.group(1); return '', mode
        m = re.match(r'^vtp password (\S+)$', l)
        if m: self.vtp['password'] = m.group(1); return 'Setting device VTP password to %s' % m.group(1), mode
        m = re.match(r'^ip domain(-| )name (\S+)$', l)
        if m:
            if self.os == 'IOS' and m.group(1) == ' ': return inv, mode
            canon = ('ip domain-name ' if self.os == 'IOS' else 'ip domain name ') + m.group(2)
            self.top = [x for x in self.top if not re.match(r'^ip domain[- ]name ', x)] + [canon]; return '', mode
        m = re.match(r'^no ip domain(-| )lookup$', l)
        if m:
            if self.os == 'IOS' and m.group(1) == ' ': return inv, mode
            canon = 'no ip domain-lookup' if self.os == 'IOS' else 'no ip domain lookup'
            self.set_top(canon); return '', mode
        m = re.match(r'^ip name-server (\S+)$', l)
        if m:
            ex = [x for x in self.top if x.startswith('ip name-server ')]
            srv = (ex[0].split()[2:] if ex else []) + [m.group(1)]
            self.set_top('ip name-server ' + ' '.join(srv), 'ip name-server '); return '', mode
        if l == 'ip ssh version 2': self.ssh_v2 = True; self.set_top(l, 'ip ssh version'); return '', mode
        m = re.match(r'^ip ssh (time-out|authentication-retries) (\d+)$', l)
        if m:
            default = {'time-out': '120', 'authentication-retries': '3'}[m.group(1)]
            self.top = [x for x in self.top if not x.startswith('ip ssh ' + m.group(1))]
            if m.group(2) != default: self.top.append(l)
            return '', mode
        if l.startswith('no ') and l[3:] in ('ip http server', 'ip http secure-server', 'ip source-route', 'service pad'):
            self.top = [x for x in self.top if x != l[3:]]
            if l[3:] in ('ip http server', 'ip http secure-server', 'service pad'): self.set_top(l)
            return '', mode
        m = re.match(r'^no snmp-server community (\S+)$', l)
        if m: self.top = [x for x in self.top if not re.match(r'^snmp-server community %s( |$)' % re.escape(m.group(1)), x)]; return '', mode
        if l.startswith('no snmp-server host '):
            self.top = [x for x in self.top if x != l[3:]]; return '', mode
        m = re.match(r'^snmp-server user (\S+) (\S+) v3 auth sha (\S+)( priv aes 128 (\S+))?$', l)
        if m: self.snmp_users.append(m.group(1)); return '', mode
        if l == 'snmp-server enable traps':
            for t in ('snmp-server enable traps snmp authentication linkdown linkup coldstart warmstart', 'snmp-server enable traps config', 'snmp-server enable traps syslog'):
                self.set_top(t)
            return '', mode
        m = re.match(r'^snmp-server (location|contact) (.+)$', l)
        if m: self.set_top(l, 'snmp-server ' + m.group(1) + ' '); return '', mode
        m = re.match(r'^snmp-server community (\S+) (RO|RW)( \S+)?$', l)
        if m: self.set_top(l, 'snmp-server community %s ' % m.group(1)); return '', mode
        if re.match(r'^snmp-server (host|group) ', l): self.set_top(l); return '', mode
        m = re.match(r'^no ntp server (?:vrf \S+ )?(\S+)$', l)
        if m: self.top = [x for x in self.top if not re.match(r'^ntp server (?:vrf \S+ )?%s( |$)' % re.escape(m.group(1)), x)]; return '', mode
        if re.match(r'^ntp (server|source) ', l): self.set_top(l, l if l.startswith('ntp server') else 'ntp source '); return '', mode
        m = re.match(r'^no logging (?:host )?(\S+)$', l)
        if m: self.top = [x for x in self.top if not re.match(r'^logging (?:host )?%s( |$)' % re.escape(m.group(1)), x)]; return '', mode
        if l == 'logging trap informational':
            self.top = [x for x in self.top if not x.startswith('logging trap ')]; return '', mode
        if re.match(r'^logging (trap|source-interface|buffered) ', l): self.set_top(l, ' '.join(l.split()[:2]) + ' '); return '', mode
        if re.match(r'^logging host \S+$', l): self.set_top(l); return '', mode
        m = re.match(r'^clock (timezone|summer-time) ', l)
        if m: self.set_top(l, 'clock ' + m.group(1) + ' '); return '', mode
        m = re.match(r'^spanning-tree mode (pvst|rapid-pvst|mst)$', l)
        if m:
            self.stp = m.group(1); self.top = [x for x in self.top if not x.startswith('spanning-tree mode')]; return '', mode
        m = re.match(r'^username (\S+) privilege 15 secret (\S+)$', l)
        if m:
            self.top = [x for x in self.top if not x.startswith('username %s ' % m.group(1))] + ['username %s privilege 15 secret 9 $9$hashhash' % m.group(1)]; return '', mode
        m = re.match(r'^enable secret (\S+)$', l)
        if m: self.set_top('enable secret 9 $9$enablehash', 'enable secret '); return '', mode
        if l.startswith('no banner '):
            self.banners.pop(l.split()[2], None); return '', mode
        if re.match(r'^(service|login on-|spanning-tree loopguard|errdisable|udld|vlan )', l):
            self.set_top(l); return '', mode
        LOG.write('INVALID: %r\n' % l)
        return inv, mode


class Session:
    """A console (SSH or serial) on top of a Switch: echo, prompts, login, enable and banners."""
    def __init__(self, sw, send_data, login=False, user='admin', password='Pa55w0rd!'):
        self.sw, self.send_data = sw, send_data
        self.buf = ''
        self.state = 'login_user' if login else ('priv' if sw.priv0 else 'user')
        self.mode = 'exec'
        self.user, self.password = user, password
        self.banner = None
        self.nolog = login
        if not login:
            self.send_data('\r\n' + self.prompt())

    def prompt(self):
        h = self.sw.host
        if self.state == 'user': return h + '>'
        if self.mode == 'exec': return h + '#'
        if self.mode.startswith('config-line'): return h + '(config-line)#'
        if self.mode == 'config-if': return h + '(config-if)#'
        return h + '(config)#'

    def receive(self, data):
        for ch in data:
            if ch in '\r\n':
                if ch == '\n' and self.buf == '' and getattr(self, '_last_cr', False):
                    self._last_cr = False
                    continue
                self._last_cr = (ch == '\r')
                line, self.buf = self.buf, ''
                self.line(line)
            elif ch == '\x03':
                self.buf = ''
                self.send_data('^C\r\n' + self.prompt())
            else:
                self.buf += ch
                if self.state not in ('login_pass', 'enable_pass') and self.banner is None:
                    self.send_data(ch)
                elif self.banner is not None:
                    self.send_data(ch)

    def line(self, l):
        e = self.state
        LOG.write('%s [%s/%s] %r\n' % (self.sw.host, e, self.mode, l))
        if e == 'login_user':
            if l == '':
                self.send_data('\r\n\r\nUser Access Verification\r\n\r\nUsername: '); return
            self.u = l; self.state = 'login_pass'; self.send_data('\r\nPassword: '); return
        if e == 'login_pass':
            if self.u == self.user and l == self.password:
                self.state = 'priv' if self.sw.priv0 else 'user'
                self.send_data('\r\n\r\n' + self.prompt())
            else:
                self.state = 'login_user'; self.send_data('\r\n% Login invalid\r\n\r\nUsername: ')
            return
        if e == 'enable_pass':
            if l == self.sw.enable_pw:
                self.state = 'priv'; self.send_data('\r\n' + self.prompt())
            else:
                self.state = 'user'; self.send_data('\r\n% Access denied\r\n\r\n' + self.prompt())
            return
        self.send_data('\r\n')
        if self.banner is not None:
            type_, delim, txt = self.banner
            if delim in l:
                before = l.split(delim)[0]
                if before: txt.append(before)
                self.sw.banners[type_] = txt
                self.banner = None
                self.send_data(self.prompt())
            else:
                txt.append(l)
            return
        if e == 'user':
            if l == 'enable':
                self.state = 'enable_pass'; self.send_data('Password: '); return
            if l.startswith('terminal '): self.send_data(self.prompt()); return
            if l.startswith('show '):
                o = self.sw.show(l)
                self.send_data(((o.replace('\n', '\r\n') + '\r\n') if o else '') + self.prompt()); return
            self.send_data("% Invalid input detected at '^' marker.\r\n\r\n" + self.prompt()); return
        if l.strip() == '':
            self.send_data(self.prompt()); return
        if self.mode == 'exec':
            if l.startswith('terminal '): self.send_data(self.prompt()); return
            if l in ('configure terminal', 'conf t'):
                if self.sw.hp:
                    self.mode = 'config'; self.send_data(self.prompt()); return
                self.mode = 'config'; self.send_data('Enter configuration commands, one per line.  End with CNTL/Z.\r\n' + self.prompt()); return
            if l in ('write memory', 'wr'):
                self.sw.saved = True
                time.sleep(0.3)
                self.send_data('Building configuration...\r\n[OK]\r\n' + self.prompt()); return
            if l.startswith('show '):
                o = self.sw.show(l)
                if o is None:
                    self.send_data("                ^\r\n% Invalid input detected at '^' marker.\r\n\r\n" + self.prompt()); return
                self.send_data(o.replace('\n', '\r\n') + '\r\n' + self.prompt()); return
            o = self.sw.exec_other(l) if hasattr(self.sw, 'exec_other') else None   # other exec commands (locate.py: ping)
            if o is not None:
                self.send_data(o.replace('\n', '\r\n') + '\r\n' + self.prompt()); return
            self.send_data("% Invalid input detected at '^' marker.\r\n\r\n" + self.prompt()); return
        m = re.match(r'^banner (login|motd|exec) (\S)$', l)
        if m:
            self.banner = (m.group(1), m.group(2), [])
            self.send_data("Enter TEXT message.  End with the character '%s'.\r\n" % m.group(2)); return
        out, self.mode = self.sw.config(l.strip(), self.mode)
        self.send_data((out.replace('\n', '\r\n') + '\r\n' if out else '') + self.prompt())


# ---------------------------------------------------------------------- SSH
class Srv(paramiko.ServerInterface):
    def __init__(self, sw):
        self.sw = sw; self.ev = threading.Event()
    def check_channel_request(self, kind, chanid): return paramiko.OPEN_SUCCEEDED
    def get_allowed_auths(self, u): return 'password,keyboard-interactive'
    def check_auth_password(self, u, p): return paramiko.AUTH_SUCCESSFUL if (u, p) == ('admin', 'Pa55w0rd!') else paramiko.AUTH_FAILED
    def check_auth_interactive(self, u, sub):
        self.u = u; return paramiko.InteractiveQuery('', '', ('Password: ', False))
    def check_auth_interactive_response(self, r): return paramiko.AUTH_SUCCESSFUL if (self.u == 'admin' and r and r[0] == 'Pa55w0rd!') else paramiko.AUTH_FAILED
    def check_channel_pty_request(self, *a): return True
    def check_channel_shell_request(self, ch): self.ev.set(); return True

def handle_conn(sock, sw, password):
    t = paramiko.Transport(sock); t.add_server_key(password); s = Srv(sw)
    try:
        t.start_server(server=s)
    except Exception as e:
        LOG.write('neg: %s\n' % e); return
    ch = t.accept(20)
    if ch is None: return
    s.ev.wait(10)
    ses = Session(sw, lambda x: ch.sendall(x.encode()))
    while not ch.closed:
        r, _, _ = select.select([ch], [], [], 0.5)
        if ch in r:
            d = ch.recv(4096)
            if not d: break
            ses.receive(d.decode(errors='replace'))

def serve(port, sw, file_password):
    if not os.path.exists(file_password):
        paramiko.RSAKey.generate(2048).write_private_key_file(file_password)
    password = paramiko.RSAKey(filename=file_password)
    srv = socket.socket(); srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(('127.0.0.1', port)); srv.listen(5)
    while True:
        c, _ = srv.accept()
        threading.Thread(target=handle_conn, args=(c, sw, password), daemon=True).start()

STATE = {}
def dump_to_disk():
    while True:
        time.sleep(0.5)
        d = {}
        for k, sw in STATE.items():
            d[k] = {'lldp': sw.lldp, 'cdp': sw.cdp, 'vtp': sw.vtp, 'stp': sw.stp, 'saved': sw.saved, 'top': sw.top, 'children': sw.children, 'banners': sw.banners, 'snmp_users': sw.snmp_users}
        json.dump(d, open(_path('state.json.tmp'), 'w'), indent=1); os.replace(_path('state.json.tmp'), _path('state.json'))

if __name__ == '__main__':
    mode = sys.argv[1] if len(sys.argv) > 1 else 'ssh'
    if mode == 'ssh':
        base = int(os.environ.get('PORT_BASE', '2241'))   # the .NET tests use another range
        defs = [
            (base, Switch('SW01', 'IOS', 'WS-C2960X-48FPD-L', 'FOC1111X0AA', True, ip='10.1.1.1')),
            (base + 1, Switch('SW02', 'IOS-XE', 'C9200L-48P-4G', 'JAE2222B0BB', False, ip='10.1.1.2')),
            (base + 2, Switch('SW03', 'IOS', 'WS-C2960X-24PS-L', 'FOC3333X0CC', True, ip='10.1.1.3')),
            (base + 3, Switch('HP-IDF4', 'IOS', 'J9773A', 'CN44XX', True, ip='10.1.1.4', hp=True)),
        ]
        for p, sw in defs:
            STATE[sw.host] = sw
            threading.Thread(target=serve, args=(p, sw, _path('hostkey_%d.key' % p)), daemon=True).start()
        threading.Thread(target=dump_to_disk, daemon=True).start()
        print('ready', flush=True)
        while True: time.sleep(1)
    else:
        import pty, tty
        master, slave = os.openpty(); tty.setraw(slave); tty.setraw(master)
        open(_os.environ.get('PTYFILE', _path('pty_configure.txt')), 'w').write(os.ttyname(slave))
        sw = Switch('SW09', 'IOS', 'WS-C2960X-24PD-L', 'FOC9999X0ZZ', False, ip='10.1.1.9')
        STATE[sw.host] = sw
        threading.Thread(target=dump_to_disk, daemon=True).start()
        ses = Session(sw, lambda x: os.write(master, x.encode()), login=True)
        print('ready', flush=True)
        while True:
            r, _, _ = select.select([master], [], [], 0.5)
            if master in r:
                d = os.read(master, 4096)
                if d: ses.receive(d.decode(errors='replace'))
