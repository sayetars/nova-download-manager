/**
 * Nova extension shared code — compiled by esbuild for Chromium and Firefox.
 * Wire protocol mirrors src/Nova.Ipc/Protocol.cs: 4-byte LE length + JSON.
 */

export const NATIVE_HOST = 'com.nova.downloadmanager';

/**
 * v1.7.0 version-integrity: stamped from version.json by scripts/sync_versions.py —
 * the minimum engine the extension needs. The options page compares it against
 * engine_status.engine_version and shows a red banner when the engine is older.
 */
export const MIN_ENGINE_VERSION = '1.8.0';

/** SemVer compare (major.minor.patch only); returns -1 | 0 | 1; 0 when unparseable. */
export function cmpVersions(a: string, b: string): number {
  const pa = String(a || '').split(/[+\-]/)[0].split('.').map((n) => parseInt(n, 10) || 0);
  const pb = String(b || '').split(/[+\-]/)[0].split('.').map((n) => parseInt(n, 10) || 0);
  for (let i = 0; i < 3; i++) {
    if ((pa[i] || 0) < (pb[i] || 0)) return -1;
    if ((pa[i] || 0) > (pb[i] || 0)) return 1;
  }
  return 0;
}

export interface MediaCandidate {
  id: string;
  url: string;
  kind: 'file' | 'hls' | 'dash';
  container?: string;
  height?: number;
  size_bytes?: number | null;
  size_is_estimate?: boolean;
  bitrate?: string;
  codecs?: string;
  label: string;
  downloadable: boolean;
  note?: string;
  has_audio?: boolean;
  has_video?: boolean;
}

export interface ExtSettings {
  enabled: boolean;
  showOnHover: boolean;
  interceptDownloads: boolean;
  preferredQuality: string; // 'ask' | 'best' | height like '720'
  allowlist: string[]; // hosts; empty = all (except blocklist)
  blocklist: string[];
  /** IDM parity v1.2.0 — special keys: hold this key while clicking a link to force download with Nova.
   * v1.8.0: 'insert' added — IDM's own default — tracked via keydown/keyup state
   * (non-modifier keys have no flag on click events). */
  interceptKey: 'alt' | 'ctrl' | 'shift' | 'insert' | 'none';
  /** IDM parity v1.2.0 — while this key is held, Nova overlay + interception are suppressed. */
  preventKey: 'none' | 'alt' | 'ctrl' | 'shift' | 'insert';
  /** v1.3.0 — opt-in: send the site's cookies with the download so login-gated files work. Privacy-first: OFF by default. */
  passCookies: boolean;
}

export const DEFAULT_SETTINGS: ExtSettings = {
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

export async function loadSettings(): Promise<ExtSettings> {
  try {
    const raw = await chrome.storage.sync.get('novaSettings');
    return { ...DEFAULT_SETTINGS, ...(raw?.novaSettings ?? {}) };
  } catch {
    return { ...DEFAULT_SETTINGS };
  }
}

export async function saveSettings(s: ExtSettings): Promise<void> {
  await chrome.storage.sync.set({ novaSettings: s });
}

export function hostMatches(host: string, patterns: string[]): boolean {
  return patterns.some((p) => {
    const t = p.trim().toLowerCase();
    if (!t) return false;
    if (t.startsWith('*.')) return host === t.slice(2) || host.endsWith(t.slice(1));
    return host === t || host.endsWith('.' + t);
  });
}

export function siteAllowed(url: string, s: ExtSettings): boolean {
  try {
    const host = new URL(url).hostname.toLowerCase();
    if (hostMatches(host, s.blocklist)) return false;
    if (s.allowlist.length === 0) return true;
    return hostMatches(host, s.allowlist);
  } catch {
    return false;
  }
}

/** IDM parity v1.2.0 — does this keyboard event hold the configured special key?
 * v1.8.0: 'insert' compares against e.key (works on keydown/keyup state events). */
export function keyMatches(e: KeyboardEvent, key: 'alt' | 'ctrl' | 'shift' | 'insert' | 'none'): boolean {
  switch (key) {
    case 'alt':
      return e.altKey || e.key === 'Alt';
    case 'ctrl':
      return e.ctrlKey || e.key === 'Control';
    case 'shift':
      return e.shiftKey || e.key === 'Shift';
    case 'insert':
      return e.key === 'Insert' || e.code === 'Insert';
    default:
      return false;
  }
}

/** v1.8.0 — is the configured special key held at CLICK time? Modifiers read their
 * event flags; non-modifier keys (insert) come from the tracked keydown/keyup state
 * that the content script maintains. */
export function keyHeldForClick(
  click: MouseEvent,
  key: 'alt' | 'ctrl' | 'shift' | 'insert' | 'none',
  insertHeld: boolean,
): boolean {
  if (key === 'insert') return insertHeld;
  return keyMatches(click as unknown as KeyboardEvent, key);
}

// ---- background ⇄ content message contracts ---------------------------------

/** Media resource seen on the network for a tab (from webRequest observer). */
export interface SeenMedia {
  url: string;
  kind: 'file' | 'hls' | 'dash';
  mime?: string;
  size?: number;
  ts: number;
}

export type BgRequest =
  | { type: 'hello' }
  | { type: 'engine-status' }
  | { type: 'seen-media'; tabId?: number }
  | { type: 'media-probe'; url: string; referer?: string }
  | {
      type: 'add-download';
      url: string;
      kind?: 'file' | 'hls' | 'dash';
      fileName?: string;
      mediaSelection?: unknown;
      referer?: string;
    }
  | { type: 'add-downloads'; urls: string[]; referer?: string }
  | { type: 'collect-links'; filter?: 'documents' | 'media' | 'archives' | 'all' };

export type BgResponse =
  | { ok: true; data: unknown }
  | { ok: false; error: { code: string; message: string } };

export async function callBackground<T>(msg: BgRequest): Promise<T | null> {
  try {
    const resp = (await chrome.runtime.sendMessage(msg)) as BgResponse | null;
    if (resp && resp.ok) return (resp.data as T) ?? null;
    if (resp && !resp.ok) console.warn('[Nova]', resp.error.code, resp.error.message);
    return null;
  } catch {
    return null;
  }
}

// ---- overlay helpers shared by content script -------------------------------

export function fmtBytes(n?: number | null): string | null {
  if (n == null || n <= 0) return null;
  const units = ['B', 'KB', 'MB', 'GB'];
  let v = n;
  let u = 0;
  while (v >= 1024 && u < units.length - 1) {
    v /= 1024;
    u++;
  }
  return `${v >= 100 || u === 0 ? Math.round(v) : v.toFixed(1)} ${units[u]}`;
}
