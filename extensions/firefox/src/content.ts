/**
 * Nova content script entry (bundled).
 * - Finds players, shows the overlay button, keeps it positioned.
 * - On panel open: merges local sources with network observations, asks the
 *   ENGINE to probe/verify candidates (no fake qualities), and hands the
 *   chosen download to the engine via the background service worker.
 */

import { callBackground, MediaCandidate, siteAllowed, loadSettings, ExtSettings, keyMatches } from '../../shared/protocol';
import { scanPlayers, watchDrm } from '../../shared/video-detect';
import { NovaOverlay } from '../../shared/overlay';

interface OverlayRecord {
  overlay: NovaOverlay;
  video: HTMLVideoElement;
}

interface DetectedJson {
  url: string;
  kind: 'file' | 'hls' | 'dash';
}

let settings: ExtSettings | null = null;
const overlays = new WeakMap<HTMLVideoElement, OverlayRecord>();
let observer: MutationObserver | null = null;

async function init(): Promise<void> {
  settings = await loadSettings();
  if (!settings.enabled) return;
  if (!siteAllowed(location.href, settings)) return;

  observeDom();
  await refresh();
  setInterval(() => void refresh(), 2500);
}

async function refresh(): Promise<void> {
  if (!settings?.enabled || !siteAllowed(location.href, settings)) return;

  const networkSources = await callBackground<{ items: DetectedJson[] }>({ type: 'seen-media' });
  const seen: DetectedJson[] = networkSources?.items ?? [];

  const players = scanPlayers(
    seen.map((s) => ({ id: 'net:' + s.url, url: s.url, kind: s.kind, from: 'network' as const })),
  );

  for (const p of players) {
    // IDM parity v1.2.0: prevent key held → overlay stays hidden this pass.
    if (preventHeld) {
      overlays.get(p.video)?.overlay.hide();
      continue;
    }
    watchDrm(p.video);
    let rec = overlays.get(p.video);
    if (!rec) {
      rec = {
        video: p.video,
        overlay: NovaOverlay.create(p.video, {
          onDownload: (cand) => void download(cand),
          onCopyLink: (url) => navigator.clipboard?.writeText(url).catch(() => {}),
          onRescan: () => void refresh(),
        }),
      };
      overlays.set(p.video, rec);
    }
    const candidates = await buildCandidates(p.sources, p.drm);
    rec.overlay.update(candidates, p.drm);
  }
}

async function buildCandidates(
  sources: { url: string; kind: 'file' | 'hls' | 'dash'; height?: number }[],
  drm: boolean,
): Promise<MediaCandidate[]> {
  if (drm) {
    return [
      {
        id: 'drm',
        url: '',
        kind: 'file',
        label: 'Protected (DRM) content',
        downloadable: false,
        note: 'This stream is protected and cannot be downloaded.',
      },
    ];
  }
  // Engine-side verification: probe each source URL; only real candidates show.
  const out: MediaCandidate[] = [];
  const byUrl = new Map(sources.map((s) => [s.url, s]));
  for (const s of byUrl.values()) {
    const resp = await callBackground<{ candidates?: MediaCandidate[] }>({
      type: 'media-probe',
      url: s.url,
      referer: location.href,
    });
    if (resp?.candidates) out.push(...resp.candidates);
  }
  const uniq = new Map<string, MediaCandidate>();
  for (const c of out) if (!uniq.has(c.id)) uniq.set(c.id, c);
  return [...uniq.values()].sort((a, b) => Number(b.downloadable) - Number(a.downloadable));
}

async function download(cand: MediaCandidate): Promise<void> {
  await callBackground({
    type: 'add-download',
    url: cand.url,
    kind: cand.kind,
    mediaSelection:
      cand.kind === 'hls'
        ? { type: 'hls', variant_url: cand.url, preferred_height: cand.height ?? null }
        : cand.kind === 'dash'
          ? {
              type: 'dash',
              video_rep_id: cand.id.startsWith('audio:') ? undefined : cand.id,
              audio_rep_id: cand.id.startsWith('audio:') ? cand.id.slice(6) : undefined,
            }
          : undefined,
    referer: location.href,
  });
}

