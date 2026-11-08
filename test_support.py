"""Shared helpers restricted to the harmless --test application."""
import json
import os
from pathlib import Path
import time
import urllib.request
from urllib.parse import urlparse

FOLDER = Path(os.environ['LOCALAPPDATA']) / 'PowerTimer-Test'
EXE = Path(__file__).resolve().parent / 'bin' / 'PowerTimer.exe'
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def connection():
    info = json.loads((FOLDER / 'api.json').read_text(encoding='utf-8-sig'))
    address = urlparse(info['baseUrl'])
    assert info['testMode'] is True and address.scheme == 'http' and address.hostname == '127.0.0.1'
    return info


def call(route, data=None, method=None, timeout=5):
    info = connection()
    request = urllib.request.Request(
        info['baseUrl'] + route,
        data=json.dumps(data).encode() if data is not None else None,
        headers={'Authorization': 'Bearer ' + info['token'], 'Content-Type': 'application/json'},
        method=method or ('POST' if data is not None else 'GET'))
    with OPENER.open(request, timeout=timeout) as response:
        return json.loads(response.read())


def click(window, control):
    return call('v1/test/interaction', dict(window=window, control=control, action='click'))


def change(window, control, action, value):
    return call('v1/test/interaction', dict(window=window, control=control, action=action, value=value))


def ui(window='main'):
    return {c['name']: c for w in call('v1/test/ui')['windows'] if w['name'] == window for c in w['controls']}


def wait_for(predicate, timeout=10):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        try:
            value = predicate()
            if value:
                return value
        except (OSError, ValueError):
            pass
        time.sleep(.05)
    raise AssertionError('Timed out waiting for test application')


def reset():
    call('v1/ui', {'mode': 'tray'})
    call('v1/timer', method='DELETE')
    call('v1/ui', {'mode': 'show'})
