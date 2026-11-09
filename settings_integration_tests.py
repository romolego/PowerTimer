from test_support import call, click, change, ui, reset
from urllib.error import HTTPError

EDITABLE = ('theme', 'defaultAction', 'addSeconds', 'presetMinutes', 'keepAwake',
            'closeToTray', 'minimizeToTray', 'startWithWindows', 'startInTray',
            'notifyBeforeShutdown', 'widgetTopmost', 'widgetSize', 'widgetOpacity', 'rememberWidgetPosition')


def main():
    reset()
    before = call('v1/settings')['settings']
    target_action = 'hibernate' if before['defaultAction'] == 'shutdown' else 'shutdown'
    try:
        assert len(before['presetMinutes']) == 7
        click('main', 'PresetButton7')
        for value in ['0', '10081', 'abc', 'NaN', '1,2.3']:
            change('custom', 'CustomValue', 'text', value)
            click('custom', 'CustomAccept')
            assert call('v1/status')['customOpen'] and ui('custom')['CustomError']['visible']
        click('custom', 'CustomCancel')
        for value, unit, seconds in [('3,5', 'hours', 12600), ('3.5', 'hours', 12600),
                                     ('0,5', 'hours', 1800), ('1,5', 'minutes', 90)]:
            click('main', 'PresetButton7')
            change('custom', 'CustomValue', 'text', value)
            change('custom', 'CustomUnit', 'select', unit)
            click('custom', 'CustomAccept')
            assert not call('v1/status')['customOpen'] and call('v1/status')['selectedSeconds'] == seconds
            click('main', 'StartButton')
            assert call('v1/status')['remainingSeconds'] == seconds
            click('main', 'CancelButton')
        call('v1/ui', {'mode': 'settings'})
        original_theme = call('v1/status')['effectiveTheme']
        preview = 'dark' if original_theme == 'light' else 'light'
        change('settings', 'ThemeSelect', 'select', preview)
        change('settings', 'DefaultActionSelect', 'select', target_action)
        assert call('v1/status')['effectiveTheme'] == preview
        try:
            call('v1/settings', {'theme': preview})
            raise AssertionError('API overwrote an open settings draft')
        except HTTPError as error:
            assert error.code == 409
        click('settings', 'CancelSettingsButton')
        assert call('v1/status')['effectiveTheme'] == original_theme
        assert call('v1/settings')['settings']['defaultAction'] == before['defaultAction']
        call('v1/ui', {'mode': 'settings'})
        new_minutes = 17 if before['presetMinutes'][0] != 17 else 19
        change('settings', 'Preset0', 'text', str(new_minutes))
        change('settings', 'DefaultActionSelect', 'select', target_action)
        call('v1/test/render', {'suffix': 'defaults'})
        click('settings', 'SaveSettingsButton')
        assert not call('v1/status')['settingsOpen']
        saved = call('v1/settings')['settings']
        assert saved['presetMinutes'] == [new_minutes] + before['presetMinutes'][1:]
        assert saved['defaultSeconds'] == new_minutes * 60 and saved['defaultAction'] == target_action
        selected = call('v1/status')
        assert selected['state'] == 'idle' and selected['selectedIndex'] == 0
        assert selected['selectedSeconds'] == new_minutes * 60 and selected['action'] == target_action, selected
        assert click('main', 'StartButton')['action'] == target_action
        call('v1/settings', {'defaultSeconds': 14400, 'defaultAction': before['defaultAction']})
        running = call('v1/status')
        assert new_minutes * 60 - 5 <= running['remainingSeconds'] <= new_minutes * 60
        assert running['action'] == target_action
        click('main', 'WidgetButton')
        for size, expected in [('small', 174), ('medium', 209), ('large', 244)]:
            call('v1/settings', {'widgetSize': size})
            assert abs(call('v1/status')['widgetWidth'] - expected) < 2
        call('v1/settings', {'widgetOpacity': 75, 'widgetTopmost': False})
        assert call('v1/status')['widgetOpacity'] == .75 and not call('v1/status')['widgetTopmost']
        click('widget', 'WidgetExpandButton')
        click('widget', 'WidgetCancel')
        selected = call('v1/status')
        assert selected['ui'] == 'show' and selected['state'] == 'idle'
        assert selected['action'] == before['defaultAction'] and selected['selectedSeconds'] == 14400 and selected['selectedIndex'] == 0
        assert not ui()['TimerCard']['visible']
        print('PASS: decimal durations, validation, theme preview/cancel, first preset and default action, active timer preservation, widget settings.')
    finally:
        call('v1/ui', {'mode': 'tray'})
        call('v1/timer', method='DELETE')
        call('v1/settings', {key: before[key] for key in EDITABLE})


if __name__ == '__main__':
    main()
