import concurrent.futures
import ctypes
from ctypes import wintypes
import time
from test_support import call, connection, reset, FOLDER


def main():
    reset()
    diagnostics = call('v1/test/ui')
    assert not diagnostics['timerWakeupScheduled']
    stamps = [(FOLDER / name).stat().st_mtime_ns for name in ('settings.json', 'timer.json')]
    started = time.perf_counter()
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        states = list(pool.map(lambda _: call('v1/status')['state'], range(1000)))
    assert all(state == 'idle' for state in states)
    assert call('v1/test/ui')['tickCount'] == diagnostics['tickCount']
    assert stamps == [(FOLDER / name).stat().st_mtime_ns for name in ('settings.json', 'timer.json')]
    elapsed = time.perf_counter() - started
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.GetProcessHandleCount.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.OpenProcess(0x1000, False, connection()['pid'])
    assert handle
    before = call('v1/settings')['settings']
    try:
        call('v1/timer', {'seconds': 600})
        # Warm theme and menu paths before checking resource growth.
        for index in range(10):
            call('v1/settings', {'theme': 'light' if index % 2 else 'dark'})
        time.sleep(.2)
        initial = wintypes.DWORD()
        assert kernel.GetProcessHandleCount(handle, ctypes.byref(initial))
        for index in range(100):
            call('v1/pause', {})
            call('v1/settings', {'theme': 'light' if index % 2 else 'dark'})
            call('v1/resume', {})
        call('v1/pause', {})
        paused = call('v1/status')['remainingSeconds']
        diagnostic_pause = call('v1/test/ui')
        assert not diagnostic_pause['timerWakeupScheduled']
        time.sleep(1.2)
        final = wintypes.DWORD()
        assert kernel.GetProcessHandleCount(handle, ctypes.byref(final))
        assert final.value <= initial.value + 32, (initial.value, final.value)
        assert call('v1/status')['remainingSeconds'] == paused
        assert call('v1/test/ui')['tickCount'] == diagnostic_pause['tickCount']
        print(f'PASS: 1000 API reads in {elapsed:.2f}s, 100 settings/pause/resume cycles; handles {initial.value}->{final.value}; no idle polling or writes.')
    finally:
        kernel.CloseHandle(handle)
        call('v1/timer', method='DELETE')
        call('v1/settings', {'theme': before['theme']})


if __name__ == '__main__':
    main()
