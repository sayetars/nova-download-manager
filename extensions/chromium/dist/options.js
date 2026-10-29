/* Nova extension options — vanilla JS, shared by Chromium and Firefox builds. */
'use strict';

const api = typeof browser !== 'undefined' ? browser : chrome;
const $ = (id) => document.getElementById(id);

// ---- v1.7.0 version-integrity About section (EN/FA) ---------------------------

const MIN_ENGINE_VERSION = '1.7.1'; // stamped from version.json by scripts/sync_versions.py

const I18N = {
  en: {
    aboutTitle: 'About & Engine status',
    extensionVersion: 'Extension version',
    engineVersion: 'Engine version',
    requiredEngine: 'Required engine',
    protocolVersion: 'IPC protocol',
    aboutNote: 'Versions come from the engine over native messaging. If the engine is older than the required version, update Nova Download Manager so extension and engine stay in lockstep.',
    freeNote: 'Nova is 100% free — every feature, no payment, ever.',
    okBanner: 'Engine connected and up to date — extension and engine are in lockstep.',
    oldBanner: 'Engine is outdated: update Nova Download Manager (engine %s, required %s) so versions do not get mixed.',
    offBanner: 'Engine not running. Start Nova Download Manager, then reopen this page.',
  },
  fa: {
    aboutTitle: 'درباره و وضعیت موتور',
    extensionVersion: 'نسخهٔ افزونه',
    engineVersion: 'نسخهٔ موتور',
    requiredEngine: 'حداقل موتور لازم',
    protocolVersion: 'نسخهٔ پروتکل',
    aboutNote: 'نسخه‌ها از موتور و از مسیر پیام‌رسانی بومی دریافت می‌شوند. اگر موتور قدیمی‌تر از نسخهٔ لازم باشد، نوا دانلود منیجر را به‌روزرسانی کنید تا نسخه‌ها با هم قاطی نشوند.',
    freeNote: 'نوا ۱۰۰٪ رایگان است — همهٔ امکانات، بدون هیچ پرداختی، برای همیشه.',
    okBanner: 'موتور متصل و به‌روز است — افزونه و موتور هم‌نسخه هستند.',
    oldBanner: 'موتور قدیمی است: نوا دانلود منیجر را به‌روزرسانی کنید (موجود %s، لازم %s).',
    offBanner: 'موتور در حال اجرا نیست. نوا دانلود منیجر را اجرا کنید و این صفحه را دوباره باز کنید.',
  },
};

function i18nLang() {
  return ((navigator.language || 'en').toLowerCase().startsWith('fa')) ? 'fa' : 'en';
}

function applyI18n() {
  const t = I18N[i18nLang()];
  document.querySelectorAll('[data-i18n]').forEach((el) => { el.textContent = t[el.dataset.i18n] || el.textContent; });
  document.documentElement.dir = i18nLang() === 'fa' ? 'rtl' : 'ltr';
}

function cmpVersions(a, b) {
  const pa = String(a || '').split(/[+-]/)[0].split('.').map((n) => parseInt(n, 10) || 0);
  const pb = String(b || '').split(/[+-]/)[0].split('.').map((n) => parseInt(n, 10) || 0);
  for (let i = 0; i < 3; i++) {
    if ((pa[i] || 0) < (pb[i] || 0)) return -1;
    if ((pa[i] || 0) > (pb[i] || 0)) return 1;
  }
  return 0;
}

async function loadEngineStatus() {
  const t = I18N[i18nLang()];
  const banner = $('versionBanner');
  try {
    $('extVersion').textContent = api.runtime.getManifest().version;
  } catch { /* ignore */ }
  $('minEngine').textContent = MIN_ENGINE_VERSION;

  let status = null;
  try {
    status = await api.runtime.sendMessage({ type: 'engine-status' });
  } catch { /* engine or background unavailable */ }

  if (status && status.ok && status.data) {
    const d = status.data;
    $('engineVersion').textContent = d.engine_version || '—';
    $('protoVersion').textContent = d.protocol_version != null ? String(d.protocol_version) : '—';
    if (d.engine_version && cmpVersions(d.engine_version, MIN_ENGINE_VERSION) < 0) {
      banner.className = 'version-banner warn';
      banner.textContent = t.oldBanner.replace('%s', d.engine_version).replace('%s', MIN_ENGINE_VERSION);
    } else {
      banner.className = 'version-banner ok';
      banner.textContent = t.okBanner;
    }
  } else {
    banner.className = 'version-banner off';
    banner.textContent = t.offBanner;
  }
  banner.style.display = 'block';
}

// ---- settings -------------------------------------------------------------------

const DEFAULTS = {
  enabled: true,
  showOnHover: true,
  interceptDownloads: false,
  preferredQuality: 'ask',
  allowlist: [],
  blocklist: [],
  interceptKey: 'alt',
  preventKey: 'none',
  passCookies: false,
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
    $('passCookies').checked = s.passCookies === true;
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
    passCookies: $('passCookies').checked === true,
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

document.addEventListener('DOMContentLoaded', () => {
  applyI18n();
  load();
  loadEngineStatus();
});
for (const id of ['enabled', 'showOnHover', 'interceptDownloads', 'preferredQuality', 'interceptKey', 'preventKey', 'passCookies', 'allowlist', 'blocklist']) {
  $(id).addEventListener('change', save);
  $(id).addEventListener('input', save);
}
