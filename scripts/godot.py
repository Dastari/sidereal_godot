"""Native evaluation lifecycle, invoked only through scripts/dev.py (v0.4.3)."""
from concurrent.futures import ThreadPoolExecutor
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tarfile
import urllib.parse
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT
TOOLS = ROOT / '.tools'
VERSION = '4.7.2'
DOTNET_VERSION = '8.0.425'
RELEASE = f'https://github.com/godotengine/godot/releases/download/{VERSION}-stable/'
ARCHIVES = {
    f'Godot_v{VERSION}-stable_mono_linux_x86_64.zip': (
        RELEASE + f'Godot_v{VERSION}-stable_mono_linux_x86_64.zip',
        '1855960b27ee3ef5e66e5e228cced69d55637b24334a7411162687dcd077d8f9f645348cdb8eae984bec8135d49ed855a1e3a16476786b8bce60774fd8402d13'),
    f'Godot_v{VERSION}-stable_mono_export_templates.tpz': (
        RELEASE + f'Godot_v{VERSION}-stable_mono_export_templates.tpz',
        'bb5c41d72370ed743660361f6228006f808ab04ca33abdc545d740b044f3fe057f32ae8cb7873a1bc86ddcd82ae683b9f6dfdfe4179852f2c0f1acde2ff6bd5a'),
    f'dotnet-sdk-{DOTNET_VERSION}.tar.gz': (
        f'https://builds.dotnet.microsoft.com/dotnet/Sdk/{DOTNET_VERSION}/dotnet-sdk-{DOTNET_VERSION}-linux-x64.tar.gz',
        '934b8060a7190e5909ad1fd0785db542f487b3bbf6cdd14826b02095fdd0d0394298b1634085eff302928fccc33f7c1a7253e9b87df555fc36fce819bcd2e798'),
    'spacetime-x86_64-unknown-linux-gnu.tar.gz': (
        'https://github.com/clockworklabs/SpacetimeDB/releases/download/v2.10.0/spacetime-x86_64-unknown-linux-gnu.tar.gz',
        '2188099ab1dde4a9a0ca88334fb10440eb9509bdcae93b4ef1147c7a66c8e702'),
    'spacetimedb-update-x86_64-unknown-linux-gnu': (
        'https://github.com/clockworklabs/SpacetimeDB/releases/download/v2.10.0/spacetimedb-update-x86_64-unknown-linux-gnu',
        'ee2bf6f3d94dd4ee58e6497610ad1b54965923e087ede466c354b42117447ce2'),
}
DOTNET = TOOLS / 'dotnet' / 'dotnet'
GODOT = TOOLS / 'godot' / f'Godot_v{VERSION}-stable_mono_linux_x86_64' / f'Godot_v{VERSION}-stable_mono_linux.x86_64'
DOWNLOADS = ROOT / 'output/godot-downloads'


def environment():
    env = os.environ.copy()
    env.update(DOTNET_ROOT=str(DOTNET.parent), DOTNET_CLI_TELEMETRY_OPTOUT='1',
               DOTNET_NOLOGO='1', XDG_DATA_HOME=str(TOOLS / 'godot-user'),
               PATH=str(DOTNET.parent) + os.pathsep + env['PATH'])
    return env


def execute(command, **kwargs):
    return subprocess.run([str(v) for v in command], cwd=PROJECT, env=environment(), check=True, **kwargs)


def digest(path, algorithm='sha256'):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, algorithm).hexdigest()


def archive(name):
    url, expected = ARCHIVES[name]
    # Godot/.NET publish SHA-512 manifests; SpacetimeDB release metadata uses SHA-256.
    algorithm = 'sha512' if len(expected) == 128 else 'sha256'
    cache = TOOLS / 'godot-downloads' / name
    cache.parent.mkdir(parents=True, exist_ok=True)
    if not cache.exists():
        temporary = cache.with_suffix('.partial')
        urllib.request.urlretrieve(url, temporary)
        if digest(temporary, algorithm) != expected:
            temporary.unlink()
            raise RuntimeError(f'Checksum mismatch: {name}')
        temporary.replace(cache)
    if digest(cache, algorithm) != expected:
        raise RuntimeError(f'Checksum mismatch: {name}')
    return cache


