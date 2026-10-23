/**
 * Nova content script entry (bundled).
 * - Finds players, shows the overlay button, keeps it positioned.
 * - On panel open: merges local sources with network observations, asks the
 *   ENGINE to probe/verify candidates (no fake qualities), and hands the
 *   chosen download to the engine via the background service worker.
 */

import { callBackground, MediaCandidate, siteAllowed, loadSettings, ExtSettings } from '../../shared/protocol';
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

void init();
