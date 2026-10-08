"""Inside owned CT116: add only the exact native PKCE callback (v0.1.0)."""
import json
import time
import urllib.parse
import urllib.request
from pathlib import Path

base = 'http://127.0.0.1:8080'
callback = 'http://127.0.0.1:43817/callback'
admin = json.loads(Path('/root/dastari-keycloak/bootstrap-admin.json').read_text())
body = urllib.parse.urlencode({'client_id': 'admin-cli', 'grant_type': 'password', **admin}).encode()
with urllib.request.urlopen(urllib.request.Request(base + '/realms/master/protocol/openid-connect/token', data=body)) as response:
    token = json.load(response)['access_token']


def api(path, method='GET', value=None):
    request = urllib.request.Request(base + '/admin/realms/dastari/' + path, method=method,
        data=None if value is None else json.dumps(value).encode(),
        headers={'Authorization': 'Bearer ' + token, 'Content-Type': 'application/json'})
    with urllib.request.urlopen(request) as response:
        raw = response.read()
        return json.loads(raw) if raw else None


clients = api('clients?clientId=sidereal-game')
if len(clients) != 1:
    raise RuntimeError('Expected exactly one existing game client')
client = api('clients/' + clients[0]['id'])
if (not client['publicClient'] or not client['standardFlowEnabled'] or
    client['directAccessGrantsEnabled'] or client['implicitFlowEnabled'] or
    client['attributes'].get('pkce.code.challenge.method') != 'S256'):
    raise RuntimeError('Unexpected existing game security configuration')
before = set(client['redirectUris'])
if callback not in before:
    backup = Path('/root/dastari-keycloak') / f'game-client-before-godot-{time.time_ns()}.json'
    backup.write_text(json.dumps(client, indent=2))
    backup.chmod(0o600)
    client['redirectUris'] = sorted(before | {callback})
    api('clients/' + client['id'], 'PUT', client)
actual = api('clients/' + client['id'])
assert set(actual['redirectUris']) == before | {callback}
assert actual['webOrigins'] == client['webOrigins']
print(json.dumps({'client': 'sidereal-game', 'nativeCallback': callback,
                  'previousCallbacksPreserved': True, 'usersChanged': False}))
