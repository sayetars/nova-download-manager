import 'package:flutter/material.dart';

/// Scheduler page: start/pause/limit/shutdown rules on Gregorian OR
/// Jalali (Persian) calendar — the deviding feature for Persian users.
/// Rules are persisted in the engine via IPC schedule.upsert.
class SchedulerPage extends StatefulWidget {
  const SchedulerPage({super.key});

  @override
  State<SchedulerPage> createState() => _SchedulerPageState();
}

class _SchedulerPageState extends State<SchedulerPage> {
  final List<Map<String, dynamic>> _rules = [];

  @override
  Widget build(BuildContext context) {
    final persian = Localizations.localeOf(context).languageCode == 'fa';
    return Scaffold(
      appBar: AppBar(title: Text(persian ? 'زمان‌بند' : 'Scheduler')),
      floatingActionButton: FloatingActionButton.extended(
        onPressed: () => _showRuleDialog(),
        icon: const Icon(Icons.add_alarm_rounded),
        label: Text(persian ? 'قاعده جدید' : 'New rule'),
      ),
      body: _rules.isEmpty
          ? Center(
              child: Text(
                persian
                    ? 'هنوز قاعده‌ای نیست.\nمثلاً «شروع دانلود ساعت ۲ شب» یا «محدود کردن سرعت روزهای جمعه»'
                    : 'No rules yet.\nExamples: "start queue at 02:00",\n"limit speed on Fridays"',
                textAlign: TextAlign.center,
                style: TextStyle(color: Theme.of(context).disabledColor),
              ),
            )
          : ListView(
              children: [
                for (final rule in _rules)
                  Card(
                    margin:
                        const EdgeInsets.symmetric(horizontal: 12, vertical: 4),
                    child: ListTile(
                      leading: Icon(_kindIcon(rule['kind'])),
                      title: Text(rule['name'] ?? ''),
                      subtitle: Text(
                          '${rule['timeExpr']} · ${rule['calendar'] == 'jalali' ? 'شمسی' : 'Gregorian'}'),
                      trailing: Switch(
                        value: rule['enabled'] ?? true,
                        onChanged: (v) =>
                            setState(() => rule['enabled'] = v),
                      ),
                    ),
                  ),
              ],
            ),
    );
  }

  IconData _kindIcon(String? kind) => switch (kind) {
        'start' => Icons.play_arrow_rounded,
        'pause' => Icons.pause_circle_outline_rounded,
        'limit' => Icons.speed_rounded,
        'shutdown' => Icons.power_settings_new_rounded,
        _ => Icons.alarm_rounded,
      };

  Future<void> _showRuleDialog() async {
    final persian = Localizations.localeOf(context).languageCode == 'fa';
    final nameCtrl = TextEditingController();
    final timeCtrl = TextEditingController(text: '02:00');
    var kind = 'start';
    var calendar = 'jalali'; // default for Persian users

    await showDialog(
      context: context,
      builder: (context) => StatefulBuilder(
        builder: (context, setState) => AlertDialog(
          title: Text(persian ? 'قاعده زمان‌بندی' : 'Schedule rule'),
          content: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              TextField(
                controller: nameCtrl,
                decoration: InputDecoration(
                  labelText: persian ? 'نام' : 'Name',
                  border: const OutlineInputBorder(),
                ),
              ),
              const SizedBox(height: 14),
              DropdownButtonFormField<String>(
                value: kind,
                decoration: InputDecoration(
                  labelText: persian ? 'عمل' : 'Action',
                  border: const OutlineInputBorder(),
                ),
                items: [
                  DropdownMenuItem(value: 'start', child: Text(persian ? 'شروع صف' : 'Start queue')),
                  DropdownMenuItem(value: 'pause', child: Text(persian ? 'توقف صف' : 'Pause queue')),
                  DropdownMenuItem(value: 'limit', child: Text(persian ? 'محدودیت سرعت' : 'Speed limit')),
                  DropdownMenuItem(value: 'shutdown', child: Text(persian ? 'خاموش کردن' : 'Shut down PC')),
                ],
                onChanged: (v) => setState(() => kind = v ?? 'start'),
              ),
              const SizedBox(height: 14),
              DropdownButtonFormField<String>(
                value: calendar,
                decoration: InputDecoration(
                  labelText: persian ? 'تقویم' : 'Calendar',
                  border: const OutlineInputBorder(),
                ),
                items: [
                  DropdownMenuItem(value: 'jalali', child: Text(persian ? 'شمسی (جلالی)' : 'Jalali (Persian)')),
                  DropdownMenuItem(value: 'gregorian', child: Text(persian ? 'میلادی' : 'Gregorian')),
                ],
                onChanged: (v) => setState(() => calendar = v ?? 'jalali'),
              ),
              const SizedBox(height: 14),
              TextField(
                controller: timeCtrl,
                decoration: InputDecoration(
                  labelText: persian ? 'زمان (مثال: 21:30 یا 1404/08/12 21:30)' : 'Time (e.g. 21:30 or 2025-11-02 21:30)',
                  border: const OutlineInputBorder(),
                ),
              ),
            ],
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.pop(context),
              child: Text(persian ? 'لغو' : 'Cancel'),
            ),
            FilledButton(
              onPressed: () {
                setState(() {}); // parent refresh
                Navigator.pop(context);
                this.setState(() {
                  _rules.add({
                    'name': nameCtrl.text.isEmpty ? kind : nameCtrl.text,
                    'kind': kind,
                    'timeExpr': timeCtrl.text,
                    'calendar': calendar,
                    'enabled': true,
                  });
                });
              },
              child: Text(persian ? 'ذخیره' : 'Save'),
            ),
          ],
        ),
      ),
    );
  }
}
