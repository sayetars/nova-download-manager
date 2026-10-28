// Nova popup: lists detected videos with quality pickers, toggles settings.
(() => {
  'use strict';

  const $ = (s) => document.querySelector(s);

  // bilingual strings (EN/FA) — RTL applied when fa
  const i18n = {
    en: {
      videos: 'Videos on this page', settings: 'Settings',
      intercept: 'Intercept browser downloads', sniff: 'Detect videos automatically',
      openApp: 'Open Nova app', scanWait: 'Scanning…', none: 'No videos detected yet',
      quality: 'Quality', download: 'Download', live: 'LIVE', rescan: 'Rescan',
    },
    fa: {
      videos: 'ویدیوهای این صفحه', settings: 'تنظیمات',
      intercept: 'قطع دانلودهای مرورگر', sniff: 'تشخیص خودکار ویدیو',
      openApp: 'باز کردن برنامه نووا', scanWait: 'در حال اسکن…', none: 'هنوز ویدیویی پیدا نشده',
      quality: 'کیفیت', download: 'دانلود', live: 'زنده', rescan: 'اسکن مجدد',
    }
  };
  let lang = (navigator.language || 'en').startsWith('fa') ? 'fa' : 'en';

  function applyI18n() {
    document.documentElement.dir = lang === 'fa' ? 'rtl' : 'ltr';
    document.querySelectorAll('[data-i18n]').forEach((el) => {
      const t = i18n[lang][el.dataset.i18n];
      if (t) el.textContent = t;
    });
  }

  // ------------------------------------------------------------------

  chrome.runtime.sendMessage({ type: 'nova-settings-get' }, (settings) => {
    $('#opt-intercept').checked = settings?.interceptDownloads ?? true;
    $('#opt-sniff').checked = settings?.sniffVideos ?? true;
  });

  $('#opt-intercept').addEventListener('change', (e) => {
    chrome.runtime.sendMessage({ type: 'nova-settings-set', patch: { interceptDownloads: e.target.checked } });
  });
  $('#opt-sniff').addEventListener('change', (e) => {
    chrome.runtime.sendMessage({ type: 'nova-settings-set', patch: { sniffVideos: e.target.checked } });
  });

  $('#open-app').addEventListener('click', () => {
    // open the Flutter UI (bundled app); falls back to store page
    chrome.tabs.create({ url: 'novadl://open' });
  });

  // ------------------------------------------------------------------

  function loadVideos() {
    chrome.runtime.sendMessage({ type: 'nova-get-videos' }, ({ videos }) => {
      const list = $('#videos');
      list.innerHTML = '';
      if (!videos || videos.length === 0) {
        const div = document.createElement('div');
        div.className = 'empty';
        div.textContent = i18n[lang].none;
        list.appendChild(div);
        return;
      }
      for (const v of videos.slice(0, 12)) renderVideo(list, v);
    });
  }

  function renderVideo(list, v) {
    const div = document.createElement('div');
    div.className = 'video';

    const title = document.createElement('div');
    title.className = 'title';
    title.textContent = v.title || v.mediaUrl.split('/').pop()?.split('?')[0] || 'video';

    const badges = document.createElement('div');
    badges.className = 'badges';
    badges.innerHTML = `
      <span class="badge">${v.protocol.toUpperCase()}</span>
      ${v.detectMethod ? `<span class="badge">${v.detectMethod}</span>` : ''}
      ${v.isLive ? `<span class="badge" style="background:#dc2626;color:#fff">${i18n[lang].live}</span>` : ''}
    `;

    const trackSel = document.createElement('select');
    (v.tracks || []).forEach((t, i) => {
      const opt = document.createElement('option');
      opt.value = i;
      opt.textContent = t.label || t.height ? `${t.label || t.height + 'p'}` : `Track ${i + 1}`;
      trackSel.appendChild(opt);
    });
    if (!v.tracks || v.tracks.length === 0) {
      const opt = document.createElement('option');
      opt.textContent = 'best';
      trackSel.appendChild(opt);
    }

    const btn = document.createElement('button');
    btn.className = 'dl';
    btn.textContent = i18n[lang].download;
    btn.addEventListener('click', () => {
      const track = v.tracks?.[trackSel.value];
      chrome.runtime.sendMessage({
        type: 'nova-download-video',
        request: {
          pageUrl: v.pageUrl, mediaUrl: v.mediaUrl, title: v.title || 'video',
          protocol: v.protocol, videoTrackId: track?.id ?? null,
          headers: v.headers, isLive: v.isLive, detectMethod: v.detectMethod
        }
      });
      btn.textContent = '✓';
    });

    const row = document.createElement('div');
    row.className = 'row';
    row.appendChild(trackSel);
    row.appendChild(btn);

    div.append(title, badges, row);
    list.appendChild(div);
  }

  applyI18n();
  loadVideos();
  // refresh every second while popup is open (new detections)
  setInterval(loadVideos, 1200);
})();
