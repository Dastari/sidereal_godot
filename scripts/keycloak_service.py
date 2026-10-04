"""Guarded native callback registration in the existing dedicated provider."""
import shlex
import subprocess

HOST = 'root@10.0.1.253'
CT = 116
REMOTE = '/root/dastari-keycloak-provision'


def ssh(arguments, **kwargs):
    return subprocess.run(['ssh', '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=10', HOST,
                           shlex.join(arguments)], check=True, text=True, **kwargs)


def owned():
    result = ssh(['pct', 'config', str(CT)], stdout=subprocess.PIPE)
    if 'dastari-auth-managed' not in result.stdout or 'hostname: dastari-auth' not in result.stdout:
        raise RuntimeError('Refusing to change an unowned authentication container')
