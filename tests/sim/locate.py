# Layer 2 network simulator for "Locate IP": four switches over SSH (paramiko), each on its own loopback
# address, whose ARP and MAC tables are computed from one topology:
#
#   CORE (127.0.0.11, gateway of every VLAN)
#    |- Gi1/0/1  -> ACC1 Gi1/0/52 (127.0.0.12)
#    |                 `- Gi1/0/10: unmanaged switch: 10.20.30.41 + 2 more MACs
#    |- Gi1/0/2  -> ACC2 Gi1/0/49 (127.0.0.13)
#    |                 |- Gi1/0/5: 10.20.30.42 (only learned after a ping)
#    |                 `- Gi1/0/48 -> ACC3 Gi1/0/52 (127.0.0.14)
#    |                                  `- Gi1/0/7: 10.20.30.40 (PC-Accounting)
#    `- Gi1/0/24: 10.20.30.50 (printer, VLAN 30)
#
# Environment: PORT_BASE (CORE on PORT_BASE, ACC1 +1, ACC2 +2, ACC3 +3), ARP_MISSING (switch names missing
# from the CORE ARP until they are pinged). Writes ready.txt when every switch listens.
import os, re, socket, sys, threading
import configure as cfg
import paramiko

GATEWAY = 'CORE'


class Net:
    def __init__(self):
        self.switches = {}
        self.links = []            # (switch, port, switch, port)
        self.hosts = []            # dict: ip, mac, sw, port, vlan, name, awake
        self.extra = []            # (switch, port, vlan, mac): devices without IP behind a port
        self.arp_missing = set(n for n in os.environ.get('ARP_MISSING', '').split(',') if n)
        self.lock = threading.Lock()

    def add_switch(self, sw):
        self.switches[sw.host] = sw

    def toward(self, here, dest):
        """Port of `here` on the path to switch `dest` (breadth-first over the links)."""
        if here == dest:
            return None
        seen, queue = {here}, [(here, None)]
        while queue:
            node, first = queue.pop(0)
            for a, pa, b, pb in self.links:
                for x, px, y in ((a, pa, b), (b, pb, a)):
                    if x == node and y not in seen:
                        hop = first or px
                        if y == dest:
                            return hop
                        seen.add(y)
                        queue.append((y, hop))
        return None

    def where(self, here, sw, port):
        return port if here == sw else self.toward(here, sw)

    def mac_table(self, here):
        rows = [('All', '0100.0ccc.cccc', 'STATIC', 'CPU'), ('All', '0180.c200.0000', 'STATIC', 'CPU')]
        for name, s in self.switches.items():
            if name != here.host:
                rows.append(('1', s.mac, 'DYNAMIC', self.toward(here.host, name)))
        with self.lock:
            for h in self.hosts:
                if h['awake']:
                    rows.append((str(h['vlan']), h['mac'], 'DYNAMIC', self.where(here.host, h['sw'], h['port'])))
        for sw, port, vlan, mac in self.extra:
            rows.append((str(vlan), mac, 'DYNAMIC', self.where(here.host, sw, port)))
        out = ['          Mac Address Table', '-------------------------------------------', '',
               'Vlan    Mac Address       Type        Ports', '----    -----------       --------    -----']
        out += ['%4s    %s    %-8s    %s' % r for r in rows]
        out.append('Total Mac Addresses for this criterion: %d' % len(rows))
        return '\n'.join(out)

    def arp(self, here):
        out = ['Protocol  Address          Age (min)  Hardware Addr   Type   Interface']
        line = lambda ip, age, mac, iface: 'Internet  %-15s %5s   %-14s  ARPA   %s' % (ip, age, mac, iface)
        if here.host != GATEWAY:
            g = self.switches[GATEWAY]
            return '\n'.join(out + [line(here.ip, '-', here.mac, 'Vlan1'), line(g.ip, '2', g.mac, 'Vlan1')])
        out.append(line(here.ip, '-', here.mac, 'Vlan1'))
        for name, s in self.switches.items():
            if name != here.host and name not in self.arp_missing:
                out.append(line(s.ip, '3', s.mac, 'Vlan1'))
        with self.lock:
            for h in self.hosts:
                if h['awake']:
                    out.append(line(h['ip'], '12', h['mac'], 'Vlan%d' % h['vlan']))
        out.append('Internet  10.20.30.99         0   Incomplete      ARPA')
        return '\n'.join(out)

    def ping(self, here, ip):
        with self.lock:
            for h in self.hosts:
                if h['ip'] == ip:
                    h['awake'] = True
            for name, s in self.switches.items():
                if s.ip == ip:
                    self.arp_missing.discard(name)

    def port_status(self, here, port):
        names = {(h['sw'], h['port']): (h['name'], h['vlan']) for h in self.hosts}
        name, vlan = names.get((here.host, port), ('', 1))
        if any(here.host in (a, b) and port in (pa, pb) for a, pa, b, pb in self.links):
            name, vlan = 'Uplink', 'trunk'
        return ('Port      Name               Status       Vlan       Duplex  Speed Type\n'
                '%-9s %-18s connected    %-10s a-full a-1000 10/100/1000BaseTX' % (port, name, vlan))


