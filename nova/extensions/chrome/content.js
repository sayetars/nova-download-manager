// Nova Download Manager — content script
// ---------------------------------------------------------------------------
// Video sniffer: watches network-ish media activity in the page:
//  - observes <video>/<audio> elements and their sources (DOM video)
//  - performance API scan for .m3u8/.mpd/.mp4/.webm requests on load
//  - patches fetch/XHR lightly to catch manifest fetches while browsing
// When a candidate is found, the background worker is informed and a small
// floating play-button overlay is injected (the IDM "download video" bubble).

(() => {
  'use strict';
  if (window.__novaInjected) return;
  window.__novaInjected = true;

  const MIN_SIZE = 100 * 1024; // ignore tiny media (<100KB) by default
  let reported = new Set();

  // ------------------------------------------------------------------
  // Report helpers
  // ------------------------------------------------------------------

  function report(candidate) {
    const key = candidate.mediaUrl + '#' + (candidate.tracks?.[0]?.height ?? '');
    if (reported.has(key)) return;
    reported.add(key);
    chrome.runtime.sendMessage({ type: 'nova-video-detected', candidate });
  }

  function withPageContext(candidate) {
    candidate.pageUrl = location.href;
    candidate.title = document.title || '';
    candidate.headers = candidate.headers || {};
    if (!candidate.headers['Referer']) candidate.headers['Referer'] = location.href;
    return candidate;
  }

  // ------------------------------------------------------------------
  // 1. DOM <video>/<audio> elements
  // ------------------------------------------------------------------

  function scanDomMedia() {
    document.querySelectorAll('video').forEach((v) => {
      const src = v.currentSrc || v.src || v.querySelector('source')?.src;
      if (!src || !/^https?:/.test(src)) return;
      const h = v.videoHeight;
      const w = v.videoWidth;
      report(withPageContext({
        mediaUrl: src,
        protocol: 'direct',
        mimeType: 'video/mp4',
        isLive: false,
        detectMethod: 'dom-video',
        tracks: [{
          id: 'dom', kind: 'video', container: ext(src),
          height: h || null, label: h ? `${h}p` : 'unknown'
        }]
      }));
    });
    document.querySelectorAll('video source').forEach((s) => {
      const src = s.src;
      if (!src) return;
      const label = s.label || s.getAttribute('size');
      const height = s.getAttribute('res') || s.getAttribute('height');
      report(withPageContext({
        mediaUrl: new URL(src, location.href).href,
        protocol: 'direct',
        detectMethod: 'dom-source',
        tracks: [{ id: src, kind: 'video', height: height ? +height : null, label: label || (height ? height + 'p' : '') }]
      }));
    });
  }

  // ------------------------------------------------------------------
  // 2. Performance API: catch m3u8/mpd/mp4/webm loaded by the page
  // ------------------------------------------------------------------

  function scanPerformance() {
    try {
      const entries = performance.getEntriesByType('resource');
      for (const e of entries) {
        const url = e.name;
        if (!/^https?:/.test(url)) continue;
        const low = url.toLowerCase().split('?')[0];
        if (low.endsWith('.m3u8')) {
          report(withPageContext({ mediaUrl: url, protocol: 'hls', detectMethod: 'hls-manifest', tracks: [] }));
        } else if (low.endsWith('.mpd')) {
          report(withPageContext({ mediaUrl: url, protocol: 'dash', detectMethod: 'dash-manifest', tracks: [] }));
        } else if ((low.endsWith('.mp4') || low.endsWith('.webm') || low.endsWith('.m4s')) &&
                   (e.transferSize || 0) > MIN_SIZE) {
          report(withPageContext({
            mediaUrl: url, protocol: 'direct', detectMethod: 'media-sniff',
            contentLength: e.transferSize,
            tracks: [{ id: url, kind: 'video', container: ext(url), sizeBytes: e.transferSize }]
          }));
        }
      }
    } catch (e) { /* page restricted */ }
  }

  // ------------------------------------------------------------------
  // 3. fetch/XHR hook for manifest requests after load
  // ------------------------------------------------------------------

  const origFetch = window.fetch;
  window.fetch = function (...args) {
    const url = typeof args[0] === 'string' ? args[0] : args[0]?.url;
    const low = (url || '').toLowerCase().split('?')[0];
    if (low.endsWith('.m3u8')) report(withPageContext({ mediaUrl: url, protocol: 'hls', detectMethod: 'hls-manifest', tracks: [] }));
    else if (low.endsWith('.mpd')) report(withPageContext({ mediaUrl: url, protocol: 'dash', detectMethod: 'dash-manifest', tracks: [] }));
    return origFetch.apply(this, args);
  };

  const origOpen = XMLHttpRequest.prototype.open;
  XMLHttpRequest.prototype.open = function (method, url, ...rest) {
    const low = (url || '').toLowerCase().split('?')[0];
    if (low.endsWith('.m3u8')) report(withPageContext({ mediaUrl: new URL(url, location.href).href, protocol: 'hls', detectMethod: 'hls-manifest', tracks: [] }));
    else if (low.endsWith('.mpd')) report(withPageContext({ mediaUrl: new URL(url, location.href).href, protocol: 'dash', detectMethod: 'dash-manifest', tracks: [] }));
    return origOpen.call(this, method, url, ...rest);
  };

  // ------------------------------------------------------------------
  // Floating download bubble (appears on video hover, top-right)
  // ------------------------------------------------------------------

  let bubble = null;
  let bubbleTimer = null;

  function ensureBubble() {
    if (bubble) return;
    bubble = document.createElement('div');
    bubble.id = 'nova-video-bubble';
    bubble.style.cssText = `
      position: fixed; z-index: 2147483647; top: 16px; right: 16px;
      background: #1e293b; color: #fff; border-radius: 12px;
      padding: 10px 14px; font: 13px/1.4 system-ui, sans-serif;
      box-shadow: 0 8px 24px rgba(0,0,0,.35); cursor: pointer;
      display: none; align-items: center; gap: 8px; direction: ltr;
    `;
    bubble.innerHTML = `
      <span style="display:inline-block;width:22px;height:22px;background:#2563eb;border-radius:6px;text-align:center;line-height:22px;">⬇</span>
      <span data-role="label">Nova found a video</span>
      <span data-role="open" style="background:#2563eb;border-radius:6px;padding:2px 8px;">Open panel</span>
    `;
    (document.body || document.documentElement).appendChild(bubble);

    bubble.addEventListener('click', (e) => {
      if (e.target.dataset.role === 'open') {
        chrome.runtime.sendMessage({ type: 'nova-open-panel' });
        return;
      }
      const candidate = bubble.__candidate;
      if (candidate) {
        chrome.runtime.sendMessage({
          type: 'nova-download-video',
          request: {
            pageUrl: candidate.pageUrl, mediaUrl: candidate.mediaUrl,
            title: candidate.title || 'video', protocol: candidate.protocol,
            headers: candidate.headers, detectMethod: candidate.detectMethod, isLive: !!candidate.isLive
          }
        });
        bubble.style.display = 'none';
      }
    });

    document.querySelectorAll('video').forEach((v) => attachVideoHover(v));
  }

  function showBubble(candidate) {
    ensureBubble();
    bubble.__candidate = candidate;
    const label = candidate.tracks?.[0]?.label ? `Download ${candidate.tracks[0].label}` : 'Download this video';
    bubble.querySelector('[data-role="label"]').textContent = label;
    bubble.style.display = 'flex';
    clearTimeout(bubbleTimer);
    bubbleTimer = setTimeout(() => { bubble.style.display = 'none'; }, 8000);
  }

  function attachVideoHover(v) {
    if (v.__novaHover) return;
    v.__novaHover = true;
    v.addEventListener('mouseenter', () => {
      const src = v.currentSrc || v.src;
      if (src) showBubble(withPageContext({
        mediaUrl: src, protocol: 'direct', detectMethod: 'dom-video',
        tracks: [{ id: 'dom', kind: 'video', height: v.videoHeight, label: v.videoHeight ? `${v.videoHeight}p` : '' }]
      }));
    });
  }

  // respond to background requests
  chrome.runtime.onMessage.addListener((msg, _s, sendResponse) => {
    if (msg.type === 'nova-show-video-button' && msg.candidate) {
      showBubble({ ...msg.candidate, pageUrl: msg.candidate.pageUrl || location.href });
      sendResponse({ ok: true });
    } else if (msg.type === 'nova-scan-videos') {
      scanDomMedia(); scanPerformance();
      sendResponse({ ok: true });
    }
    return false;
  });

  function ext(url) {
    const m = url.split('?')[0].match(/\.(\w{2,4})$/);
    return m ? m[1] : 'mp4';
  }

  // ------------------------------------------------------------------
  // Run
  // ------------------------------------------------------------------

  if (document.readyState === 'complete') { scanDomMedia(); scanPerformance(); }
  else window.addEventListener('load', () => { scanDomMedia(); scanPerformance(); });

  // periodic rescan (SPA sites mutate DOM constantly)
  setInterval(() => {
    if (document.hidden) return;
    scanDomMedia();
    document.querySelectorAll('video').forEach((v) => attachVideoHover(v));
  }, 4000);
})();
