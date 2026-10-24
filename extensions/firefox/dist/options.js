/* Nova extension options — vanilla JS, shared by Chromium and Firefox builds. */
'use strict';

const api = typeof browser !== 'undefined' ? browser : chrome;
const $ = (id) => document.getElementById(id);

const DEFAULTS = {
  enabled: true,
  showOnHover: true,
  interceptDownloads: false,
  preferredQuality: 'ask',
  allowlist: [],
  blocklist: [],
  interceptKey: 'alt',
  preventKey: 'none',
};

let saving = null;

function load() {
  api.storage.sync.get('novaSettings').then((raw) => {
    const s = { ...DEFAULTS, ...(raw && raw.novaSettings ? raw.novaSettings : {}) };
    $('enabled').checked = !!s.enabled;
    $('showOnHover').checked = !!s.showOnHover;
    $('interceptDownloads').checked = !!s.interceptDownloads;
    $('preferredQuality').value = s.preferredQuality;
    $('interceptKey').value = s.interceptKey;
    $('preventKey').value = s.preventKey;
    $('allowlist').value = (s.allowlist || []).join('\n');
    $('blocklist').value = (s.blocklist || []).join('\n');
  });
}

function save() {
  const s = {
    enabled: $('enabled').checked,
    showOnHover: $('showOnHover').checked,
    interceptDownloads: $('interceptDownloads').checked,
    preferredQuality: $('preferredQuality').value,
    interceptKey: $('interceptKey').value,
    preventKey: $('preventKey').value,
    allowlist: $('allowlist').value.split('\n').map((x) => x.trim()).filter(Boolean),
    blocklist: $('blocklist').value.split('\n').map((x) => x.trim()).filter(Boolean),
  };
  clearTimeout(saving);
  saving = setTimeout(() => {
    api.storage.sync.set({ novaSettings: s }).then(() => {
      const st = $('status');
      st.classList.add('show');
      setTimeout(() => st.classList.remove('show'), 1200);
    });
  }, 250);
}

document.addEventListener('DOMContentLoaded', load);
for (const id of ['enabled', 'showOnHover', 'interceptDownloads', 'preferredQuality', 'interceptKey', 'preventKey', 'allowlist', 'blocklist']) {
  $(id).addEventListener('change', save);
  $(id).addEventListener('input', save);
}
