import shutil
import subprocess
from tempfile import TemporaryDirectory
from test_support import call, click, ui, reset, connection, EXE


def main():
    reset()
    original_pid = connection()['pid']
    call('v1/timer', {'seconds': 600})
    call('v1/ui', {'mode': 'widget'})
    call('v1/widget', {'expanded': True})
    call('v1/ui', {'mode': 'tray'})
    remaining = call('v1/status')['remainingSeconds']
    children = [subprocess.Popen([str(EXE), '--test'], creationflags=subprocess.CREATE_NO_WINDOW) for _ in range(3)]
    for child in children:
        assert child.wait(timeout=12) == 0
    state = call('v1/status')
    assert connection()['pid'] == original_pid
    assert state['ui'] == 'widget' and not state['widgetExpanded']
    assert state['state'] == 'running' and remaining - 15 <= state['remainingSeconds'] <= remaining
    with TemporaryDirectory(prefix='PowerTimer-instance-') as directory:
        other = shutil.copy2(EXE, directory)
        call('v1/ui', {'mode': 'tray'})
        child = subprocess.Popen([other, '--test'], creationflags=subprocess.CREATE_NO_WINDOW)
        assert child.wait(timeout=12) == 0
    assert connection()['pid'] == original_pid and call('v1/status')['ui'] == 'widget'
    call('v1/widget', {'expanded': True})
    click('widget', 'WidgetPause')
    assert call('v1/status')['state'] == 'paused'
    click('widget', 'WidgetExpandButton')
    assert not call('v1/status')['widgetExpanded']
    click('widget', 'WidgetExpandButton')
    click('widget', 'WidgetOpenMain')
    assert not ui()['Presets']['visible']
    click('main', 'CancelButton')
    assert ui()['Presets']['visible']
    # A repeat launch while the custom dialog is open restores that dialog.
    click('main', 'PresetButton7')
    child = subprocess.Popen([str(EXE), '--test'], creationflags=subprocess.CREATE_NO_WINDOW)
    assert child.wait(timeout=12) == 0 and call('v1/status')['customOpen']
    click('custom', 'CustomCancel')
    print('PASS: simultaneous launches, alternate executable copy, tray restore, dialog restore, original PID and countdown preserved.')


if __name__ == '__main__':
    main()
