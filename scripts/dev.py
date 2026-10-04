#!/usr/bin/env python3
"""Native frontend lifecycle. The existing game's database is never started here."""
from contextlib import contextmanager
from pathlib import Path
import argparse
import fcntl
import json
import os
import signal
import socket
import subprocess
import sys
import time
import tomllib
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
CFG = tomllib.loads((ROOT / 'dev.toml').read_text())
STATE = ROOT / '.runtime'
STATE.mkdir(exist_ok=True, mode=0o700)
TOOLS = ROOT / '.tools/spacetime-native'
CLI = [str(TOOLS / 'spacetime'), f'--root-dir={TOOLS}']
DB_URL = f"http://{CFG['server']['host']}:{CFG['server']['port']}"


def run(command, **kwargs):
    return subprocess.run(command, cwd=ROOT, check=True, **kwargs)


def cli(*arguments):
    return run(CLI + list(arguments))


def load():
    path = STATE / 'processes.json'
    return json.loads(path.read_text()) if path.exists() else {}


def save(state):
    path = STATE / 'processes.json'
    temporary = path.with_suffix('.tmp')
    descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(descriptor, 'w') as stream:
        json.dump(state, stream, indent=2)
    temporary.replace(path)


@contextmanager
def state_lock():
    with (STATE / 'processes.lock').open('a') as handle:
        fcntl.flock(handle, fcntl.LOCK_EX)
        yield


def proc(pid):
    try:
        fields = Path(f'/proc/{pid}/stat').read_text().split()
        return None if fields[2] == 'Z' else fields[21]
    except FileNotFoundError:
        return None


def alive(row):
    return row['start'] is not None and proc(row['pid']) == row['start']


def port_free(host, port):
    with socket.socket() as sock:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        sock.bind((host, port))


def launch(name, command):
    if name != 'godot-smoke-database':
        raise RuntimeError('This project may only launch its isolated smoke database')
    with state_lock():
        state = load()
        if name in state and alive(state[name]):
            return
        with (STATE / f'{name}.log').open('ab') as output:
            os.chmod(output.name, 0o600)
            process = subprocess.Popen(command, cwd=ROOT, stdout=output, stderr=output, start_new_session=True)
        state[name] = {'pid': process.pid, 'start': proc(process.pid), 'command': command}
        save(state)


def ready(url, name):
    deadline = time.monotonic() + 45
    while time.monotonic() < deadline:
        row = load().get(name)
        if not row or not alive(row):
            raise RuntimeError(f'{name} stopped; inspect its private runtime log')
        try:
            with urllib.request.urlopen(url, timeout=1) as response:
                if response.status == 200:
                    return
        except Exception:
            time.sleep(.15)
    raise RuntimeError('Isolated test server readiness timed out')


def down(name):
    if name != 'godot-smoke-database':
        raise RuntimeError('Refusing to stop any other project service')
    with state_lock():
        state = load()
        row = state.get(name)
        if row and alive(row):
            os.killpg(row['pid'], signal.SIGTERM)
            deadline = time.monotonic() + 30
            while alive(row) and time.monotonic() < deadline:
                time.sleep(.1)
            if alive(row):
                raise RuntimeError('Smoke server is still shutting down; its process was not killed')
        state.pop(name, None)
        save(state)


def main():
    actions = ('setup', 'generate', 'schema-refresh', 'build', 'export', 'run', 'serve', 'up',
               'down', 'auth-setup', 'test', 'smoke', 'smoke-stop')
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['godot-' + action for action in actions])
    parser.add_argument('--module-artifact')
    parser.add_argument('--artifact-sha256')
    parser.add_argument('--operator-config', help='Private deployment CLI config; only its subject is used for the isolated fixture')
    args = parser.parse_args()
    import godot
    godot.command(args.command.removeprefix('godot-'), sys.modules[__name__], args.module_artifact, args.artifact_sha256, args.operator_config)


if __name__ == '__main__':
    main()
