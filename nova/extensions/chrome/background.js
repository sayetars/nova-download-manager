// Nova Download Manager — Chrome/Brave/Edge MV3 background service worker
// ---------------------------------------------------------------------------
// Responsibilities:
//  1. Catch every browser download (chrome.downloads.onDeterminingFilename)
//     and, when intercept is enabled, cancel it and hand the URL to Nova
//     through the native messaging host.
//  2. Receive detected video candidates from content scripts (m3u8/mpd/media
//     sniffing) and surface the quality panel in the page + badge count.
//  3. Bridge extension <-> native host <-> engine IPC.

const NATIVE_HOST = 'com.nova.downloadmanager';
let port = null;
let reconnectTimer = null;
let videoCache = new Map();   // tabId -> VideoCandidate[]
let settings = {
  interceptDownloads: true,
  sniffVideos: true,
  minMediaSizeKb: 500,
  autoGrabFirst: false,
};

// ---------------------------------------------------------------------------
// Native messaging connection (auto-reconnect)
// ---------------------------------------------------------------------------

function connectEngine() {
  if (reconnectTimer) clearTimeout(reconnectTimer);
  try {
    port = chrome.runtime.connectNative(NATIVE_HOST);
    port.onMessage.addListener(onEngineMessage);
    port.onDisconnect.addListener(() => {
      port = null;
      console.warn('[nova] native host disconnected, retrying in 3s');
      reconnectTimer = setTimeout(connectEngine, 3000);
    });
    sendToEngine({ type: 'hello', v: 1 });
  } catch (e) {
    console.warn('[nova] native host connect failed', e);
    reconnectTimer = setTimeout(connectEngine, 5000);
  }
}

function sendToEngine(msg) {
  if (port) {
    try { port.postMessage(msg); } catch (e) { port = null; connectEngine(); }
  }
}

function onEngineMessage(msg) {
  switch (msg.type) {
    case 'ev.task.progress':
      // update badge with aggregate speed
      chrome.action.setBadgeText({ text: fmtSpeed(msg.payload?.download) });
      chrome.action.setBadgeBackgroundColor({ color: '#2563eb' });
      break;
    case 'ev.task.state':
      if (msg.payload?.state === 'completed') {
        notify(chrome.i18n?.getMessage?.('downloaded') ?? 'Download complete', msg.payload);
      } else if (msg.payload?.state === 'failed') {
        notify('Download failed', msg.payload);
      }
      break;
    case 'ev.video.detected':
      // engine-side detection (yt-dlp bridge) pushing candidates
      break;
    default:
      break;
  }
}

function notify(title, payload) {
  chrome.notifications?.create({
    type: 'basic',
    iconUrl: 'icons/icon128.png',
    title: 'Nova',
    message: title + (payload?.fileName ? `: ${payload.fileName}` : '')
  });
}

function fmtSpeed(bytesPerSec) {
  if (!bytesPerSec || bytesPerSec <= 0) return '';
  if (bytesPerSec < 1024 * 1024) return `${Math.round(bytesPerSec / 1024)}K`;
  return `${(bytesPerSec / 1024 / 1024).toFixed(1)}M`;
}

// ---------------------------------------------------------------------------
// 1. Download interception (IDM's core behavior)
// ---------------------------------------------------------------------------

chrome.downloads.onDeterminingFilename.addListener((item, suggest) => {
  if (!settings.interceptDownloads) { suggest(); return true; }

  const isDetermined = item.finalUrl && item.finalUrl.startsWith('http');
  if (!isDetermined) { suggest(); return true; }

  // never intercept our own completed files or data/blob URLs
  if (item.url.startsWith('blob:') || item.url.startsWith('data:')) { suggest(); return true; }

  // ask Nova to take over
  chrome.downloads.cancel(item.id);
  sendToEngine({
    type: 'task.addUrl',
    payload: {
      url: item.finalUrl || item.url,
      fileName: item.filename || '',
      headers: { 'Referer': item.referrer || '' }
    }
  });
  // do not suggest() — we cancelled
  return true;
});