function observeDom(): void {
  if (observer) return;
  observer = new MutationObserver(() => {
    void refresh();
  });
  observer.observe(document.documentElement, { childList: true, subtree: true });
}

// ---- IDM parity: "Download ALL links" selection dialog ------------------------

const DOC_RE = /\.(pdf|docx?|xlsx?|pptx?|odt|txt|epub)(\?|$)/i;
const MEDIA_RE = /\.(mp4|mkv|webm|mov|avi|m4v|mp3|flac|wav|m4a|ogg)(\?|$)/i;
const ARCHIVE_RE = /\.(zip|rar|7z|tar|gz|bz2|xz|iso)(\?|$)/i;

function collectLinks(): { url: string; text: string; group: 'documents' | 'media' | 'archives' | 'other' }[] {
  const out: { url: string; text: string; group: 'documents' | 'media' | 'archives' | 'other' }[] = [];
  const seen = new Set<string>();
  for (const a of document.querySelectorAll<HTMLAnchorElement>('a[href]')) {
    const href = a.href;
    if (!/^https?:/i.test(href) || seen.has(href)) continue;
    seen.add(href);
    const group = DOC_RE.test(href) ? 'documents'
      : MEDIA_RE.test(href) ? 'media'
      : ARCHIVE_RE.test(href) ? 'archives'
      : 'other';
    out.push({ url: href, text: (a.textContent ?? '').trim().slice(0, 80), group });
  }
  return out;
}

