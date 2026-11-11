"""Azure-authenticated operation; never print credentials or raw child-process errors."""
import datetime
import json
import os
import subprocess
import sys
import time
import urllib.request

APP = ['--resource-group', 'Phpnet20211126011001ResourceGroup', '--name', 'kinshout-api-prod']


def az(*args):
    process = subprocess.run(['az', *args, *APP, '--output', 'json'], capture_output=True, text=True)
    if process.returncode:
        raise RuntimeError('Azure configuration read failed')
    return json.loads(process.stdout)


def main():
    action = sys.argv[1] if len(sys.argv) > 1 else 'verify'
    if action not in ('verify', 'report', 'pause', 'resume', 'stop', 'preview', 'stop-and-hide'):
        raise RuntimeError('Unknown operator action')
    settings = {x['name']: x.get('value', '') for x in az('webapp', 'config', 'appsettings', 'list')}
    def setting(name, default=None):
        return next((v for k, v in settings.items() if k.lower().replace(':', '__') == name.lower()), default)
    if action != 'verify':
        connection = setting('ConnectionStrings__DefaultConnection')
        if not connection:
            strings = az('webapp', 'config', 'connection-string', 'list')
            connection = next((x['connectionString'] for x in strings if x['name'] == 'DefaultConnection'), None)
        if not connection or connection.startswith('@Microsoft.KeyVault('):
            raise RuntimeError('A resolved production database connection is required')
        env = dict(os.environ, ConnectionStrings__DefaultConnection=connection)
        def run(operation):
            result = subprocess.run(['dotnet', 'run', '--no-build', '-c', 'Release', '--project', 'tools/SeedingControl', '--', operation],
                                    env=env, capture_output=True, text=True, timeout=120)
            if result.returncode:
                raise RuntimeError('Production database operation failed; credentials and raw errors suppressed')
            return json.loads(result.stdout.strip().splitlines()[-1])
        print(json.dumps(run(action)))
        return
    config = az('webapp', 'config', 'show')
    print('CONFIG', json.dumps({
        'enabled': setting('DiscussionSeeding__Enabled', 'true'),
        'simulationEnabled': setting('DiscussionSeeding__SimulationEnabled', 'true'),
        'moderationEnabled': setting('OpenAI__ModerationEnabled', 'true'),
        'ordinaryDiscussionModeration': setting('OpenAI__DiscussionModerationEnabled', 'false'),
        'alwaysOn': config.get('alwaysOn')}))
    if str(setting('DiscussionSeeding__Enabled', 'true')).lower() != 'true' or str(setting('DiscussionSeeding__SimulationEnabled', 'true')).lower() != 'true':
        raise RuntimeError('Production simulation is disabled')
    if str(setting('OpenAI__ModerationEnabled', 'true')).lower() != 'true' or not config.get('alwaysOn'):
        raise RuntimeError('Production moderation and Always On are required')
    # Check the deployed binary through the public health endpoint.
    app = az('webapp', 'show')
    expected = os.environ.get('GITHUB_SHA')
    health = {}
    for attempt in range(12):
        try:
            with urllib.request.urlopen('https://' + app['defaultHostName'] + '/api/health', timeout=30) as response:
                health = json.load(response)
            if not expected or expected in str(health.get('version', '')): break
        except Exception:
            pass
        time.sleep(10)
    print('HEALTH', json.dumps(health))
    if not health or (expected and expected not in str(health.get('version', ''))):
        raise RuntimeError('Health endpoint does not identify the expected deployed commit')
    secret = setting('ClientAuth__KinshoutWebSecret')
    if not secret: raise RuntimeError('Web client credential is required for verification')
    base = 'https://' + app['defaultHostName']
    request = urllib.request.Request(base + '/api/auth/client',
        data=json.dumps({'clientId': 'kinshout-web', 'clientSecret': secret}).encode(),
        headers={'Content-Type': 'application/json', 'Origin': 'https://kinoiserie.com'})
    with urllib.request.urlopen(request, timeout=30) as response:
        auth = json.load(response)
    token = auth.get('clientToken')
    if not token: raise RuntimeError('Client authentication did not return a token')
    first = None
    for attempt in range(12):
        request = urllib.request.Request(base + '/api/discussions/engagement-display/simulation',
            headers={'X-Kinshout-Client-Token': token, 'Origin': 'https://kinoiserie.com'})
        with urllib.request.urlopen(request, timeout=30) as response:
            report = json.load(response)
        heartbeat = report.get('heartbeatAt')
        active = report['enabled'] and report['flags']['generationEnabled'] and report['flags']['publishingEnabled'] and report['activeRuns'] > 0
        if not active:
            time.sleep(15)
            continue
        if first is None:
            first = heartbeat
        elif heartbeat and heartbeat != first and report['activeSimulatedProfiles'] >= 1500:
            stamp = datetime.datetime.fromisoformat(heartbeat.rstrip('Z')).replace(tzinfo=datetime.timezone.utc)
            if (datetime.datetime.now(datetime.timezone.utc) - stamp).total_seconds() < 180:
                print('REPORT', json.dumps(report))
                print('VERIFIED: deployed commit healthy; durable scheduler heartbeat advanced')
                return
        time.sleep(15)
    print('REPORT', json.dumps(report))
    raise RuntimeError('No advancing production scheduler heartbeat was observed')


try:
    main()
except Exception as error:
    print('VERIFICATION FAILED:', str(error) if isinstance(error, RuntimeError) else type(error).__name__)
    sys.exit(1)
