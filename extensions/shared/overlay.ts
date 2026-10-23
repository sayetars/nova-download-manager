/**
 * The Nova on-player overlay: a download button pinned to the player's corner +
 * a compact quality panel. Shadow-DOM isolated, light/dark adaptive, RTL-aware,
 * repositions on resize/scroll/fullscreen, never overlaps native controls,
 * no duplicate buttons, no cost on video-free pages.
 */

import { DetectedSource } from './video-detect';
import { MediaCandidate, fmtBytes } from './protocol';

export interface OverlayCallbacks {
  onDownload: (cand: MediaCandidate) => void;
  onCopyLink: (url: string) => void;
  onRescan: () => void;
}

export class NovaOverlay {
  private host: HTMLElement | null = null;
  private root: ShadowRoot | null = null;
  private button: HTMLElement | null = null;
  private panel: HTMLElement | null = null;
  private video: HTMLVideoElement | null = null;
  private candidates: MediaCandidate[] = [];
  private drm = false;
  private raf = 0;
  private cb: OverlayCallbacks;

  constructor(private readonly container: HTMLElement) {
    this.cb = { onDownload: () => {}, onCopyLink: () => {}, onRescan: () => {} };
  }

  static create(video: HTMLVideoElement, cbs: OverlayCallbacks): NovaOverlay {
    const o = new NovaOverlay(document.documentElement);
    o.cb = cbs;
    o.video = video;
    o.mount();
    return o;
  }

  private mount(): void {
    this.host = document.createElement('div');
    this.host.dataset['novaHost'] = '1';
    this.host.style.cssText = 'all:initial; position:fixed; z-index:2147483646; pointer-events:none;';
    this.root = this.host.attachShadow({ mode: 'closed' });
    this.root.innerHTML = STYLES + BUTTON_HTML + PANEL_HTML;
    document.documentElement.appendChild(this.host);

    this.button = this.root.querySelector('.nova-btn');
    this.panel = this.root.querySelector('.nova-panel');

    this.button!.addEventListener('click', (e) => {
      e.stopPropagation();
      this.togglePanel();
    });
    this.root.querySelector('.nova-close')?.addEventListener('click', (e) => {
      e.stopPropagation();
      this.panel!.classList.remove('open');
    });
    this.root.querySelector('.nova-rescan')?.addEventListener('click', (e) => {
      e.stopPropagation();
      this.cb.onRescan();
    });

    this.startTracking();
  }

  // ---- state ---------------------------------------------------------------

  update(candidates: MediaCandidate[], drm: boolean): void {
    this.candidates = candidates;
    this.drm = drm;
    this.renderPanel();
    const hasSomething = candidates.length > 0;
    this.button!.classList.toggle('disabled', !hasSomething || drm);
    this.button!.setAttribute(
      'title',
      drm ? 'Protected (DRM) content' : hasSomething ? 'Download with Nova' : 'No downloadable media yet',
    );
  }

  show(): void {
    this.host!.style.display = '';
  }

  hide(): void {
    this.host!.style.display = 'none';
    this.panel!.classList.remove('open');
  }

  destroy(): void {
    cancelAnimationFrame(this.raf);
    this.host?.remove();
    this.host = null;
  }

  // ---- positioning ---------------------------------------------------------

  private startTracking(): void {
    const tick = () => {
      if (!this.video || !this.host) return;
      const rect = this.video.getBoundingClientRect();
      if (rect.width < 120 || rect.height < 80 || !this.video.isConnected) {
        this.hide();
      } else {
        this.show();
        this.position(rect);
      }
      this.raf = requestAnimationFrame(tick);
    };
    this.raf = requestAnimationFrame(tick);
  }

  private position(rect: DOMRect): void {
    const margin = 12;
    const insetInlineEnd = Math.max(margin, Math.min(window.innerWidth - rect.right, margin)) + 'px';
    const top = Math.max(margin, rect.top + margin) + 'px';
    this.host!.style.setProperty('inset-inline-end', `calc(100vw - ${rect.right - margin}px)`);
    this.host!.style.setProperty('top', top);
    this.host!.style.setProperty('--nova-inset-end', insetInlineEnd);
    this.host!.style.left = '';
    this.host!.style.right = '';
    // fullscreen: attach INSIDE the fullscreen element so it stays visible
    const fsEl = document.fullscreenElement;
    if (fsEl && fsEl !== document.documentElement && this.host.parentElement !== fsEl) {
      fsEl.appendChild(this.host);
    } else if (!fsEl && this.host.parentElement !== document.documentElement) {
      document.documentElement.appendChild(this.host);
    }
  }

  // ---- panel ----------------------------------------------------------------

  private togglePanel(): void {
    if (this.panel!.classList.contains('open')) {
      this.panel!.classList.remove('open');
    } else if (this.candidates.length > 0 && !this.drm) {
      this.renderPanel();
      this.panel!.classList.add('open');
    }
  }