def setup():
    # All downloads are pinned to upstream release checksums.
    with ThreadPoolExecutor(3) as pool:
        paths = list(pool.map(archive, ARCHIVES))
    if not GODOT.exists():
        GODOT.parent.parent.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(paths[0]) as package:
            package.extractall(GODOT.parent.parent)
        GODOT.chmod(0o755)
    if not (DOTNET.parent / 'sdk' / DOTNET_VERSION).exists():
        DOTNET.parent.mkdir(parents=True, exist_ok=True)
        with tarfile.open(paths[2]) as package:
            package.extractall(DOTNET.parent, filter='data')
    templates = TOOLS / 'godot-user/godot/export_templates' / f'{VERSION}.stable.mono'
    if not (templates / 'version.txt').exists():
        templates.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(paths[1]) as package:
            for member in package.infolist():
                if not member.filename.startswith('templates/') or member.is_dir():
                    continue
                target = templates / member.filename.removeprefix('templates/')
                target.parent.mkdir(parents=True, exist_ok=True)
                with package.open(member) as source, target.open('wb') as dest:
                    shutil.copyfileobj(source, dest)
                if target.name.endswith('.x86_64'):
                    target.chmod(0o755)
    spacetime = TOOLS / 'spacetime-native'
    version = spacetime / 'bin/2.10.0'
    if not (version / 'spacetimedb-cli').exists():
        version.mkdir(parents=True, exist_ok=True)
        with tarfile.open(paths[3]) as package:
            package.extractall(version, filter='data')
        for name in ('spacetimedb-cli', 'spacetimedb-standalone'):
            (version / name).chmod(0o755)
    current = spacetime / 'bin/current'
    if not current.exists():
        current.symlink_to('2.10.0', target_is_directory=True)
    updater = spacetime / 'spacetime'
    if not updater.exists():
        shutil.copyfile(paths[4], updater)
        updater.chmod(0o755)
    execute([updater, f'--root-dir={spacetime}', '--version'])
    execute([DOTNET, '--version'])
    execute([GODOT, '--headless', '--version'])


def generate(managed):
    # The running backend can be ahead of upstream main. Replay a pinned metadata
    # snapshot through the official CLI's supported schema-extractor hook.
    placeholder = ROOT / '.runtime/godot-schema-placeholder.js'
    placeholder.write_text('// Public metadata supplied by the pinned schema extractor.\n')
    env = environment()
    env['SPACETIMEDB_SCHEMA_EXTRACTOR'] = str(ROOT / 'scripts/godot_schema.py')
    managed.run(managed.CLI + ['generate', '--lang', 'csharp', '--namespace', 'Sidereal.Bindings',
                '--out-dir', 'Bindings', '--js-path', str(placeholder), '--yes', '--no-config'], env=env)


def refresh_schema(managed):
    url = managed.DB_URL + '/v1/database/' + managed.CFG['project']['database'] + '/schema?version=10'
    with urllib.request.urlopen(url, timeout=30) as response:
        raw = response.read()
        length = response.headers.get('Content-Length')
        if length is not None and len(raw) != int(length):
            raise RuntimeError('Incomplete deployed schema response')
    schema = json.loads(raw)
    if not isinstance(schema.get('sections'), list):
        raise RuntimeError('Expected complete SpacetimeDB V10 schema')
    path = PROJECT / 'Bindings/schema.json'
    path.write_text(json.dumps({'V10': schema}, separators=(',', ':')) + '\n')
    (PROJECT / 'Bindings/schema-provenance.json').write_text(json.dumps({
        'format': 'RawModuleDefV10', 'bytes': len(raw), 'sourceSha256': hashlib.sha256(raw).hexdigest(),
        'schemaSha256': digest(path), 'generator': 'SpacetimeDB CLI 2.10.0',
        'source': 'deployed backend schema (metadata only; no database rows)'
    }, indent=2) + '\n')
    generate(managed)


