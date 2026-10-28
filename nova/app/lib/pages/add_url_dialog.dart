import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../services/app_model.dart';

/// IDM-style "Add URL" dialog with filename / category / queue pickers.
Future<void> showAddUrlDialog(BuildContext context) async {
  final urlCtrl = TextEditingController();
  var category = 'general';
  final persian = Localizations.localeOf(context).languageCode == 'fa';

  await showDialog(
    context: context,
    builder: (context) => StatefulBuilder(
      builder: (context, setState) => AlertDialog(
        title: Text(persian ? 'افزودن لینک' : 'Add URL'),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            TextField(
              controller: urlCtrl,
              autofocus: true,
              decoration: InputDecoration(
                labelText: persian ? 'لینک دانلود' : 'Download URL',
                hintText: 'https://…',
                border: const OutlineInputBorder(),
              ),
            ),
            const SizedBox(height: 16),
            DropdownButtonFormField<String>(
              value: category,
              decoration: InputDecoration(
                labelText: persian ? 'دسته' : 'Category',
                border: const OutlineInputBorder(),
              ),
              items: const [
                DropdownMenuItem(value: 'general', child: Text('General')),
                DropdownMenuItem(value: 'video', child: Text('Video')),
                DropdownMenuItem(value: 'music', child: Text('Music')),
                DropdownMenuItem(value: 'documents', child: Text('Documents')),
                DropdownMenuItem(value: 'compressed', child: Text('Compressed')),
                DropdownMenuItem(value: 'programs', child: Text('Programs')),
              ],
              onChanged: (v) => setState(() => category = v ?? 'general'),
            ),
          ],
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context),
            child: Text(persian ? 'لغو' : 'Cancel'),
          ),
          FilledButton(
            onPressed: () async {
              final url = urlCtrl.text.trim();
              if (url.isEmpty) return;
              Navigator.pop(context);
              try {
                await context.read<AppModel>().addUrl(url, category: category);
              } catch (e) {
                if (context.mounted) {
                  ScaffoldMessenger.of(context).showSnackBar(
                    SnackBar(content: Text('$e')),
                  );
                }
              }
            },
            child: Text(persian ? 'افزودن' : 'Add'),
          ),
        ],
      ),
    ),
  );
}
