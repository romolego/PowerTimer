from test_support import call, click, change, ui, reset
from urllib.error import HTTPError

EDITABLE = ('theme', 'defaultSeconds', 'addSeconds', 'presetMinutes', 'keepAwake',
            'closeToTray', 'minimizeToTray', 'startWithWindows', 'startInTray',
            'notifyBeforeShutdown', 'widgetTopmost', 'widgetSize', 'widgetOpacity', 'rememberWidgetPosition')


def main():
    reset()
    before = call('v1/settings')['settings']
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
        assert call('v1/status')['effectiveTheme'] == preview
        try:
            call('v1/settings', {'theme': preview})
            raise AssertionError('API overwrote an open settings draft')
        except HTTPError as error:
            assert error.code == 409
        click('settings', 'CancelSettingsButton')
        assert call('v1/status')['effectiveTheme'] == original_theme
        call('v1/ui', {'mode': 'settings'})
        change('settings', 'Preset0', 'text', '17')
        change('settings', 'DefaultHours', 'text', '3')
        click('settings', 'SaveSettingsButton')
        assert not call('v1/status')['settingsOpen']
        assert call('v1/settings')['settings']['presetMinutes'][0] == 17
        selected = click('main', 'PresetButton0')
        assert selected['state'] == 'idle' and selected['selectedSeconds'] == 1020
        click('main', 'StartButton')
        call('v1/settings', {'defaultSeconds': 14400})
        assert 1015 <= call('v1/status')['remainingSeconds'] <= 1020
        click('main', 'WidgetButton')
        for size, expected in [('small', 174), ('medium', 209), ('large', 244)]:
            call('v1/settings', {'widgetSize': size})
            assert abs(call('v1/status')['widgetWidth'] - expected) < 2
        call('v1/settings', {'widgetOpacity': 75, 'widgetTopmost': False})
        assert call('v1/status')['widgetOpacity'] == .75 and not call('v1/status')['widgetTopmost']
        click('widget', 'WidgetExpandButton')
        click('widget', 'WidgetCancel')
        assert call('v1/status')['ui'] == 'show' and call('v1/status')['state'] == 'idle'
        assert not ui()['TimerCard']['visible']
        print('PASS: decimal durations, validation, theme preview/cancel, saved presets, active timer preservation, widget settings.')
    finally:
        call('v1/ui', {'mode': 'tray'})
        call('v1/timer', method='DELETE')
        call('v1/settings', {key: before[key] for key in EDITABLE})


if __name__ == '__main__':
    main()