def test(managed, smoke=False, module=None, module_sha256=None, operator_config=None):
    execute([DOTNET, 'restore', 'Tests/Tests.csproj', '--locked-mode'])
    arguments = []
    if smoke:
        from time import time_ns
        cfg = managed.CFG.get('godot', {})
        port = cfg.get('smoke_port', 3131)
        if port == managed.CFG['server']['port']:
            raise RuntimeError('Godot smoke cannot use the live database listener')
        url = f'http://127.0.0.1:{port}'
        name = 'godot-smoke-database'
        data = ROOT / '.runtime/godot-smoke-data'
        state = managed.load().get(name)
        if not state or not managed.alive(state):
            managed.port_free('127.0.0.1', port)
            managed.launch(name, managed.CLI + ['start', '--listen-addr', f'127.0.0.1:{port}', '--data-dir', str(data), '--non-interactive'])
        managed.ready(url + '/v1/ping', name)
        from smoke_fixture import configure
        configure(ROOT, port, operator_config)
        database = f'sidereal-godot-{time_ns()}-smoke'
        if not module or not module_sha256 or digest(Path(module)) != module_sha256:
            raise RuntimeError('Godot smoke requires the exact deployed --module-artifact and --artifact-sha256; it never uses an older source module')
        managed.cli('publish', database, '--server', url, '--js-path', module, '--yes', '--no-config', '--delete-data=never')
        managed.cli('call', '--server', url, '--yes', '--no-config', database, 'operator_set_starter_prefab',
                    json.dumps('godot-smoke-starter'), json.dumps('fed.s.wren'), json.dumps('ship-components-v1@4'), 'false')
        arguments = [url, database]
        (ROOT / '.runtime/godot-smoke.json').write_text(json.dumps({'url': url, 'database': database}) + '\n')
        settings = json.loads((PROJECT / 'client-settings.json').read_text())
        origin = urllib.parse.urlsplit(settings['gameOrigin'])
        settings.update(gameOrigin=f'{origin.scheme}://{origin.hostname}:{cfg.get("test_https_port", 8448)}', database=database)
        (ROOT / 'client-settings.network-test.json').write_text(json.dumps(settings, indent=2) + '\n')
        DOWNLOADS.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(ROOT / 'client-settings.network-test.json', DOWNLOADS / 'client-settings.network-test.json')
    execute([DOTNET, 'run', '--project', 'Tests/Tests.csproj', '--no-restore', '--', *arguments])


def build():
    execute([sys.executable, ROOT / 'scripts/godot_world_assets.py', '--verify'])
    execute([sys.executable, ROOT / 'scripts/godot_space_assets.py', '--verify'])
    execute([sys.executable, ROOT / 'scripts/godot_crew_assets.py', '--verify'])
    execute([sys.executable, ROOT / 'scripts/godot_combat_assets.py', '--verify'])
    execute([DOTNET, 'restore', 'Sidereal.Godot.csproj', '--locked-mode'])
    execute([DOTNET, 'build', 'Sidereal.Godot.csproj', '--no-restore'])
    execute([GODOT, '--headless', '--path', PROJECT, '--editor', '--import'])


