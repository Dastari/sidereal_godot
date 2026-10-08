"""Issue a short-lived fixture credential using ONLY the isolated server's own key.

The exact deployed Sidereal module pins this public maintenance principal. This
does not reuse production signing keys, credentials, or OIDC game accounts.
"""
from pathlib import Path
import base64
import json
import subprocess
import time
import tomllib

OPERATOR = 'c2005c42b5bdfffcfba7f99d5313dc8b1a3a3db7b8ba5453cc010c39d50387be'


def configure(root, port, operator_config=None):
    if port == 3100:
        raise RuntimeError('Fixture credentials cannot target the live listener')
    tools = root / '.tools/spacetime-native'
    key = tools / 'config/id_ecdsa'
    if not key.resolve().is_relative_to(tools.resolve()) or tools.is_symlink():
        raise RuntimeError('The fixture signing key must belong to this isolated installation')
    principal = root / '.runtime/godot-smoke-operator.json'
    if operator_config:
        # Read only the deployment subject, not its bearer credential or signing key.
        original = tomllib.loads(Path(operator_config).read_text())['spacetimedb_token']
        encoded = original.split('.')[1]
        claims = json.loads(base64.urlsafe_b64decode(encoded + '=' * ((4 - len(encoded) % 4) % 4)))
        if claims.get('iss') != 'localhost' or claims.get('hex_identity') != OPERATOR:
            raise RuntimeError('Expected the deployed module maintenance principal')
        principal.write_text(json.dumps({'subject': claims['sub']}) + '\n')
        principal.chmod(0o600)
    if not principal.exists():
        raise RuntimeError('Smoke seeding needs --operator-config pointing to the private deployment CLI config; no production credential is copied')
    subject = json.loads(principal.read_text())['subject']
    encode = lambda raw: base64.urlsafe_b64encode(raw).rstrip(b'=').decode()
    json_part = lambda value: encode(json.dumps(value, separators=(',', ':')).encode())
    now = int(time.time())
    payload = {'iss': 'localhost', 'sub': subject,
               'aud': ['spacetimedb'], 'iat': now, 'exp': now + 3600, 'hex_identity': OPERATOR}
    message = json_part({'typ': 'JWT', 'alg': 'ES256'}) + '.' + json_part(payload)
    # OpenSSL produces DER ECDSA; JWT ES256 uses a fixed-width r || s signature.
    der = subprocess.run(['openssl', 'dgst', '-sha256', '-sign', str(key)],
                         input=message.encode(), check=True, capture_output=True).stdout
    if len(der) < 8 or der[0] != 48 or der[2] != 2:
        raise RuntimeError('Unexpected ES256 signature encoding')
    length = der[3]
    r = int.from_bytes(der[4:4 + length], 'big')
    offset = 4 + length
    if der[offset] != 2:
        raise RuntimeError('Unexpected ES256 signature encoding')
    length = der[offset + 1]
    s = int.from_bytes(der[offset + 2:offset + 2 + length], 'big')
    token = message + '.' + encode(r.to_bytes(32, 'big') + s.to_bytes(32, 'big'))
    path = tools / 'config/cli.toml'
    path.write_text('default_server = "godot-smoke"\nspacetimedb_token = ' + json.dumps(token) +
                    '\n\n[[server_configs]]\nnickname = "godot-smoke"\nhost = "127.0.0.1:' + str(port) +
                    '"\nprotocol = "http"\n')
    path.chmod(0o600)
