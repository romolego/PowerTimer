from test_support import call, click, change, ui, reset


def main():
    reset()
    idle = ui()
    assert not idle['TimerCard']['visible'] and idle['StartButton']['visible']
    assert not idle['ActiveActions']['visible'] and not idle['WidgetButton']['visible']
    selected = click('main', 'PresetButton3')
    assert selected['state'] == 'idle' and selected['selectedSeconds'] == 3600
    click('main', 'StartButton')
    running = ui()
    assert running['TimerCard']['visible'] and running['ActiveActions']['visible']
    assert running['WidgetButton']['visible'] and not running['Presets']['visible']
    assert not running['StartButton']['visible']
    click('main', 'PauseButton')
    assert call('v1/status')['state'] == 'paused'
    call('v1/test/render', {'suffix': 'active'})
    click('main', 'WidgetButton')
    state = call('v1/status')
    assert state['ui'] == 'widget' and state['widgetHeight'] < 65 and not state['widgetExpanded']
    click('widget', 'WidgetExpandButton')
    click('widget', 'WidgetPause')
    assert call('v1/status')['state'] == 'running'
    click('widget', 'WidgetPause')
    assert call('v1/status')['state'] == 'paused'
    click('widget', 'WidgetOpenMain')
    click('main', 'CancelButton')
    assert call('v1/status')['state'] == 'idle'
    click('main', 'PresetButton7')
    change('custom', 'CustomValue', 'text', '95')
    click('custom', 'CustomAccept')
    assert call('v1/status')['selectedSeconds'] == 5700 and call('v1/status')['state'] == 'idle'
    click('main', 'StartButton')
    assert call('v1/status')['remainingSeconds'] == 5700
    click('main', 'CancelButton')
    click('main', 'PresetButton7')
    click('custom', 'CustomCancel')
    assert call('v1/status')['selectedSeconds'] == 5700
    call('v1/test/render', {'suffix': 'idle'})
    print('PASS: selection, start, pause, resume, cancel, custom dialog, widget, rendered windows.')


if __name__ == '__main__':
    main()