def source_bundle(downloads=DOWNLOADS):
    # An explicit allowlist prevents local credentials/cache from entering downloads.
    names = ('README.md', 'project.godot', 'Main.tscn', 'Main.cs', 'Main.cs.uid', 'ClientCore.cs', 'ClientCore.Gameplay.cs',
             'NativeAuth.cs', 'NativePreferences.cs', 'InventoryModel.cs', 'InventoryCargoPlan.cs', 'PresentationDisplay.cs',
             'Sidereal.Godot.csproj', 'Sidereal.Godot.sln', 'global.json', 'client-settings.json',
             'export_presets.cfg')
    target = downloads / 'Sidereal-Godot-Source.zip'
    temporary = target.with_suffix('.partial')
    with zipfile.ZipFile(temporary, 'w', zipfile.ZIP_DEFLATED) as package:
        for name in names:
            package.write(PROJECT / name, 'Sidereal/' + name)
        for path in sorted(PROJECT.glob('*.cs.uid')):
            if path.name not in names:
                package.write(path, 'Sidereal/' + path.name)
        for directory in ('Ui', 'InventoryUi', 'Presentation', 'Assets', 'Input'):
            for path in sorted((PROJECT / directory).rglob('*')):
                if path.is_file():
                    package.write(path, 'Sidereal/' + str(path.relative_to(PROJECT)))
        for path in sorted((PROJECT / 'Bindings').rglob('*.cs')):
            package.write(path, 'Sidereal/' + str(path.relative_to(PROJECT)))
        package.write(PROJECT / 'Bindings/.gdignore', 'Sidereal/Bindings/.gdignore')
        for path in sorted(PROJECT.glob('packages.*.lock.json')):
            package.write(path, 'Sidereal/' + path.name)
    temporary.replace(target)