function openLinksDialog(): void {
  if (document.getElementById('nova-links-panel')) return;
  const links = collectLinks().slice(0, 500);
  const panel = document.createElement('div');
  panel.id = 'nova-links-panel';
  Object.assign(panel.style, {
    position: 'fixed', zIndex: '2147483647', inset: 'auto 16px 16px auto', top: '16px',
    left: '16px', right: '16px', bottom: '16px',
    background: '#0f172a', color: '#e2e8f0', borderRadius: '14px',
    fontFamily: 'system-ui,Segoe UI,sans-serif', fontSize: '13px',
    display: 'flex', flexDirection: 'column', boxShadow: '0 12px 48px rgba(0,0,0,.5)',
    border: '1px solid #334155', direction: 'ltr',
  } as CSSStyleDeclaration);

  const head = document.createElement('div');
  head.style.cssText = 'display:flex;align-items:center;gap:10px;padding:14px 16px;border-bottom:1px solid #334155';
  head.innerHTML = '<b style="font-size:14px">Nova — links on this page</b>';
  const close = document.createElement('span');
  close.textContent = '✕';
  close.style.cssText = 'cursor:pointer;font-size:16px;margin-left:auto';
  close.onclick = () => panel.remove();
  head.appendChild(close);
  panel.appendChild(head);

  let filter: 'all' | 'documents' | 'media' | 'archives' = 'all';
  const chips = document.createElement('div');
  chips.style.cssText = 'display:flex;gap:8px;padding:10px 16px;flex-wrap:wrap';
  const bodyWrap = document.createElement('div');
  bodyWrap.style.cssText = 'flex:1;overflow:auto;padding:6px 10px';

  const footer = document.createElement('div');
  footer.style.cssText = 'display:flex;align-items:center;gap:12px;padding:12px 16px;border-top:1px solid #334155';
  const count = document.createElement('span');
  count.style.cssText = 'color:#94a3b8;font-size:12px';
  const allNone = document.createElement('button');
  allNone.textContent = 'All / none';
  allNone.style.cssText = 'background:#334155;color:#e2e8f0;border:0;border-radius:8px;padding:7px 12px;cursor:pointer';
  const go = document.createElement('button');
  go.textContent = 'Download selected';
  go.style.cssText = 'background:#2563eb;color:#fff;border:0;border-radius:8px;padding:8px 16px;cursor:pointer;font-weight:600;margin-left:auto';
  footer.append(allNone, count, go);
  panel.append(chips, bodyWrap, footer);
  document.documentElement.appendChild(panel);

  const boxes = new Map<string, HTMLInputElement>();
  function render(): void {
    bodyWrap.innerHTML = '';
    boxes.clear();
    let shown = 0;
    for (const l of links) {
      if (filter !== 'all' && l.group !== filter && !(filter === 'media' && l.group === 'other')) continue;
      shown++;
      const row = document.createElement('label');
      row.style.cssText = 'display:flex;gap:10px;align-items:center;padding:7px 10px;border-radius:8px;cursor:pointer';
      row.onmouseenter = () => (row.style.background = '#1e293b');
      row.onmouseleave = () => (row.style.background = '');
      const cb = document.createElement('input');
      cb.type = 'checkbox';
      cb.checked = true;
      const name = document.createElement('div');
      name.style.cssText = 'flex:1;min-width:0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis';
      name.textContent = l.text || l.url.split('/').pop() || l.url;
      const grp = document.createElement('span');
      grp.style.cssText = 'color:#64748b;font-size:11px';
      grp.textContent = l.group;
      row.append(cb, name, grp);
      bodyWrap.appendChild(row);
      boxes.set(l.url, cb);
    }
    count.textContent = `${shown} shown`;
  }
  for (const f of ['all', 'documents', 'media', 'archives'] as const) {
    const chip = document.createElement('button');
    chip.textContent = f;
    chip.style.cssText = `background:${f === 'all' ? '#2563eb' : '#334155'};color:#fff;border:0;border-radius:999px;padding:6px 14px;cursor:pointer`;
    chip.onclick = () => {
      filter = f;
      for (const c of chips.children) (c as HTMLElement).style.background = '#334155';
      chip.style.background = '#2563eb';
      render();
    };
    chips.appendChild(chip);
  }
  let allOn = true;
  allNone.onclick = () => {
    allOn = !allOn;
    for (const cb of boxes.values()) cb.checked = allOn;
  };
  go.onclick = () => {
    const urls = [...boxes.entries()].filter(([, cb]) => cb.checked).map(([u]) => u);
    void callBackground({ type: 'add-downloads', urls, referer: location.href });
    panel.remove();
  };
  render();
}

browser.runtime.onMessage.addListener((msg: { type?: string }) => {
  if (msg?.type === 'nova-open-links-dialog') openLinksDialog();
});

// ---- IDM parity v1.2.0: special keys ------------------------------------------------
// interceptKey: hold while clicking a link → force download with Nova.
// preventKey: hold → Nova overlay + interception suppressed (checked each refresh).
let preventHeld = false;
document.addEventListener(
  'keydown',
  (e) => {
    if (settings?.preventKey && settings.preventKey !== 'none' && keyMatches(e, settings.preventKey)) preventHeld = true;
  },
  true,
);
document.addEventListener(
  'keyup',
  (e) => {
    if (settings?.preventKey && settings.preventKey !== 'none' && keyMatches(e, settings.preventKey)) preventHeld = false;
  },
  true,
);
window.addEventListener('blur', () => { preventHeld = false; });

document.addEventListener(
  'click',
  (e) => {
    if (e.defaultPrevented || preventHeld || !settings?.enabled) return;
    if (!settings.interceptKey || settings.interceptKey === 'none') return;
    if (!keyMatches(e as unknown as KeyboardEvent, settings.interceptKey)) return;
    const a = (e.target as HTMLElement | null)?.closest?.('a[href]');
    if (!a) return;
    e.preventDefault();
    e.stopPropagation();
    void callBackground({ type: 'add-download', url: (a as HTMLAnchorElement).href, referer: location.href });
  },
  true,
);

void init();

