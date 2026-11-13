from datetime import datetime, timedelta, timezone
from urllib.error import HTTPError

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


def check_schedule():
    before = call('v1/settings')['settings']
    try:
        click('main', 'SettingsButton')
        change('settings', 'DefaultTimingModeSelect', 'select', 'schedule')
        change('settings', 'ScheduleDateOption', 'select', 'date')
        change('settings', 'ScheduleTimeOption', 'select', 'now')
        assert not 'DefaultPresetExplanation' in ui('settings')
        click('settings', 'SaveSettingsButton')
        controls = ui()
        assert controls['ScheduleDate']['visible'] and not controls['Presets']['visible']
        assert 'SchedulePreview' not in controls
        if controls['ScheduleDate']['value'] == datetime.now().date().isoformat():
            assert not controls['StartButton']['enabled']
        call('v1/test/interaction', {'window': 'main', 'control': 'ScheduleDate', 'action': 'openCalendar'})
        last_day = (datetime.now() + timedelta(days=7)).date()
        change('main', 'ScheduleDate', 'date', last_day.isoformat())
        try:
            change('main', 'ScheduleDate', 'date', (last_day + timedelta(days=1)).isoformat())
        except HTTPError as error:
            assert error.code == 400
        else:
            raise AssertionError('Calendar accepted a date beyond the limit')
        assert ui()['ScheduleDate']['value'] == last_day.isoformat()
        if (datetime.now() + timedelta(days=7)).hour < 23:
            change('main', 'ScheduleClock', 'text', '23:59')
            assert not ui()['StartButton']['enabled']
            try:
                change('main', 'ScheduleClock', 'select', '23:45')
            except HTTPError as error:
                assert error.code == 400
            else:
                raise AssertionError('Dropdown offered a time beyond the limit')
        change('main', 'ScheduleClock', 'text', '25:00')
        assert not ui()['StartButton']['enabled']
        target = (datetime.now() + timedelta(days=1)).replace(hour=23, minute=37, second=0, microsecond=0)
        change('main', 'ScheduleDate', 'date', target.date().isoformat())
        change('main', 'ScheduleClock', 'dropdown', True)
        change('main', 'ScheduleClock', 'select', '23:30')
        change('main', 'ScheduleClock', 'dropdown', False)
        assert ui()['ScheduleClock']['value'] == '23:30' and ui()['StartButton']['enabled']
        change('main', 'ScheduleClock', 'text', target.strftime('%H:%M'))
        change('main', 'PowerActionSelect', 'select', 'hibernate')
        call('v1/test/render', {'suffix': 'schedule-input'})
        scheduled = click('main', 'StartButton')
        assert scheduled['state'] == 'running' and scheduled['action'] == 'hibernate'
        assert scheduled['timingMode'] == 'schedule'
        assert datetime.fromisoformat(scheduled['deadlineLocal']).replace(tzinfo=None) == target
        click('main', 'WidgetButton')
        assert ui('widget')['WidgetDeadline']['visible']
        call('v1/test/render', {'suffix': 'schedule-active'})
        moved = call('v1/test/interaction', {'window': 'widget', 'action': 'move', 'x': 0, 'y': 0})
        call('v1/ui', {'mode': 'settings'})
        reset_position = click('settings', 'ResetWidgetPosition')
        assert (reset_position['widgetLeft'], reset_position['widgetTop']) != (moved['widgetLeft'], moved['widgetTop'])
        assert reset_position['deadlineUtc'] == scheduled['deadlineUtc']
        click('settings', 'CancelSettingsButton')
        paused = call('v1/pause', {})
        assert paused['deadlineUtc'] == scheduled['deadlineUtc']
        resumed = call('v1/resume', {})
        assert resumed['deadlineUtc'] == scheduled['deadlineUtc']
        call('v1/timer', method='DELETE')
        call('v1/settings', {'scheduleTimeOnly': True, 'scheduleInitialTime': 'hour'})
        assert not ui()['ScheduleDate']['visible'] and ui()['ScheduleClock']['value'].endswith(':00')
        call('v1/settings', {'scheduleInitialTime': 'now'})
        assert not ui()['StartButton']['enabled']
        change('main', 'ScheduleClock', 'text', '00:00')
        assert ui()['ScheduleTimeLabel']['value'] == 'Время · завтра'
        api_target = (datetime.now(timezone.utc) + timedelta(minutes=10)).isoformat(timespec='seconds')
        via_api = call('v1/schedule', {'at': api_target, 'action': 'shutdown'})
        assert via_api['timingMode'] == 'schedule' and via_api['action'] == 'shutdown'
        try:
            call('v1/schedule', {'at': '2026-11-13T12:00:00', 'action': 'shutdown'})
        except HTTPError as error:
            assert error.code == 400
        else:
            raise AssertionError('Schedule accepted a timestamp without a time zone')
        assert call('v1/status')['deadlineUtc'] == via_api['deadlineUtc']
    finally:
        call('v1/timer', method='DELETE')
        call('v1/ui', {'mode': 'show'})
        change('main', 'TimingModeSelect', 'select', 'timer')
        change('main', 'PowerActionSelect', 'select', before['defaultAction'])
        call('v1/settings', {key: before[key] for key in ('defaultTimingMode', 'scheduleTimeOnly', 'scheduleInitialTime')})


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
    check_schedule()
    print('PASS: countdown and scheduled modes, date/time input and time-only defaults, tray, pause/resume, cancel and widget.')


if __name__ == '__main__':
    main()