def export(downloads=DOWNLOADS):
    build()
    downloads.mkdir(parents=True, exist_ok=True)
    (ROOT / 'output').mkdir(parents=True, exist_ok=True)
    (ROOT / 'output/.gdignore').touch()
    for preset, directory, binary in [('Windows Desktop', 'windows', 'Sidereal.exe'), ('Linux', 'linux', 'Sidereal.x86_64')]:
        out = ROOT / 'output/godot' / directory
        # Never carry files from an older engine/runtime into the new package.
        if out.exists():
            shutil.rmtree(out)
        out.mkdir(parents=True, exist_ok=True)
        execute([GODOT, '--headless', '--path', PROJECT, '--export-release', preset, out / binary])
        if not (out / binary).exists() or not any(out.rglob('Sidereal.Godot.dll')):
            raise RuntimeError(f'{preset} export did not produce the complete .NET game')
        shutil.copyfile(PROJECT / 'Ui/Fonts/OFL.txt', out / 'Font-LICENSE.txt')
        package = downloads / f'Sidereal-{directory}-x64.zip'
        temporary = package.with_suffix('.partial')
        with zipfile.ZipFile(temporary, 'w', zipfile.ZIP_DEFLATED) as bundle:
            for path in sorted(out.rglob('*')):
                if path.is_file():
                    bundle.write(path, 'Sidereal/' + str(path.relative_to(out)))
        temporary.replace(package)
    source_bundle(downloads)
    files = [downloads / name for name in ('Sidereal-Godot-Source.zip', 'Sidereal-linux-x64.zip', 'Sidereal-windows-x64.zip')]
    (downloads / 'SHA256SUMS.txt').write_text(''.join(f'{digest(path)}  {path.name}\n' for path in files))
    manifest = [{'name': p.name, 'bytes': p.stat().st_size, 'sha256': digest(p)} for p in files]
    (downloads / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    shutil.copyfile(PROJECT / 'download.html', downloads / 'index.html')
    print(json.dumps(manifest, indent=2))


def serve(config):
    if not (DOWNLOADS / 'index.html').exists():
        raise RuntimeError('Build packages first with godot-export')
    class Handler(SimpleHTTPRequestHandler):
        def end_headers(self):
            self.send_header('X-Content-Type-Options', 'nosniff')
            self.send_header('Cache-Control', 'no-cache')
            super().end_headers()
    server = ThreadingHTTPServer(('127.0.0.1', config.get('download_port', 5186)),
                                partial(Handler, directory=str(DOWNLOADS)))
    server.serve_forever()


def routes(managed):
    cfg = managed.CFG.get('godot', {})
    result = [(cfg.get('download_https_port', 8446), f"http://127.0.0.1:{cfg.get('download_port', 5186)}"),
              (cfg.get('game_https_port', 8447), managed.DB_URL)]
    if (ROOT / 'client-settings.network-test.json').exists():
        result.append((cfg.get('test_https_port', 8448), f"http://127.0.0.1:{cfg.get('smoke_port', 3131)}"))
    return result


def check_routes(managed):
    result = subprocess.run(['tailscale', 'serve', 'status', '--json'], check=True, capture_output=True, text=True)
    existing = json.loads(result.stdout).get('Web', {})
    present = set()
    expected = routes(managed)
    if len({port for port, _ in expected}) != len(expected):
        raise RuntimeError('Download and game transport need distinct Tailscale ports')
    for port, target in expected:
        for host, value in existing.items():
            if host.endswith(f':{port}'):
                proxy = value.get('Handlers', {}).get('/', {}).get('Proxy')
                if proxy != target:
                    raise RuntimeError(f'Tailscale port {port} already serves another workload')
                present.add(port)
    return present


def check_unit():
    result = subprocess.run(['systemctl', 'show', 'sidereal-godot-downloads.service',
                             '--property=WorkingDirectory', '--value'], check=True, capture_output=True, text=True)
    directory = result.stdout.strip()
    if directory and Path(directory).resolve() != ROOT.resolve():
        raise RuntimeError('The downloads unit belongs to another checkout')


def up(managed):
    check_routes(managed)
    check_unit()
    if not (DOWNLOADS / 'index.html').exists():
        raise RuntimeError('Run godot-export before exposing downloads')
    # The managed foreground command is the same for interactive use and reboot.
    unit = 'sidereal-godot-downloads.service'
    template = ROOT / 'ops/systemd' / unit
    destination = Path('/etc/systemd/system') / unit
    destination.write_text(template.read_text().replace('@PROJECT_ROOT@', str(ROOT)))
    subprocess.run(['systemctl', 'daemon-reload'], check=True)
    subprocess.run(['systemctl', 'enable', '--now', unit], check=True)
    for port, target in routes(managed):
        subprocess.run(['tailscale', 'serve', '--bg', f'--https={port}', target], check=True)


def command(action, managed, module_artifact=None, module_sha256=None, operator_config=None, export_directory=None):
    if action == 'setup':
        setup()
    elif action == 'generate':
        generate(managed)
    elif action == 'schema-refresh':
        refresh_schema(managed)
    elif action == 'build':
        build()
    elif action == 'export':
        export(Path(export_directory).resolve() if export_directory else DOWNLOADS)
    elif action == 'run':
        execute([GODOT, '--path', PROJECT])
    elif action == 'serve':
        serve(managed.CFG.get('godot', {}))
    elif action == 'up':
        up(managed)
    elif action == 'down':
        present = check_routes(managed)
        check_unit()
        subprocess.run(['systemctl', 'stop', 'sidereal-godot-downloads.service'], check=True)
        for port in present:
            subprocess.run(['tailscale', 'serve', f'--https={port}', 'off'], check=True)
    elif action == 'auth-setup':
        from keycloak_service import owned, ssh, HOST, CT, REMOTE
        owned()
        ssh(['mkdir', '-p', REMOTE])
        source = ROOT / 'ops/keycloak/configure-godot.py'
        subprocess.run(['scp', '-q', str(source), f'{HOST}:{REMOTE}/{source.name}'], check=True)
        remote = '/root/dastari-keycloak/' + source.name
        ssh(['pct', 'push', str(CT), f'{REMOTE}/{source.name}', remote, '--perms', '0600'])
        ssh(['pct', 'exec', str(CT), '--', 'python3', remote])
    elif action in ('test', 'smoke'):
        test(managed, smoke=action == 'smoke', module=module_artifact, module_sha256=module_sha256, operator_config=operator_config)
    elif action == 'smoke-stop':
        managed.down('godot-smoke-database')
    else:
        raise ValueError(f'Unknown Godot command: {action}')
