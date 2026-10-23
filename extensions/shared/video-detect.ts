/**
 * Video detection — runs in the CONTENT script world.
 * Methods (no single method is relied on alone):
 *   1. <video>/<source> DOM scan (src/currentSrc) with MutationObserver for SPA + dynamic players
 *   2. Performance resource entries (same-context network log)
 *   3. Background-reported webRequest observations (asked on demand)
 *   4. Known manifest sniffing (.m3u8/.mpd)
 * DRM detection: EME usage (encrypted events / MediaKeys) marks the player as protected — we refuse.
 */

export interface DetectedSource {
  id: string;
  url: string;
  kind: 'file' | 'hls' | 'dash';
  height?: number;
  width?: number;
  mime?: string;
  from: 'dom' | 'performance' | 'network';
}

export interface PlayerInfo {
  video: HTMLVideoElement;
  rect: DOMRect;
  sources: DetectedSource[];
  drm: boolean;
}

const MEDIA_EXT = /\.(mp4|webm|mkv|mov|m4v|avi|flv|ts|mp3|m4a|aac|ogg|opus|wav)(\?|$)/i;
const HLS_EXT = /\.m3u8(\?|$)/i;
const DASH_EXT = /\.mpd(\?|$)/i;

export function classifyUrl(url: string): 'file' | 'hls' | 'dash' | null {
  if (!/^https?:\/\//i.test(url)) return null;
  if (HLS_EXT.test(url)) return 'hls';
  if (DASH_EXT.test(url)) return 'dash';
  if (MEDIA_EXT.test(url)) return 'file';
  return null;
}

function norm(u: string): string {
  try {
    return new URL(u, location.href).href;
  } catch {
    return u;
  }
}

function videoIsReal(v: HTMLVideoElement): boolean {
  // ignore tiny/hidden utility videos (ad trackers, analytics pixels)
  const r = v.getBoundingClientRect();
  const w = Math.max(r.width, v.videoWidth, v.clientWidth);
  const h = Math.max(r.height, v.videoHeight, v.clientHeight);
  if (w < 120 || h < 80) return false;
  const style = getComputedStyle(v);
  if (style.visibility === 'hidden' || style.display === 'none') return false;
  return true;
}

function collectFromElement(v: HTMLVideoElement): DetectedSource[] {
  const out: DetectedSource[] = [];
  const seen = new Set<string>();
  const push = (url: string, height?: number, width?: number, mime?: string) => {
    if (!url || url.startsWith('blob:') || url.startsWith('mediasource:')) {
      // blob: MSE streams are handled via network observation (m3u8/mpd segments)
      return;
    }
    const kind = classifyUrl(url);
    if (!kind) return;
    const full = norm(url);
    if (seen.has(full)) return;
    seen.add(full);
    out.push({
      id: 'dom:' + full,
      url: full,
      kind,
      height: height ?? (v.videoHeight || undefined),
      width: width ?? (v.videoWidth || undefined),
      mime,
      from: 'dom',
    });
  };
  push(v.currentSrc || v.src);
  v.querySelectorAll('source').forEach((s) => push(s.src || s.getAttribute('src') || ''));
  return out;
}

function collectFromPerformance(): DetectedSource[] {
  const out: DetectedSource[] = [];
  try {
    const entries = performance.getEntriesByType('resource') as PerformanceResourceTiming[];
    for (const e of entries) {
      const kind = classifyUrl(e.name);
      if (!kind) continue;
      if (kind === 'file' && (e.transferSize ?? 0) < 512 * 1024 && !MEDIA_EXT.test(e.name)) continue;
      out.push({
        id: 'perf:' + e.name,
        url: norm(e.name),
        kind,
        from: 'performance',
      });
    }
  } catch {
    /* performance API unavailable */
  }
  return out;
}

export function detectDrm(v: HTMLVideoElement): boolean {
  // EME markers: MediaKeys attached or encrypted event fired
  try {
    if ((v as unknown as { mediaKeys?: unknown }).mediaKeys) return true;
  } catch {
    /* ignore */
  }
  return v.dataset['novaDrm'] === '1';
}

/** Wire the encrypted event once per video. */
export function watchDrm(v: HTMLVideoElement): void {
  if (v.dataset['novaDrmWatched']) return;
  v.dataset['novaDrmWatched'] = '1';
  v.addEventListener('encrypted', () => {
    v.dataset['novaDrm'] = '1';
  });
}

/** All visible players with their detected sources. */
export function scanPlayers(networkSources: DetectedSource[] = []): PlayerInfo[] {
  const videos = Array.from(document.querySelectorAll('video'));
  const players: PlayerInfo[] = [];
  for (const v of videos) {
    if (!videoIsReal(v)) continue;
    watchDrm(v);
    const own = collectFromElement(v);
    const drm = detectDrm(v);
    if (own.length === 0 && networkSources.length === 0 && !drm) continue;
    players.push({
      video: v,
      rect: v.getBoundingClientRect(),
      sources: dedupe([...own, ...networkSources]),
      drm,
    });
  }
  return players;
}

export function dedupe(list: DetectedSource[]): DetectedSource[] {
  const byUrl = new Map<string, DetectedSource>();
  for (const s of list) {
    const existing = byUrl.get(s.url);
    if (!existing) byUrl.set(s.url, s);
  }
  return [...byUrl.values()];
}
