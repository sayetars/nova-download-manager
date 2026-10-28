/**
 * Nova background entry (Chromium MV3 service worker / Firefox background).
 * - Observes media-looking network traffic per tab (webRequest, observational only).
 * - Bridges content-script requests to the engine through the Native Messaging host.
 * - Context menus for links/videos; optional download interception.
 * - Engine connection status via action badge; auto-reconnect.
 */

import {
  ExtSettings,
  loadSettings,
  BgRequest,
  BgResponse,
  SeenMedia,
} from '../../shared/protocol';

// ---- native messaging --------------------------------------------------------

interface HostMessageIn {
  ok: boolean;
  data?: unknown;
  error?: { code: string; message: string };
}

let port: browser.runtime.Port | null = null;
let engineUp = false;
let reconnectTimer: ReturnType<typeof setTimeout> | null = null;

function connectHost(): void {
  if (port) return;
  try {
    port = browser.runtime.connectNative('com.nova.downloadmanager');
    port.onMessage.addListener((m: HostMessageIn) => {
      const pending = inflight.shift();
      if (pending) {
        if (m.ok) pending.res({ ok: true, data: m.data } as BgResponse);
        else pending.res({ ok: false, error: m.error ?? { code: 'error', message: 'Engine error' } } as BgResponse);
      } else {
        lastHelloResult = m;
      }
    });
    port.onDisconnect.addListener(() => {
      port = null;
      engineUp = false;
      updateBadge();
      scheduleReconnect();
    });
    // hello handshake
    postToHost({ type: 'hello' }).then((r) => {
      engineUp = r.ok;
      updateBadge();
    });
  } catch {
    engineUp = false;
    updateBadge();
    scheduleReconnect();
  }
}

function scheduleReconnect(): void {
  if (reconnectTimer) return;
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    connectHost();
  }, 4000);
}

const inflight: { res: (v: BgResponse) => void }[] = [];
let lastHelloResult: HostMessageIn | null = null;

/**
 * v1.3.0 — opt-in cookie pass-through: when the user enables it in Nova options,
 * the site's cookies for the download URL ride along with the add_download request
 * so login-gated files (premium hosts, SSO portals) can be fetched by the engine.
 * Values are never logged and only ever sent to the local engine over the pipe.
 */
async function cookieHeaderFor(url: string): Promise<string | undefined> {
  try {
    const s = await loadSettings();
    if (!s.passCookies || !/^https?:/i.test(url)) return undefined;
    const jars = await browser.cookies.getAll({ url });
    if (!jars || jars.length === 0) return undefined;
    return jars.map((c) => `${c.name}=${c.value}`).join('; ');
  } catch {
    return undefined;
  }
}

function postToHost(msg: unknown): Promise<BgResponse> {
  return new Promise((resolve) => {
    if (!port) {
      resolve({ ok: false, error: { code: 'engine_down', message: 'Nova engine is not running' } });
      return;
    }
    inflight.push({ res: resolve });
    try {
      port.postMessage(msg as object);
    } catch {
      inflight.pop();
      resolve({ ok: false, error: { code: 'send_failed', message: 'Native host disconnected' } });
      port = null;
      scheduleReconnect();
    }
    // safety timeout
    setTimeout(() => {
      const idx = inflight.findIndex((x) => x.res === resolve);
      if (idx >= 0) {
        inflight.splice(idx, 1);
        resolve({ ok: false, error: { code: 'timeout', message: 'Engine did not respond' } });
      }
    }, 8000);
  });
}

// ---- tab media observation ----------------------------------------------------

const MAX_PER_TAB = 24;
const tabMedia = new Map<number, SeenMedia[]>();

