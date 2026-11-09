from test_support import call, click, change, ui, reset, wait_for


def check_tray_menu():
    idle = ['Открыть главное окно', 'Настройки', 'Выключить через', 'Гибернация через', 'Выход']
    assert call('v1/test/ui')['trayMenu'] == idle
    first_interval = call('v1/settings')['settings']['presetMinutes'][0] * 60
    for mode in ('shutdown', 'hibernate'):
        call('v1/test/interaction', {'window': 'main', 'action': 'trayPreset', 'mode': mode})
        wait_for(lambda: call('v1/status')['state'] == 'running')
        status = call('v1/status')
        assert status['action'] == mode and status['totalSeconds'] == first_interval
        active = ['Открыть главное окно', 'Показать виджет', 'Настройки', 'Пауза', 'Отменить таймер', 'Выход']
        assert call('v1/test/ui')['trayMenu'] == active
        call('v1/pause', {})
        active[3] = 'Продолжить'
        assert call('v1/test/ui')['trayMenu'] == active
        call('v1/timer', method='DELETE')
        assert call('v1/test/ui')['trayMenu'] == idle


def main():
    reset()
    check_tray_menu()
    idle = ui()
    assert not idle['TimerCard']['visible'] and idle['StartButton']['visible']
    assert not idle['ActiveActions']['visible'] and not idle['WidgetButton']['visible']
    selected = click('main', 'PresetButton3')
    assert selected['state'] == 'idle' and selected['selectedSeconds'] == call('v1/settings')['settings']['presetMinutes'][3] * 60
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
    opened = call('v1/test/interaction', {'window': 'widget', 'action': 'doubleClick', 'control': 'WidgetDrag'})
    assert opened['ui'] == 'show' and opened['state'] == 'paused'
    assert opened['action'] == state['action'] and opened['remainingSeconds'] == state['remainingSeconds']
    click('main', 'WidgetButton')
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
    change('main', 'PowerActionSelect', 'select', 'hibernate')
    assert click('main', 'StartButton')['action'] == 'hibernate'
    hibernating = ui()
    assert hibernating['StateText']['value'] == 'ДО ГИБЕРНАЦИИ'
    assert hibernating['PowerActionSelect']['value'] == 'hibernate' and not hibernating['PowerActionSelect']['enabled']
    call('v1/test/render', {'suffix': 'hibernate'})
    assert click('main', 'PauseButton')['action'] == 'hibernate'
    assert click('main', 'PauseButton')['state'] == 'running'
    click('main', 'CancelButton')
    assert ui()['PowerActionSelect']['enabled']
    change('main', 'PowerActionSelect', 'select', 'shutdown')
    call('v1/test/render', {'suffix': 'idle'})
    print('PASS: tray states and both tray presets, action selector, start, pause, resume, cancel, custom dialog, widget double-click and controls.')


if __name__ == '__main__':
    main()