class L2Switch(cfg.Switch):
    def __init__(self, net, host, ip, mac, os_, model, serial):
        super().__init__(host, os_, model, serial, True, ip=ip)
        self.net, self.mac = net, mac

    def show(self, cmd):
        if cmd == 'show ip arp':
            return self.net.arp(self)
        if cmd == 'show mac address-table':
            return self.net.mac_table(self)
        m = re.match(r'^show interfaces (\S+) status$', cmd)
        if m:
            return self.net.port_status(self, m.group(1))
        return super().show(cmd)

    def exec_other(self, line):
        m = re.match(r'^ping (\S+) repeat (\d+) timeout (\d+)$', line)
        if not m:
            return None
        self.net.ping(self, m.group(1))
        return ('Type escape sequence to abort.\nSending %s, 100-byte ICMP Echos to %s, timeout is %s seconds:\n!!\n'
                'Success rate is 100 percent (2/2), round-trip min/avg/max = 1/1/2 ms' % (m.group(2), m.group(1), m.group(3)))


def build():
    net = Net()
    for host, ip, mac, os_, model, serial in (
            ('CORE', '127.0.0.11', '00aa.0000.0011', 'IOS-XE', 'C9200L-48P-4X', 'JAE1100CORE'),
            ('ACC1', '127.0.0.12', '00aa.0000.0012', 'IOS', 'WS-C2960X-48FPD-L', 'FOC1200ACC1'),
            ('ACC2', '127.0.0.13', '00aa.0000.0013', 'IOS', 'WS-C2960X-48FPD-L', 'FOC1300ACC2'),
            ('ACC3', '127.0.0.14', '00aa.0000.0014', 'IOS', 'WS-C2960X-24PS-L', 'FOC1400ACC3')):
        net.add_switch(L2Switch(net, host, ip, mac, os_, model, serial))
    net.links = [('CORE', 'Gi1/0/1', 'ACC1', 'Gi1/0/52'), ('CORE', 'Gi1/0/2', 'ACC2', 'Gi1/0/49'), ('ACC2', 'Gi1/0/48', 'ACC3', 'Gi1/0/52')]
    for ip, mac, sw, port, vlan, name, awake in (
            ('10.20.30.40', '0050.5600.0040', 'ACC3', 'Gi1/0/7', 20, 'PC-Accounting', True),
            ('10.20.30.41', '0050.5600.0041', 'ACC1', 'Gi1/0/10', 20, 'Meeting-room', True),
            ('10.20.30.42', '0050.5600.0042', 'ACC2', 'Gi1/0/5', 20, 'Sleepy-PC', False),
            ('10.20.30.50', '0050.5600.0050', 'CORE', 'Gi1/0/24', 30, 'Printer', True)):
        net.hosts.append({'ip': ip, 'mac': mac, 'sw': sw, 'port': port, 'vlan': vlan, 'name': name, 'awake': awake})
    net.extra = [('ACC1', 'Gi1/0/10', 20, '0050.5600.0a01'), ('ACC1', 'Gi1/0/10', 20, '0050.5600.0a02')]
    return net


def listen(ip, port):
    s = socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind((ip, port)); s.listen(5)
    return s


def accept_loop(srv, sw, key):
    while True:
        c, _ = srv.accept()
        threading.Thread(target=cfg.handle_conn, args=(c, sw, key), daemon=True).start()


if __name__ == '__main__':
    base = int(os.environ.get('PORT_BASE', '2371'))
    net = build()
    for i, sw in enumerate(net.switches.values()):
        key_file = cfg._path('hostkey_locate_%s.key' % sw.host)
        if not os.path.exists(key_file):
            paramiko.RSAKey.generate(2048).write_private_key_file(key_file)
        srv = listen(sw.ip, base + i)
        threading.Thread(target=accept_loop, args=(srv, sw, paramiko.RSAKey(filename=key_file)), daemon=True).start()
    open(cfg._path('ready.txt'), 'w').write('ready')
    print('ready', flush=True)
    threading.Event().wait()
