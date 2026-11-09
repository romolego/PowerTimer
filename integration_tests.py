"""Integration checks against ONLY the harmless PowerTimer.exe --test instance.
No production endpoints or Windows power commands are used.
"""
import concurrent.futures
import json
import os
from pathlib import Path
import socket
import time
import urllib.error
import urllib.request

folder = Path(os.environ['LOCALAPPDATA']) / 'PowerTimer-Test'
info = json.loads((folder / 'api.json').read_text(encoding='utf-8-sig'))
assert info['testMode'] is True, 'Refusing to test a production instance'
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
checks = 0


def request(method, route, data=None, auth=True, extra=None, raw=None):
    headers = {'Content-Type': 'application/json'}
    if auth:
        headers['Authorization'] = 'Bearer ' + info['token']
    headers.update(extra or {})
    payload = raw if raw is not None else json.dumps(data).encode() if data is not None else None
    req = urllib.request.Request(info['baseUrl'] + route, data=payload, headers=headers, method=method)
    try:
        with opener.open(req, timeout=5) as response:
            return response.status, json.loads(response.read())
    except urllib.error.HTTPError as error:
        return error.code, json.loads(error.read())


def expect(value, label):
    global checks
    checks += 1
    assert value, label


expect(request('GET', 'v1/status')[1]['testMode'] is True, 'test mode')
expect(request('GET', 'v1/status', auth=False)[0] == 401, 'authentication required')
expect(request('GET', 'v1/status', extra={'Authorization': 'Bearer invalid'})[0] == 401, 'invalid key')
expect(request('GET', 'v1/status', extra={'Origin': 'http://evil.example'})[0] == 403, 'browser origin rejected')
expect(request('GET', 'v1/status', extra={'Host': 'evil.example'})[0] == 403, 'DNS rebinding host rejected')
expect(request('GET', 'v1/no-such-route')[0] == 404, 'unknown route')
request('DELETE', 'v1/timer')
expect(request('POST', 'v1/pause', {})[0] == 409, 'pause idle conflict')
expect(request('POST', 'v1/resume', {})[0] == 409, 'resume idle conflict')
expect(request('POST', 'v1/extend', {'seconds': 60})[0] == 409, 'extend idle conflict')
for value in (0, -1, 604801, '60', True, None):
    expect(request('POST', 'v1/timer', {'seconds': value})[0] == 400, 'invalid seconds ' + str(value))
expect(request('POST', 'v1/timer', raw=b'{broken')[0] == 400, 'malformed JSON')
expect(request('POST', 'v1/timer', raw=b'[]')[0] == 400, 'JSON array rejected')
expect(request('POST', 'v1/timer', {'seconds': 60}, extra={'Content-Type': 'text/plain'})[0] == 415, 'JSON content type required')
for seconds in (600, 900, 1800, 3600, 7200, 10800, 14400, 18000):
    code, result = request('POST', 'v1/timer', {'seconds': seconds})
    expect(code == 200 and result['state'] == 'running' and result['remainingSeconds'] == seconds and result['action'] == 'shutdown', 'shutdown preset ' + str(seconds))
time.sleep(1.1)
expect(request('GET', 'v1/status')[1]['remainingSeconds'] < 18000, 'countdown advances')
paused = request('POST', 'v1/pause', {})[1]
time.sleep(.4)
expect(request('GET', 'v1/status')[1]['remainingSeconds'] == paused['remainingSeconds'], 'pause freezes')
extended = request('POST', 'v1/extend', {'seconds': 600})[1]
expect(extended['remainingSeconds'] == paused['remainingSeconds'] + 600, 'extend paused')
expect(request('POST', 'v1/resume', {})[1]['state'] == 'running', 'resume')
expect(request('DELETE', 'v1/timer')[1]['state'] == 'idle', 'cancel')
request('POST', 'v1/timer', {'seconds': 604800})
expect(request('POST', 'v1/extend', {'seconds': 600})[0] == 400, 'maximum enforced on extend')
request('DELETE', 'v1/timer')
request('POST', 'v1/timer', {'seconds': 300})
for mode in ('widget', 'tray', 'show'):
    result = request('POST', 'v1/ui', {'mode': mode})[1]
    expect(result['ui'] == mode, 'UI ' + mode)
request('DELETE', 'v1/timer')
expect(request('POST', 'v1/ui', {'mode': 'invalid'})[0] == 400, 'invalid UI mode')
with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
    results = list(pool.map(lambda _: request('GET', 'v1/status')[0], range(16)))
expect(all(code == 200 for code in results), 'concurrent readers')
port = int(info['baseUrl'].split(':')[-1].strip('/'))
with socket.create_connection(('127.0.0.1', port), timeout=5) as connection:
    connection.sendall((f'POST /v1/timer HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nAuthorization: Bearer {info["token"]}\r\nContent-Length: 20000\r\nContent-Type: application/json\r\n\r\n').encode())
    expect(b'413 Payload Too Large' in connection.recv(4096), 'oversized body rejected')


def firings(command='TEST: would request shutdown'):
    log = folder / 'activity.log'
    return log.read_text(encoding='utf-8-sig').count(command) if log.exists() else 0


baseline = firings()
request('POST', 'v1/timer', {'seconds': 1})
time.sleep(1.6)
expect(request('GET', 'v1/status')[1]['state'] == 'completed', 'due action completed')
expect(firings() == baseline + 1, 'fake power action once')
time.sleep(.5)
expect(firings() == baseline + 1, 'no repeated power action')
request('POST', 'v1/timer', {'seconds': 1})
request('DELETE', 'v1/timer')
time.sleep(1.4)
expect(firings() == baseline + 1, 'cancellation suppresses action')
expect(request('POST', 'v1/timer', {'seconds': 1, 'action': 'hibernate'})[0] == 400, 'hibernation must use the explicit endpoint')
expect(request('GET', 'v1/status')[1]['state'] == 'idle', 'mismatched action did not start a timer')
hibernate_baseline = firings('TEST: would request shutdown /h')
code, result = request('POST', 'v1/timer/hibernate', {'seconds': 1})
expect(code == 200 and result['action'] == 'hibernate', 'hibernation scheduled')
time.sleep(1.6)
completed = request('GET', 'v1/status')[1]
expect(completed['state'] == 'completed' and completed['action'] == 'hibernate', 'hibernation reached deadline')
expect(firings('TEST: would request shutdown /h') == hibernate_baseline + 1 and firings() == baseline + 2,
       'hibernation invokes /h exactly once')
code, result = request('POST', 'v1/timer', {'seconds': 600})
expect(code == 200 and result['action'] == 'shutdown', 'legacy API still schedules shutdown after hibernation')
request('DELETE', 'v1/timer')
print(f'PASS: {checks} API integration checks. Only the harmless test instance was used.')