  private renderPanel(): void {
    const list = this.root!.querySelector('.nova-list')!;
    list.innerHTML = '';
    if (this.drm) return;
    for (const c of this.candidates.slice(0, 8)) {
      const row = document.createElement('button');
      row.className = 'nova-item' + (c.downloadable ? '' : ' nova-muted');
      const size = fmtBytes(c.size_bytes);
      row.innerHTML = `
        <span class="nova-main">
          <span class="nova-label"></span>
          <span class="nova-sub"></span>
        </span>
        <span class="nova-go"></span>`;
      (row.querySelector('.nova-label') as HTMLElement).textContent =
        c.label || c.id.slice(0, 40);
      const sub: string[] = [];
      if (c.height) sub.push(`${c.height}p`);
      if (c.container) sub.push(c.container.toUpperCase());
      if (c.bitrate) sub.push(c.bitrate);
      if (size) sub.push((c.size_is_estimate ? '~' : '') + size);
      (row.querySelector('.nova-sub') as HTMLElement).textContent = sub.join(' · ');
      (row.querySelector('.nova-go') as HTMLElement).textContent = c.downloadable ? '↓' : '✕';
      if (!c.downloadable) {
        row.setAttribute('title', c.note ?? 'Not downloadable');
        row.addEventListener('click', () => this.cb.onCopyLink(c.url));
      } else {
        row.addEventListener('click', (e) => {
          e.stopPropagation();
          this.cb.onDownload(c);
          this.panel!.classList.remove('open');
        });
      }
      list.appendChild(row);
    }
    const copyRow = this.root!.querySelector('.nova-copy');
    copyRow?.classList.toggle('nova-hidden', this.candidates.every((c) => !c.downloadable));
  }

  setCopyHandler(url: string | null): void {
    const copyRow = this.root!.querySelector('.nova-copy');
    if (!copyRow) return;
    copyRow.classList.toggle('nova-hidden', !url);
    (copyRow as HTMLElement).onclick = (e) => {
      e.stopPropagation();
      if (url) this.cb.onCopyLink(url);
      this.panel!.classList.remove('open');
    };
  }
}

// ---- styles + markup ---------------------------------------------------------

const STYLES = `
<style>
  :host { all: initial; }
  * { box-sizing: border-box; margin: 0; padding: 0; font-family: 'Segoe UI', Tahoma, Vazirmatn, sans-serif; }
  .nova-btn {
    pointer-events: auto;
    display: flex; align-items: center; gap: 7px;
    padding: 8px 13px;
    border: 0; border-radius: 10px; cursor: pointer;
    background: linear-gradient(135deg, #4F5BEE, #7B4DF0);
    color: #fff; font-size: 13px; font-weight: 700;
    box-shadow: 0 4px 18px rgba(48, 60, 220, .45);
    opacity: .92; transition: opacity .15s, transform .15s;
  }
  .nova-btn:hover { opacity: 1; transform: translateY(-1px); }
  .nova-btn.disabled { filter: grayscale(.9); opacity: .55; }
  .nova-btn .nova-icon { width: 16px; height: 16px; display: inline-block;
    background: #fff; -webkit-mask: var(--nova-arrow) center/contain no-repeat; }
  .nova-panel {
    pointer-events: auto;
    position: absolute; top: 46px; inset-inline-end: 0;
    min-width: 264px; max-width: 340px;
    background: #17181f; color: #e8e9f0;
    border: 1px solid #2c2e3a; border-radius: 12px;
    box-shadow: 0 18px 46px rgba(0,0,0,.5);
    display: none; overflow: hidden;
  }
  .nova-panel.open { display: block; }
  .nova-head { display: flex; align-items: center; gap: 8px; padding: 10px 12px; border-bottom: 1px solid #262833; }
  .nova-head b { font-size: 12.5px; flex: 1; }
  .nova-head button { background: none; border: 0; color: #9aa0b5; cursor: pointer; font-size: 13px; padding: 2px 6px; border-radius: 6px; }
  .nova-head button:hover { background: #23242e; color: #fff; }
  .nova-list { max-height: 300px; overflow: auto; }
  .nova-item {
    display: flex; width: 100%; align-items: center; gap: 10px;
    padding: 9px 12px; border: 0; background: none; color: inherit;
    cursor: pointer; text-align: start; direction: ltr;
  }
  .nova-item:hover { background: #20212930; }
  .nova-item:not(:last-child) { border-bottom: 1px solid #22232c; }
  .nova-item .nova-main { flex: 1; display: flex; flex-direction: column; gap: 2px; min-width: 0; }
  .nova-item .nova-label { font-size: 12.5px; font-weight: 700; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .nova-item .nova-sub { font-size: 11px; color: #9aa0b5; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .nova-item .nova-go { font-size: 15px; color: #7f89ff; }
  .nova-item.nova-muted { opacity: .55; }
  .nova-copy { display: flex; width: 100%; padding: 9px 12px; border: 0; border-top: 1px solid #262833;
    background: #1d1e27; color: #b9bdd0; font-size: 12px; cursor: pointer; }
  .nova-copy:hover { color: #fff; }
  .nova-hidden { display: none !important; }
</style>`;

const BUTTON_HTML = `
<button class="nova-btn" style="--nova-arrow:url(&quot;data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'%3E%3Cpath fill='black' d='M12 3v10m0 0 4-4m-4 4L8 9m-5 8v2a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-2' stroke='black' stroke-width='2.4' fill='none' stroke-linecap='round' stroke-linejoin='round'/%3E%3C/svg%3E&quot;)">
  <span class="nova-icon"></span><span>Nova</span>
</button>`;

const PANEL_HTML = `
<div class="nova-panel">
  <div class="nova-head">
    <b>Download quality</b>
    <button class="nova-rescan" title="Rescan sources">⟳</button>
    <button class="nova-close" title="Close">✕</button>
  </div>
  <div class="nova-list"></div>
  <button class="nova-copy">Copy direct link</button>
</div>`;
