import 'package:flutter/material.dart';

/// Settings page: general / speed / browser integration / video detection.
/// Writes through AppModel -> engine IPC settings.set.
class SettingsPage extends StatefulWidget {
  const SettingsPage({super.key});

  @override
  State<SettingsPage> createState() => _SettingsPageState();
}

class _SettingsPageState extends State<SettingsPage> {
  @override
  Widget build(BuildContext context) {
    final persian = Localizations.localeOf(context).languageCode == 'fa';
    return Scaffold(
      appBar: AppBar(title: Text(persian ? 'تنظیمات' : 'Settings')),
      body: ListView(
        children: [
          _section(persian ? 'عمومی' : 'General', [
            _SettingTile(
              icon: Icons.folder_outlined,
              title: persian ? 'ذخیره در' : 'Save to',
              trailing: const Text('Downloads'),
              onTap: () {},
            ),
            _SettingTile.slider(
              icon: Icons.layers_outlined,
              title: persian ? 'اتصال در هر دانلود' : 'Connections per download',
              value: 8,
              min: 1,
              max: 32,
              onChanged: (v) {},
            ),
            _SettingTile.slider(
              icon: Icons.copy_all_outlined,
              title: persian ? 'دانلود همزمان' : 'Simultaneous downloads',
              value: 3,
              min: 1,
              max: 10,
              onChanged: (v) {},
            ),
            _SettingTile.switch_(
              icon: Icons.content_paste_go_outlined,
              title: persian ? 'پایش کلیپ‌بورد' : 'Monitor clipboard',
              value: true,
              onChanged: (v) {},
            ),
            _SettingTile.switch_(
              icon: Icons.movie_filter_outlined,
              title: persian ? 'پیش‌نمایش ویدیو هنگام دانلود' : 'Preview video while downloading',
              value: true,
              onChanged: (v) {},
            ),
          ]),
          _section(persian ? 'یکپارچه‌سازی مرورگر' : 'Browser Integration', [
            _SettingTile(
              icon: Icons.extension_outlined,
              title: 'Chrome / Brave / Edge',
              trailing: _InstallBadge(installed: true),
              onTap: () {},
            ),
            _SettingTile(
              icon: Icons.local_fire_department_outlined,
              title: 'Firefox',
              trailing: _InstallBadge(installed: false),
              onTap: () {},
            ),
          ]),
          _section(persian ? 'تشخیص ویدیو' : 'Video Detection', [
            _SettingTile.switch_(
              icon: Icons.sensors_outlined,
              title: 'HLS (m3u8)',
              value: true,
              onChanged: (v) {},
            ),
            _SettingTile.switch_(
              icon: Icons.sensors_outlined,
              title: 'DASH (mpd)',
              value: true,
              onChanged: (v) {},
            ),
            _SettingTile.switch_(
              icon: Icons.videocam_outlined,
              title: persian ? 'ویدیوهای صفحه' : 'DOM <video> elements',
              value: true,
              onChanged: (v) {},
            ),
          ]),
        ],
      ),
    );
  }

  Widget _section(String title, List<Widget> children) => Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 20, 16, 4),
            child: Text(title,
                style: TextStyle(
                  fontSize: 13,
                  fontWeight: FontWeight.w700,
                  color: Theme.of(context).colorScheme.primary,
                )),
          ),
          Card(
            margin: const EdgeInsets.symmetric(horizontal: 12),
            elevation: 0,
            child: Column(children: children),
          ),
        ],
      );
}

class _InstallBadge extends StatelessWidget {
  final bool installed;
  const _InstallBadge({required this.installed});

  @override
  Widget build(BuildContext context) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
        decoration: BoxDecoration(
          color: installed
              ? Colors.green.withValues(alpha: .12)
              : Theme.of(context).colorScheme.primary.withValues(alpha: .12),
          borderRadius: BorderRadius.circular(999),
        ),
        child: Text(
          installed ? 'Installed' : 'Install',
          style: TextStyle(
            fontSize: 12,
            color: installed ? Colors.green.shade700 : Theme.of(context).colorScheme.primary,
          ),
        ),
      );
}

class _SettingTile extends StatelessWidget {
  final IconData icon;
  final String title;
  final Widget? trailing;
  final VoidCallback? onTap;

  // slider factory
  final double? _sliderValue;
  final double? _sliderMin, _sliderMax;
  final ValueChanged<double>? _onSlider;

  // switch factory
  final bool? _switchValue;
  final ValueChanged<bool>? _onSwitch;

  const _SettingTile({required this.icon, required this.title, this.trailing, this.onTap})
      : _sliderValue = null,
        _sliderMin = null,
        _sliderMax = null,
        _onSlider = null,
        _switchValue = null,
        _onSwitch = null;

  const _SettingTile.slider({
    required this.icon,
    required this.title,
    required double value,
    required double min,
    required double max,
    ValueChanged<double>? onChanged,
  })  : _sliderValue = value,
        _sliderMin = min,
        _sliderMax = max,
        _onSlider = onChanged,
        _switchValue = null,
        _onSwitch = null,
        trailing = null,
        onTap = null;

  const _SettingTile.switch_({
    required this.icon,
    required this.title,
    required bool value,
    required ValueChanged<bool> onChanged,
  })  : _switchValue = value,
        _onSwitch = onChanged,
        _sliderValue = null,
        _sliderMin = null,
        _sliderMax = null,
        _onSlider = null,
        trailing = null,
        onTap = null;

  @override
  Widget build(BuildContext context) {
    Widget? subtitle;
    if (_sliderValue != null) {
      subtitle = Row(
        children: [
          Expanded(
            child: Slider(
              value: _sliderValue!.clamp(_sliderMin!, _sliderMax!),
              min: _sliderMin!,
              max: _sliderMax!,
              divisions: ((_sliderMax! - _sliderMin!).round()),
              label: _sliderValue!.round().toString(),
              onChanged: _onSlider,
            ),
          ),
          Text(_sliderValue!.round().toString()),
        ],
      );
    }
    Widget? control;
    if (_switchValue != null) {
      control = Switch(value: _switchValue!, onChanged: _onSwitch);
    } else if (trailing != null) {
      control = trailing;
    }

    return ListTile(
      leading: Icon(icon),
      title: Text(title),
      subtitle: subtitle,
      trailing: control,
      onTap: onTap,
    );
  }
}
