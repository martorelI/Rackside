import os as _os
SIM_DIR = _os.environ.get('SIM_DIR') or _os.path.join(_os.path.dirname(_os.path.abspath(__file__)), '..', 'out', 'sim')
_os.makedirs(SIM_DIR, exist_ok=True)
def _path(n): return _os.path.join(SIM_DIR, n)
# IOS-XE style SSH server: the console (shell) connects to the simulator through its pty;
# "scp -t flash:x" saves to SIM_DIR/flash_scp (which the simulator sees as flash:). Rejects -p -r -d.
import os, socket, threading, time, select, paramiko
PORT = int(_os.environ.get('PORT_SSH', '2224'))
PTY = open(_os.environ.get('PTYFILE', _path('pty_scp.txt'))).read().strip()
if not _os.path.exists(_path('bridge_host.key')): paramiko.RSAKey.generate(2048).write_private_key_file(_path('bridge_host.key'))
password = paramiko.RSAKey(filename=_path('bridge_host.key'))
log = open(_path('ssh_bridge.log'), 'a', buffering=1)
# STALE_AFTER_SCP=1 reproduces a control session that hangs after a long transfer: a shell opened
# before an SCP stops delivering output shortly after the SCP finishes (the channel stays open).
STALE = _os.environ.get('STALE_AFTER_SCP') == '1'
scp_count = [0]
class Srv(paramiko.ServerInterface):
    def __init__(self): self.ev = threading.Event(); self.type_ = None; self.cmd = None
    def check_channel_request(self, kind, chanid): return paramiko.OPEN_SUCCEEDED
    def get_allowed_auths(self, u): return 'keyboard-interactive'
    def check_auth_interactive(self, u, sub): return paramiko.InteractiveQuery('', '', ('Password: ', False))
    def check_auth_interactive_response(self, r): return paramiko.AUTH_SUCCESSFUL if r and r[0] == 'Pa55w0rd!' else paramiko.AUTH_FAILED
    def check_channel_pty_request(self, *a): return True
    def check_channel_shell_request(self, ch): self.type_ = 'shell'; self.ev.set(); return True
    def check_channel_exec_request(self, ch, cmd): self.type_ = 'exec'; self.cmd = cmd.decode(); self.ev.set(); return True
def read(ch, n):
    b = b''
    while len(b) < n:
        d = ch.recv(min(65536, n - len(b)))
        if not d: raise EOFError
        b += d
    return b
def shell(ch):
    fd = os.open(PTY, os.O_RDWR | os.O_NOCTTY)
    import termios, tty; tty.setraw(fd)
    scp_at_start = scp_count[0]; budget = 300
    while not ch.closed:
        r, _, _ = select.select([fd, ch], [], [], 0.2)
        if fd in r:
            d = os.read(fd, 65536)
            if d and STALE and scp_count[0] > scp_at_start:
                if budget <= 0: log.write('STALE: output swallowed\n'); continue
                d = d[:budget]; budget -= len(d)
            if d: ch.sendall(d)
        if ch in r:
            d = ch.recv(65536)
            if not d: break
            os.write(fd, d)
    os.close(fd)
def scp(ch, cmd):
    log.write(f'EXEC: {cmd}\n'); p = cmd.split()
    if any(o in p for o in ('-p', '-r', '-d')):
        ch.sendall(b'\x01-p -r -d options not supported\n'); ch.send_exit_status(1); ch.close(); return
    name = p[-1].split(':', 1)[1].lstrip('/')
    ch.sendall(b'\x00'); hdr = b''
    while not hdr.endswith(b'\n'): hdr += read(ch, 1)
    tam = int(hdr.decode().split()[1]); ch.sendall(b'\x00')
    data = read(ch, tam); read(ch, 1)
    open(os.path.join(_path('flash_scp'), name), 'wb').write(data)
    log.write(f'RECEIVED {name} {len(data)}\n')
    ch.sendall(b'\x00'); ch.send_exit_status(0); ch.close(); scp_count[0] += 1
def handle_conn(sock):
    t = paramiko.Transport(sock); t.add_server_key(password); s = Srv(); t.start_server(server=s)
    ch = t.accept(20)
    if ch is None: return
    s.ev.wait(10)
    if s.type_ == 'shell': shell(ch)
    else: scp(ch, s.cmd)
srv = socket.socket(); srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); srv.bind(('127.0.0.1', PORT)); srv.listen(5)
print('ready', flush=True)
while True:
    c, _ = srv.accept(); threading.Thread(target=handle_conn, args=(c,), daemon=True).start()
