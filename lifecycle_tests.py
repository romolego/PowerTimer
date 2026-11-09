"""Crash/restart, blocked dispatcher, expiry and exit-race regression checks."""
import concurrent.futures
import ctypes
from ctypes import wintypes
import json
import subprocess
import time
from urllib.error import HTTPError
from test_support import call, connection, wait_for, reset, FOLDER, EXE

kernel = ctypes.WinDLL('kernel32', use_last_error=True)
kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
kernel.OpenProcess.restype = wintypes.HANDLE
kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
kernel.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
kernel.CloseHandle.argtypes = [wintypes.HANDLE]


def stop(force=False):
    info = connection()
    handle = kernel.OpenProcess(0x100000 | 0x1000 | 1, False, info['pid'])
    assert handle
    try:
        length = wintypes.DWORD(32768)
        path = ctypes.create_unicode_buffer(length.value)
        assert kernel.QueryFullProcessImageNameW(handle, 0, path, ctypes.byref(length))
        assert path.value.casefold() == str(EXE).casefold(), 'Refusing to stop another executable'
        if force:
            assert kernel.TerminateProcess(handle, 99)
        else:
            call('v1/app/exit', {})
        assert kernel.WaitForSingleObject(handle, 5000) == 0
    finally:
        kernel.CloseHandle(handle)


def start():
    child = subprocess.Popen([str(EXE), '--test', '--tray'], creationflags=subprocess.CREATE_NO_WINDOW)
    wait_for(lambda: connection()['pid'] == child.pid and call('v1/status')['testMode'])
    return child


def firings():
    log = FOLDER / 'activity.log'
    return log.read_text(encoding='utf-8-sig').count('TEST: would request shutdown') if log.exists() else 0


def main():
    reset()
    baseline = firings()
    old_token = connection()['token']
    call('v1/timer/hibernate', {'seconds': 600})
    stop(force=True)
    start()
    state = call('v1/status')
    assert state['state'] == 'paused' and state['action'] == 'hibernate' and 580 <= state['remainingSeconds'] <= 600
    assert connection()['token'] != old_token
    remaining = state['remainingSeconds']
    time.sleep(1.1)
    assert call('v1/status')['remainingSeconds'] == remaining
    assert call('v1/resume', {})['action'] == 'hibernate'
    time.sleep(1.1)
    assert call('v1/status')['remainingSeconds'] < remaining
    call('v1/timer', {'seconds': 1})
    stop(force=True)
    time.sleep(1.2)
    start()
    assert call('v1/status')['state'] == 'idle' and firings() == baseline
    # Malformed state and oversized settings must not prevent launching.
    stop()
    saved_settings = (FOLDER / 'settings.json').read_bytes()
    (FOLDER / 'timer.json').write_text('{broken', encoding='utf-8')
    (FOLDER / 'settings.json').write_text('x' * 65537, encoding='utf-8')
    start()
    assert call('v1/status')['state'] == 'idle'
    stop()
    (FOLDER / 'settings.json').write_bytes(saved_settings)
    start()
    # A queued mutation must not run much later than its caller intended.
    with concurrent.futures.ThreadPoolExecutor(max_workers=1) as pool:
        blocked = pool.submit(call, 'v1/test/interaction', {'window': 'main', 'action': 'stall', 'value': 4500}, timeout=10)
        time.sleep(.25)
        try:
            call('v1/timer', {'seconds': 600}, timeout=10)
            raise AssertionError('Stale queued mutation was applied')
        except HTTPError as error:
            assert error.code == 409
        blocked.result()
    assert call('v1/status')['state'] == 'idle'
    # Simulate a suspended/busy UI passing a deadline. It must cancel, not power off.
    call('v1/timer', {'seconds': 1})
    call('v1/test/interaction', {'window': 'main', 'action': 'stall', 'value': 16000}, timeout=25)
    wait_for(lambda: call('v1/status')['state'] == 'idle')
    assert firings() == baseline
    call('v1/test/interaction', {'window': 'main', 'action': 'exitRace'})
    wait_for(lambda: not (FOLDER / 'api.json').exists())
    assert json.loads((FOLDER / 'timer.json').read_text(encoding='utf-8-sig'))['state'] == 'idle'
    start()
    assert call('v1/status')['state'] == 'idle'
    print('PASS: hibernation crash recovery, expired timer discard, corrupt storage, token rotation, stale requests, stalled deadline, exit race.')


if __name__ == '__main__':
    main()
