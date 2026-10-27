#!/usr/bin/env python3
"""Real Core + MAVLink simulator + C# console acceptance test (no Python packages needed)."""
import argparse
from pathlib import Path
import signal
import socket
import subprocess
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--core-dir', type=Path, default=ROOT.parent / 'gcs-core')
parser.add_argument('--simulator-dir', type=Path, default=ROOT.parent / 'uav-simulator')
args = parser.parse_args()
core_dir = args.core_dir.resolve()
sim_dir = args.simulator_dir.resolve()
assert (ROOT / 'proto/telemetry.proto').read_bytes() == (core_dir / 'proto/telemetry.proto').read_bytes(), 'Protocol copies differ'
client_dll = ROOT / 'src/Gcs.Client.Console/bin/Release/net10.0/Gcs.Client.Console.dll'
assert client_dll.exists(), 'Run dotnet build -c Release first'
# Refuse to interfere with an existing server/simulator.
for kind, port in [(socket.SOCK_STREAM, 50051), (socket.SOCK_DGRAM, 14550)]:
    with socket.socket(socket.AF_INET, kind) as sock:
        sock.bind(('0.0.0.0', port))

processes = []

def start(command, log, **kwargs):
    proc = subprocess.Popen(command, stdout=log, stderr=log, **kwargs)
    processes.append(proc)
    return proc

def read(path):
    return path.read_text(errors='replace')

def wait_for(check, description):
    deadline = time.monotonic() + 12
    while time.monotonic() < deadline:
        if check():
            return
        time.sleep(0.05)
    raise AssertionError('Timed out: ' + description)

def events(path, status):
    return [dict(item.split('=', 1) for item in line.split()[1:])
            for line in read(path).splitlines() if line.startswith(status + ' ')]

with tempfile.TemporaryDirectory(prefix='gcs-client-test-') as temp:
    temp = Path(temp)
    logs = {name: temp / (name + '.log') for name in ['core', 'client', 'sim', 'second']}
    handles = {name: path.open('w') for name, path in logs.items()}
    try:
        core = start([str(core_dir / 'build-mac/gcs-core')], handles['core'], cwd=temp)
        wait_for(lambda: 'LINK WAITING' in read(logs['core']) or core.poll() is not None, 'Core startup')
        assert core.poll() is None, read(logs['core'])
        client = start(['dotnet', str(client_dll)], handles['client'])
        wait_for(lambda: events(logs['client'], 'WAITING'), 'WAITING notification')
        assert events(logs['client'], 'WAITING') == [{'rev': '0'}]
        sim = start([str(sim_dir / '.venv/bin/python'), str(sim_dir / 'simulator.py'),
                     '--host', '127.0.0.1', '--scenario', 'straight', '--rate-hz', '5',
                     '--speed-mps', '5', '--heading-deg', '90'], handles['sim'],
                    stdin=subprocess.PIPE, text=True)
        wait_for(lambda: len(events(logs['client'], 'LIVE')) >= 3, 'LIVE positions')
        live = events(logs['client'], 'LIVE')
        assert float(live[-1]['lon']) > float(live[0]['lon'])
        assert int(live[-1]['rev']) > int(live[0]['rev'])
        for key, value in [('lat', 32), ('alt_msl_m', 100), ('rel_alt_m', 50), ('speed_mps', 5), ('hdg_deg', 90)]:
            assert float(live[-1][key]) == value, live[-1]
        sim.stdin.write('pause\n')
        sim.stdin.flush()
        wait_for(lambda: events(logs['client'], 'STALE'), 'STALE notification')
        stale = events(logs['client'], 'STALE')[-1]
        assert stale == events(logs['client'], 'LIVE')[-1], 'STALE must retain last position and revision'
        time.sleep(0.3)
        assert len(events(logs['client'], 'STALE')) == 1
        sim.stdin.write('resume\n')
        sim.stdin.flush()
        wait_for(lambda: int(events(logs['client'], 'LIVE')[-1]['rev']) > int(stale['rev']), 'LIVE recovery')
        client.send_signal(signal.SIGINT)
        assert client.wait(timeout=5) == 0, read(logs['client'])
        assert core.poll() is None, 'Client cancellation stopped server'
        second = start(['dotnet', str(client_dll), 'http://127.0.0.1:50051'], handles['second'])
        wait_for(lambda: events(logs['second'], 'LIVE'), 'New subscription after cancellation')
        core.terminate()
        assert core.wait(timeout=5) == 0, read(logs['core'])
        # Core may gracefully finish the stream or cancel outstanding calls.
        assert second.wait(timeout=5) in (0, 1)
        offline = subprocess.run(['dotnet', str(client_dll)], capture_output=True, text=True, timeout=12)
        assert offline.returncode == 1 and 'Subscription failed:' in offline.stderr, offline
        bad = subprocess.run(['dotnet', str(client_dll), 'invalid'], capture_output=True, text=True, timeout=5)
        assert bad.returncode == 2 and 'Usage:' in bad.stderr, bad
        print('PASS: matching proto; WAITING; moving LIVE telemetry; STALE retention; resume;')
        print('      Ctrl-C; new subscription; server shutdown; offline failure; invalid endpoint.')
    except Exception:
        for name, path in logs.items():
            print(f'--- {name} ---\n{read(path)}')
        raise
    finally:
        for proc in reversed(processes):
            if proc.poll() is None:
                proc.terminate()
                try:
                    proc.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    proc.kill()
                    proc.wait()
            if proc.stdin:
                proc.stdin.close()
        for handle in handles.values():
            handle.close()
