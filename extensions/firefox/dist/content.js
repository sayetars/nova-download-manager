(()=>{var M=Object.defineProperty;var S=(e,n,o)=>n in e?M(e,n,{enumerable:!0,configurable:!0,writable:!0,value:o}):e[n]=o;var s=(e,n,o)=>S(e,typeof n!="symbol"?n+"":n,o);var b={enabled:!0,showOnHover:!0,interceptDownloads:!1,preferredQuality:"ask",allowlist:[],blocklist:[]};async function w(){try{let e=await chrome.storage.sync.get("novaSettings");return{...b,...e?.novaSettings??{}}}catch{return{...b}}}function y(e,n){return n.some(o=>{let t=o.trim().toLowerCase();return t?t.startsWith("*.")?e===t.slice(2)||e.endsWith(t.slice(1)):e===t||e.endsWith("."+t):!1})}function m(e,n){try{let o=new URL(e).hostname.toLowerCase();return y(o,n.blocklist)?!1:n.allowlist.length===0?!0:y(o,n.allowlist)}catch{return!1}}async function c(e){try{let n=await chrome.runtime.sendMessage(e);return n&&n.ok?n.data??null:(n&&!n.ok&&console.warn("[Nova]",n.error.code,n.error.message),null)}catch{return null}}function x(e){if(e==null||e<=0)return null;let n=["B","KB","MB","GB"],o=e,t=0;for(;o>=1024&&t<n.length-1;)o/=1024,t++;return`${o>=100||t===0?Math.round(o):o.toFixed(1)} ${n[t]}`}var L=/\.(mp4|webm|mkv|mov|m4v|avi|flv|ts|mp3|m4a|aac|ogg|opus|wav)(\?|$)/i,T=/\.m3u8(\?|$)/i,C=/\.mpd(\?|$)/i;function D(e){return/^https?:\/\//i.test(e)?T.test(e)?"hls":C.test(e)?"dash":L.test(e)?"file":null:null}function H(e){try{return new URL(e,location.href).href}catch{return e}}function P(e){let n=e.getBoundingClientRect(),o=Math.max(n.width,e.videoWidth,e.clientWidth),t=Math.max(n.height,e.videoHeight,e.clientHeight);if(o<120||t<80)return!1;let i=getComputedStyle(e);return!(i.visibility==="hidden"||i.display==="none")}function R(e){let n=[],o=new Set,t=(i,a,r,h)=>{if(!i||i.startsWith("blob:")||i.startsWith("mediasource:"))return;let g=D(i);if(!g)return;let d=H(i);o.has(d)||(o.add(d),n.push({id:"dom:"+d,url:d,kind:g,height:a??(e.videoHeight||void 0),width:r??(e.videoWidth||void 0),mime:h,from:"dom"}))};return t(e.currentSrc||e.src),e.querySelectorAll("source").forEach(i=>t(i.src||i.getAttribute("src")||"")),n}function q(e){try{if(e.mediaKeys)return!0}catch{}return e.dataset.novaDrm==="1"}function f(e){e.dataset.novaDrmWatched||(e.dataset.novaDrmWatched="1",e.addEventListener("encrypted",()=>{e.dataset.novaDrm="1"}))}function k(e=[]){let n=Array.from(document.querySelectorAll("video")),o=[];for(let t of n){if(!P(t))continue;f(t);let i=R(t),a=q(t);i.length===0&&e.length===0&&!a||o.push({video:t,rect:t.getBoundingClientRect(),sources:B([...i,...e]),drm:a})}return o}function B(e){let n=new Map;for(let o of e)n.get(o.url)||n.set(o.url,o);return[...n.values()]}var u=class e{constructor(n){this.container=n;s(this,"host",null);s(this,"root",null);s(this,"button",null);s(this,"panel",null);s(this,"video",null);s(this,"candidates",[]);s(this,"drm",!1);s(this,"raf",0);s(this,"cb");this.cb={onDownload:()=>{},onCopyLink:()=>{},onRescan:()=>{}}}static create(n,o){let t=new e(document.documentElement);return t.cb=o,t.video=n,t.mount(),t}mount(){this.host=document.createElement("div"),this.host.dataset.novaHost="1",this.host.style.cssText="all:initial; position:fixed; z-index:2147483646; pointer-events:none;",this.root=this.host.attachShadow({mode:"closed"}),this.root.innerHTML=_+z+A,document.documentElement.appendChild(this.host),this.button=this.root.querySelector(".nova-btn"),this.panel=this.root.querySelector(".nova-panel"),this.button.addEventListener("click",n=>{n.stopPropagation(),this.togglePanel()}),this.root.querySelector(".nova-close")?.addEventListener("click",n=>{n.stopPropagation(),this.panel.classList.remove("open")}),this.root.querySelector(".nova-rescan")?.addEventListener("click",n=>{n.stopPropagation(),this.cb.onRescan()}),this.startTracking()}update(n,o){this.candidates=n,this.drm=o,this.renderPanel();let t=n.length>0;this.button.classList.toggle("disabled",!t||o),this.button.setAttribute("title",o?"Protected (DRM) content":t?"Download with Nova":"No downloadable media yet")}show(){this.host.style.display=""}hide(){this.host.style.display="none",this.panel.classList.remove("open")}destroy(){cancelAnimationFrame(this.raf),this.host?.remove(),this.host=null}startTracking(){let n=()=>{if(!this.video||!this.host)return;let o=this.video.getBoundingClientRect();o.width<120||o.height<80||!this.video.isConnected?this.hide():(this.show(),this.position(o)),this.raf=requestAnimationFrame(n)};this.raf=requestAnimationFrame(n)}position(n){let t=Math.max(12,Math.min(window.innerWidth-n.right,12))+"px",i=Math.max(12,n.top+12)+"px";this.host.style.setProperty("inset-inline-end",`calc(100vw - ${n.right-12}px)`),this.host.style.setProperty("top",i),this.host.style.setProperty("--nova-inset-end",t),this.host.style.left="",this.host.style.right="";let a=document.fullscreenElement;a&&a!==document.documentElement&&this.host.parentElement!==a?a.appendChild(this.host):!a&&this.host.parentElement!==document.documentElement&&document.documentElement.appendChild(this.host)}togglePanel(){this.panel.classList.contains("open")?this.panel.classList.remove("open"):this.candidates.length>0&&!this.drm&&(this.renderPanel(),this.panel.classList.add("open"))}renderPanel(){let n=this.root.querySelector(".nova-list");if(n.innerHTML="",this.drm)return;for(let t of this.candidates.slice(0,8)){let i=document.createElement("button");i.className="nova-item"+(t.downloadable?"":" nova-muted");let a=x(t.size_bytes);i.innerHTML=`
        <span class="nova-main">
          <span class="nova-label"></span>
          <span class="nova-sub"></span>
        </span>
        <span class="nova-go"></span>`,i.querySelector(".nova-label").textContent=t.label||t.id.slice(0,40);let r=[];t.height&&r.push(`${t.height}p`),t.container&&r.push(t.container.toUpperCase()),t.bitrate&&r.push(t.bitrate),a&&r.push((t.size_is_estimate?"~":"")+a),i.querySelector(".nova-sub").textContent=r.join(" \xB7 "),i.querySelector(".nova-go").textContent=t.downloadable?"\u2193":"\u2715",t.downloadable?i.addEventListener("click",h=>{h.stopPropagation(),this.cb.onDownload(t),this.panel.classList.remove("open")}):(i.setAttribute("title",t.note??"Not downloadable"),i.addEventListener("click",()=>this.cb.onCopyLink(t.url))),n.appendChild(i)}this.root.querySelector(".nova-copy")?.classList.toggle("nova-hidden",this.candidates.every(t=>!t.downloadable))}setCopyHandler(n){let o=this.root.querySelector(".nova-copy");o&&(o.classList.toggle("nova-hidden",!n),o.onclick=t=>{t.stopPropagation(),n&&this.cb.onCopyLink(n),this.panel.classList.remove("open")})}},_=`
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
</style>`,z=`
<button class="nova-btn" style="--nova-arrow:url(&quot;data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'%3E%3Cpath fill='black' d='M12 3v10m0 0 4-4m-4 4L8 9m-5 8v2a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-2' stroke='black' stroke-width='2.4' fill='none' stroke-linecap='round' stroke-linejoin='round'/%3E%3C/svg%3E&quot;)">
  <span class="nova-icon"></span><span>Nova</span>
</button>`,A=`
<div class="nova-panel">
  <div class="nova-head">
    <b>Download quality</b>
    <button class="nova-rescan" title="Rescan sources">\u27F3</button>
    <button class="nova-close" title="Close">\u2715</button>
  </div>
  <div class="nova-list"></div>
  <button class="nova-copy">Copy direct link</button>
</div>`;var l=null,E=new WeakMap,v=null;async function N(){l=await w(),l.enabled&&m(location.href,l)&&(I(),await p(),setInterval(()=>void p(),2500))}async function p(){if(!l?.enabled||!m(location.href,l))return;let n=(await c({type:"seen-media"}))?.items??[],o=k(n.map(t=>({id:"net:"+t.url,url:t.url,kind:t.kind,from:"network"})));for(let t of o){f(t.video);let i=E.get(t.video);i||(i={video:t.video,overlay:u.create(t.video,{onDownload:r=>void W(r),onCopyLink:r=>navigator.clipboard?.writeText(r).catch(()=>{}),onRescan:()=>void p()})},E.set(t.video,i));let a=await O(t.sources,t.drm);i.overlay.update(a,t.drm)}}async function O(e,n){if(n)return[{id:"drm",url:"",kind:"file",label:"Protected (DRM) content",downloadable:!1,note:"This stream is protected and cannot be downloaded."}];let o=[],t=new Map(e.map(a=>[a.url,a]));for(let a of t.values()){let r=await c({type:"media-probe",url:a.url,referer:location.href});r?.candidates&&o.push(...r.candidates)}let i=new Map;for(let a of o)i.has(a.id)||i.set(a.id,a);return[...i.values()].sort((a,r)=>Number(r.downloadable)-Number(a.downloadable))}async function W(e){await c({type:"add-download",url:e.url,kind:e.kind,mediaSelection:e.kind==="hls"?{type:"hls",variant_url:e.url,preferred_height:e.height??null}:e.kind==="dash"?{type:"dash",video_rep_id:e.id.startsWith("audio:")?void 0:e.id,audio_rep_id:e.id.startsWith("audio:")?e.id.slice(6):void 0}:void 0,referer:location.href})}function I(){v||(v=new MutationObserver(()=>{p()}),v.observe(document.documentElement,{childList:!0,subtree:!0}))}N();})();