// ---------------------------------------------------------------------------
// 2. Video detection aggregation from content scripts
// ---------------------------------------------------------------------------

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  switch (msg.type) {
    case 'nova-video-detected': {
      const tabId = sender?.tab?.id ?? -1;
      if (tabId < 0) return;
      addVideoCandidate(tabId, msg.candidate);
      updateBadge(tabId);
      // tell the page's floating button a new video appeared
      chrome.tabs.sendMessage(tabId, { type: 'nova-show-video-button', candidate: msg.candidate }).catch(() => {});
      sendResponse({ ok: true });
      break;
    }
    case 'nova-get-videos': {
      const tabId = sender?.tab?.id ?? -1;
      sendResponse({ videos: videoCache.get(tabId) || [] });
      break;
    }
    case 'nova-download-video': {
      sendToEngine({
        type: 'task.addVideo',
        payload: msg.request
      });
      sendResponse({ ok: true });
      break;
    }
    case 'nova-resolve-tracks': {
      sendToEngine({ type: 'video.resolveTracks', payload: { pageUrl: msg.pageUrl, mediaUrl: msg.mediaUrl, title: msg.title || '', protocol: msg.protocol, headers: msg.headers || {} } });
      sendResponse({ ok: true });
      break;
    }
    case 'nova-settings-get':
      sendResponse(settings);
      break;
    case 'nova-settings-set':
      settings = { ...settings, ...msg.patch };
      chrome.storage.local.set({ settings });
      sendResponse({ ok: true });
      break;
    default:
      break;
  }
  return false; // sync responses only
});

function addVideoCandidate(tabId, candidate) {
  if (!videoCache.has(tabId)) videoCache.set(tabId, []);
  const list = videoCache.get(tabId);
  // dedupe by URL+height
  const dup = list.find(v => v.mediaUrl === candidate.mediaUrl &&
    (v.tracks?.[0]?.height ?? 0) === (candidate.tracks?.[0]?.height ?? 0));
  if (!dup) list.push(candidate);
}

function updateBadge(tabId) {
  const count = (videoCache.get(tabId) || []).length;
  if (count > 0) {
    chrome.action.setBadgeText({ text: String(Math.min(count, 9)) + (count > 9 ? '+' : ''), tabId });
    chrome.action.setBadgeBackgroundColor({ color: '#f59e0b', tabId });
  }
}

// ---------------------------------------------------------------------------
// 3. Context menu: "Download with Nova"
// ---------------------------------------------------------------------------

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({
    id: 'nova-download-link',
    title: 'Download with Nova',
    contexts: ['link']
  });
  chrome.contextMenus.create({
    id: 'nova-download-video',
    title: 'Download this video with Nova',
    contexts: ['video', 'audio']
  });
  chrome.storage.local.get('settings', (r) => {
    if (r.settings) settings = { ...settings, ...r.settings };
  });
});

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId === 'nova-download-link') {
    sendToEngine({ type: 'task.addUrl', payload: { url: info.linkUrl, headers: { Referer: tab?.url || '' } } });
  } else if (info.menuItemId === 'nova-download-video') {
    const pageUrl = tab?.url || '';
    const mediaUrl = info.srcUrl || pageUrl;
    sendToEngine({
      type: 'task.addVideo', payload: {
        pageUrl, mediaUrl, title: tab?.title || 'video', protocol: 'direct',
        headers: { Referer: pageUrl }
      }
    });
  }
});

// keyboard shortcut: scan current tab for videos
chrome.commands?.onCommand.addListener(async (command) => {
  if (command !== 'grab-videos') return;
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab?.id) return;
  chrome.tabs.sendMessage(tab.id, { type: 'nova-scan-videos' }).catch(() => {});
  chrome.action.openPopup?.();
});

// ---------------------------------------------------------------------------
// start
// ---------------------------------------------------------------------------
connectEngine();