function classify(req: { url: string; type?: string }, headers?: Map<string, string>): 'file' | 'hls' | 'dash' | null {
  const u = req.url;
  if (!/^https?:\/\//i.test(u)) return null;
  if (/\.m3u8(\?|$)/i.test(u)) return 'hls';
  if (/\.mpd(\?|$)/i.test(u)) return 'dash';
  const mime = headers?.get('content-type') ?? '';
  if (mime.includes('mpegurl')) return 'hls';
  if (mime.includes('dash+xml')) return 'dash';
  if (req.type === 'media' || mime.startsWith('video/') || /\.(mp4|webm|ts|m4v|mov)(\?|$)/i.test(u)) {
    return 'file';
  }
  return null;
}

function remember(tabId: number, url: string, kind: 'file' | 'hls' | 'dash', mime?: string, size?: number): void {
  let list = tabMedia.get(tabId);
  if (!list) {
    list = [];
    tabMedia.set(tabId, list);
  }
  // replace same URL with newer info
  const existing = list.find((x) => x.url === url);
  if (existing) {
    existing.ts = Date.now();
    if (mime) existing.mime = mime;
    if (size) existing.size = size;
    return;
  }
  list.unshift({ url, kind, mime, size, ts: Date.now() });
  if (list.length > MAX_PER_TAB) list.length = MAX_PER_TAB;
}

browser.webRequest.onBeforeRequest.addListener(
  (details: { tabId: number; url: string; type?: string }) => {
    const kind = classify(details);
    if (kind && details.tabId >= 0) remember(details.tabId, details.url, kind);
  },
  { urls: ['http://*/*', 'https://*/*'] },
);

browser.webRequest.onResponseStarted.addListener(
  (details: { tabId: number; url: string; type?: string; responseHeaders?: { name: string; value: string }[] }) => {
    const headers = new Map(
      (details.responseHeaders ?? []).map((h) => [h.name.toLowerCase(), h.value]),
    );
    const len = headers.get('content-length');
    const kind =
      classify(details, headers) ??
      (headers.get('content-type')?.startsWith('video/') ? 'file' : null);
    if (kind && details.tabId >= 0) {
      remember(details.tabId, details.url, kind, headers.get('content-type'),
        len ? Number(len) : undefined);
    }
  },
  { urls: ['http://*/*', 'https://*/*'] },
  ['responseHeaders'],
);

browser.tabs.onRemoved.addListener((tabId: number) => tabMedia.delete(tabId));

// ---- message router (content ⇄ background) -----------------------------------

browser.runtime.onMessage.addListener(
  (msg: BgRequest, sender: RuntimeSender, sendResponse: (r: BgResponse) => void) => {
    handle(msg, sender).then(sendResponse);
    return true; // async response
  },
);

interface RuntimeSender {
  tab?: { id?: number };
}

async function handle(msg: BgRequest, sender?: RuntimeSender): Promise<BgResponse> {
  const s = await loadSettings();
  switch (msg.type) {
    case 'hello':
      return { ok: engineUp, data: { engine: engineUp } };
    case 'engine-status': {
      // v1.7.0 version integrity: raw engine_status for the options About section.
      const r = await postToHost({ type: 'engine_status' });
      return r;
    }
    case 'seen-media': {
      const tabId = sender?.tab?.id;
      const items = tabId != null ? (tabMedia.get(tabId) ?? []).filter(
        (m) => Date.now() - m.ts < 30 * 60 * 1000,
      ) : [];
      return { ok: true, data: { items } };
    }
    case 'media-probe': {
      if (!s.enabled) return { ok: false, error: { code: 'disabled', message: 'Nova is disabled' } };
      const r = await postToHost({ type: 'media_probe', payload: { url: msg.url, referer: msg.referer } });
      return r;
    }
    case 'add-download': {
      if (!s.enabled) return { ok: false, error: { code: 'disabled', message: 'Nova is disabled' } };
      const cookies = await cookieHeaderFor(msg.url);
      const r = await postToHost({
        type: 'add_download',
        payload: {
          url: msg.url,
          kind: msg.kind,
          file_name: msg.fileName,
          media_selection: msg.mediaSelection,
          referer: msg.referer,
          ...(cookies ? { cookies } : {}),
          auto_start: true,
        },
      });
      return r;
    }
    case 'add-downloads': {
      // IDM parity: bulk add from the page-links dialog.
      if (!s.enabled) return { ok: false, error: { code: 'disabled', message: 'Nova is disabled' } };
      let added = 0;
      for (const url of msg.urls.slice(0, 500)) {
        const r = await postToHost({
          type: 'add_download',
          payload: { url, referer: msg.referer, auto_start: true },
        });
        if (r.ok) added++;
      }
      return { ok: true, data: { added } };
    }
    default:
      return { ok: false, error: { code: 'unknown', message: 'Unknown message' } };
  }
}

// ---- context menus ------------------------------------------------------------

const MENU_LINK = 'nova-link';
const MENU_VIDEO = 'nova-video';
const MENU_PAGE = 'nova-page';
const MENU_ALL_LINKS = 'nova-all-links';

browser.runtime.onInstalled.addListener(() => {
  browser.contextMenus.removeAll(() => {
    browser.contextMenus.create({ id: MENU_LINK, title: 'Download with Nova', contexts: ['link'] });
    browser.contextMenus.create({ id: MENU_VIDEO, title: 'Download video with Nova', contexts: ['video'] });
    browser.contextMenus.create({
      id: MENU_PAGE,
      title: 'Download page videos with Nova',
      contexts: ['page', 'video'],
    });
    // IDM parity: "Download ALL links with Nova" opens the selection dialog.
    browser.contextMenus.create({
      id: MENU_ALL_LINKS,
      title: 'Download ALL links with Nova…',
      contexts: ['page'],
    });
  });
});

browser.contextMenus.onClicked.addListener(async (info, tab) => {
  const s = await loadSettings();
  if (!s.enabled) return;
  const referer = tab?.url;
  if (info.menuItemId === MENU_LINK && info.linkUrl) {
    await postToHost({
      type: 'add_download',
      payload: { url: info.linkUrl, referer, auto_start: true },
    });
  } else if (info.menuItemId === MENU_ALL_LINKS && tab?.id != null) {
    await browser.tabs.sendMessage(tab.id, { type: 'nova-open-links-dialog' }).catch(() => {});
    return;
  } else if (info.menuItemId === MENU_PAGE && tab?.id != null) {
    const items = tabMedia.get(tab.id) ?? [];
    for (const m of items.slice(0, 8)) {
      await postToHost({
        type: 'add_download',
        payload: { url: m.url, kind: m.kind, referer, auto_start: true },
      });
    }
  } else if (info.menuItemId === MENU_VIDEO && info.srcUrl) {
    await postToHost({
      type: 'add_download',
      payload: { url: info.srcUrl, referer, auto_start: true },
    });
  }
});

// ---- optional browser-download interception -----------------------------------

browser.downloads?.onCreated?.addListener(async (item: { id: number; url: string; state?: string; paused?: boolean }) => {
  const s = await loadSettings();
  if (!s.interceptDownloads || !/^https?:\/\//i.test(item.url)) return;
  try {
    await browser.downloads.pause(item.id);
    const r = await postToHost({
      type: 'add_download',
      payload: { url: item.url, auto_start: true },
    });
    if (r.ok) await browser.downloads.cancel(item.id);
    else await browser.downloads.resume(item.id);
  } catch {
    /* downloads API unavailable */
  }
});

// ---- badge ----------------------------------------------------------------------

function updateBadge(): void {
  try {
    browser.action?.setBadgeText({ text: engineUp ? '' : '!' });
    browser.action?.setBadgeBackgroundColor({ color: '#E5484D' });
    browser.action?.setTitle({
      title: engineUp
        ? 'Nova — connected to engine'
        : 'Nova — engine offline, click to retry',
    });
  } catch {
    /* action API differences */
  }
}

browser.action?.onClicked?.addListener(() => connectHost());

// settings changes (popup or storage sync)
browser.storage.onChanged.addListener((changes: Record<string, { newValue?: unknown }>, area: string) => {
  if (area === 'sync' && changes['novaSettings']) {
    // settings reload lazily; nothing else to do
  }
});

connectHost();
updateBadge();

// keep service worker heartbeats light: periodic cleanup of stale media maps
setInterval(() => {
  const cutoff = Date.now() - 30 * 60 * 1000;
  for (const [tab, list] of tabMedia) {
    const filtered = list.filter((m) => m.ts > cutoff);
    if (filtered.length === 0) tabMedia.delete(tab);
    else tabMedia.set(tab, filtered);
  }
}, 60_000);
