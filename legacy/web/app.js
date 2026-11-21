'use strict';

/* =====================================================================
 * 视频对比工具 —— 前端逻辑
 * 左右两栏各自独立播放，元数据由 /api/probe 提供（ffprobe 优先）
 * ===================================================================== */

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));

function el(tag, cls, text) {
  const node = document.createElement(tag);
  if (cls) node.className = cls;
  if (text !== undefined && text !== null) node.textContent = text;
  return node;
}

/* ------------------------------ 格式化 ------------------------------ */
const DASH = '—';

function fmtSize(bytes) {
  if (!bytes && bytes !== 0) return DASH;
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = Number(bytes);
  let i = 0;
  while (value >= 1024 && i < units.length - 1) { value /= 1024; i++; }
  return i === 0 ? `${Math.round(value)} B` : `${value.toFixed(value < 10 ? 2 : 1)} ${units[i]}`;
}

function fmtBitrate(bps) {
  if (!bps) return DASH;
  const v = Number(bps);
  if (v >= 1e6) return `${(v / 1e6).toFixed(2)} Mbps`;
  if (v >= 1e3) return `${(v / 1e3).toFixed(0)} kbps`;
  return `${Math.round(v)} bps`;
}

function fmtDuration(seconds) {
  if (!seconds && seconds !== 0) return DASH;
  const s = Number(seconds);
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = Math.floor(s % 60);
  const cs = Math.floor((s - Math.floor(s)) * 100);
  const pad = (n) => String(n).padStart(2, '0');
  return h > 0 ? `${h}:${pad(m)}:${pad(sec)}.${pad(cs)}` : `${pad(m)}:${pad(sec)}.${pad(cs)}`;
}

function fmtClock(seconds) {
  if (!seconds && seconds !== 0) return '00:00.00';
  return fmtDuration(Math.max(0, Number(seconds)));
}

function fmtFps(fps) {
  if (!fps) return DASH;
  const v = Number(fps);
  const text = Number.isInteger(v) ? String(v) : v.toFixed(3).replace(/0+$/, '').replace(/\.$/, '');
  return `${text} fps`;
}

function fmtSampleRate(rate) {
  if (!rate) return DASH;
  const v = Number(rate);
  return v % 1000 === 0 ? `${(v / 1000).toFixed(0)} kHz` : `${(v / 1000).toFixed(1)} kHz`;
}

/* ------------------------------ 请求封装 ------------------------------ */
async function api(path, options) {
  const res = await fetch(path, options);
  let data = null;
  try {
    data = await res.json();
  } catch (err) {
    throw new Error(`响应解析失败（HTTP ${res.status}）`);
  }
  if (!res.ok || data.ok === false) {
    const error = new Error(data.error || `请求失败（HTTP ${res.status}）`);
    error.payload = data;
    throw error;
  }
  return data;
}

function postJSON(path, body) {
  return api(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body || {}),
  });
}

/* ------------------------------ Toast ------------------------------ */
function toast(message, kind = 'info', timeout = 3600) {
  const box = $('#toasts');
  const node = el('div', `toast ${kind}`, message);
  box.appendChild(node);
  setTimeout(() => {
    node.style.opacity = '0';
    node.style.transition = 'opacity .25s';
    setTimeout(() => node.remove(), 260);
  }, timeout);
}

/* =====================================================================
 * 元数据字段定义
 * ===================================================================== */
const FIELD_GROUPS = [
  {
    title: '文件信息',
    rows: [
      { key: 'name', label: '文件名', mono: true, noDiff: true, get: (m) => m.name, title: (m) => m.path },
      { key: 'container', label: '容器格式', get: (m) => m.container },
      { key: 'duration', label: '时长', get: (m) => fmtDuration(m.duration) },
      { key: 'size', label: '文件大小', noDiff: true, get: (m) => m.size_human || fmtSize(m.size) },
      { key: 'bitrate_total', label: '总码率', get: (m) => fmtBitrate(m.bitrate_total) },
      { key: 'mtime', label: '修改时间', mono: true, noDiff: true, get: (m) => m.mtime || DASH },
      {
        key: 'storage', label: '存储位置', noDiff: true,
        get: (m) => (m.network ? '网络共享（SMB/UNC）' : '本地磁盘'),
        title: (m) => m.path,
      },
    ],
  },
  {
    title: '视频',
    rows: [
      {
        key: 'video.codec', label: '编码方式',
        get: (m) => (m.video ? m.video.codec : DASH),
      },
      {
        key: 'video.profile', label: 'Profile / Level',
        get: (m) => {
          const v = m.video || {};
          const parts = [v.profile, v.level ? `Level ${v.level}` : null].filter(Boolean);
          return parts.length ? parts.join(' · ') : DASH;
        },
      },
      {
        key: 'video.resolution', label: '分辨率',
        get: (m) => {
          const v = m.video || {};
          if (!v.width || !v.height) return DASH;
          const ar = v.display_aspect || (() => {
            const gcd = (a, b) => (b ? gcd(b, a % b) : a);
            const g = gcd(v.width, v.height) || 1;
            return `${v.width / g}:${v.height / g}`;
          })();
          return `${v.width} × ${v.height}（${ar}）`;
        },
      },
      { key: 'video.fps', label: '帧率', get: (m) => fmtFps((m.video || {}).fps) },
      { key: 'video.bitrate', label: '视频码率', get: (m) => fmtBitrate((m.video || {}).bitrate) },
      { key: 'video.frames', label: '总帧数', mono: true, get: (m) => { const f = (m.video || {}).frames; return f ? `${f.toLocaleString()} 帧` : DASH; } },
      { key: 'video.pix_fmt', label: '像素格式', mono: true, get: (m) => (m.video || {}).pix_fmt || DASH },
      { key: 'video.bit_depth', label: '位深', get: (m) => { const d = (m.video || {}).bit_depth; return d ? `${d} bit` : DASH; } },
      { key: 'video.color', label: '色彩 / HDR', get: (m) => { const v = m.video || {}; return v.color_desc || v.color_space || DASH; } },
      {
        key: 'video.rotation', label: '旋转 / 场序',
        get: (m) => {
          const v = m.video || {};
          const parts = [];
          if (v.rotation) parts.push(`旋转 ${v.rotation}°`);
          if (v.field_order) parts.push(v.field_order === 'progressive' ? '逐行' : '隔行');
          return parts.length ? parts.join(' · ') : DASH;
        },
      },
    ],
  },
  {
    title: '音频',
    rows: [
      { key: 'audio.codec', label: '编码方式', get: (m) => { const a = m.audio; if (!a) return '无音轨'; return a.profile ? `${a.codec}（${a.profile}）` : a.codec; } },
      {
        key: 'audio.channels', label: '声道',
        get: (m) => {
          const a = m.audio;
          if (!a) return '无音轨';
          if (a.channel_layout) return a.channel_layout;
          return a.channels ? `${a.channels} 声道` : DASH;
        },
      },
      { key: 'audio.sample_rate', label: '采样率', get: (m) => (m.audio ? fmtSampleRate(m.audio.sample_rate) : '无音轨') },
      { key: 'audio.bit_depth', label: '位深', get: (m) => { const a = m.audio; if (!a) return '无音轨'; return a.bit_depth ? `${a.bit_depth} bit` : DASH; } },
      { key: 'audio.bitrate', label: '码率', get: (m) => (m.audio ? fmtBitrate(m.audio.bitrate) : '无音轨') },
      { key: 'audio.language', label: '语言', noDiff: true, get: (m) => (m.audio && m.audio.language) || DASH },
      { key: 'audio.count', label: '音轨数量', get: (m) => (m.audio_stream_count ? `${m.audio_stream_count} 条` : (m.audio ? '1 条' : '0 条')) },
    ],
  },
  {
    title: '其他',
    rows: [
      { key: 'streams.subtitle', label: '字幕流', get: (m) => `${m.subtitle_stream_count || 0} 条` },
      { key: 'streams.attachment', label: '附件流', get: (m) => `${m.attachment_count || 0} 条` },
      { key: 'chapters', label: '章节', get: (m) => `${m.chapters || 0} 个` },
      { key: 'engine', label: '解析引擎', mono: true, noDiff: true, get: (m) => m.engine || DASH },
      { key: 'path', label: '完整路径', mono: true, noDiff: true, get: (m) => m.path, title: (m) => m.path },
    ],
  },
];

/* =====================================================================
 * 面板
 * ===================================================================== */
const PANE_HTML = `
  <div class="pane-head">
    <span class="badge {{side}}">{{label}}</span>
    <div class="fname" data-role="fname">未选择视频</div>
    <button class="btn primary" data-act="pick">选择视频…</button>
  </div>
  <div class="stage">
    <video data-role="video" preload="metadata" playsinline></video>
    <img class="frame" data-role="frame" hidden alt="帧预览">
    <div class="stage-overlay" data-role="overlay">
      <div class="overlay-card">
        <div class="overlay-title" data-role="overlay-title">点击「选择视频…」载入{{label}}侧视频</div>
        <div class="overlay-sub" data-role="overlay-sub">支持 MP4 / MKV / MOV / AVI / WebM 等格式，浏览器无法解码时可自动转码或用帧预览</div>
        <div class="overlay-actions" data-role="overlay-actions"></div>
      </div>
    </div>
    <div class="stage-tag" data-role="tag" hidden></div>
    <div class="stage-progress" data-role="progress" hidden>
      <div class="bar"><i data-role="progress-bar"></i></div>
      <span data-role="progress-text">转码中…</span>
      <button class="icon-btn" data-role="progress-cancel" title="取消转码">✕</button>
    </div>
  </div>
  <div class="transport">
    <button class="icon-btn" data-act="play" title="播放 / 暂停（空格）">▶</button>
    <button class="icon-btn" data-act="prev-frame" title="上一帧（Shift + ←）">◀|</button>
    <button class="icon-btn" data-act="next-frame" title="下一帧（Shift + →）">|▶</button>
    <span class="time" data-role="time">00:00.00 / 00:00.00</span>
    <input type="range" class="seek" data-role="seek" min="0" max="1000" step="1" value="0" disabled>
    <button class="icon-btn" data-act="mute" title="静音">🔊</button>
    <input type="range" class="vol" data-role="vol" min="0" max="100" step="1" value="100" disabled title="音量">
    <select class="speed" data-role="speed" title="播放速度">
      <option value="0.25">0.25×</option>
      <option value="0.5">0.5×</option>
      <option value="0.75">0.75×</option>
      <option value="1" selected>1×</option>
      <option value="1.25">1.25×</option>
      <option value="1.5">1.5×</option>
      <option value="2">2×</option>
      <option value="4">4×</option>
    </select>
  </div>
  <div class="ops">
    <button class="btn" data-act="rename" title="重命名当前文件">重命名</button>
    <button class="btn" data-act="move" title="把当前文件移动到其他文件夹">移动</button>
    <button class="btn danger" data-act="delete" title="把当前文件移到回收站">删除</button>
    <button class="btn ghost" data-act="stop" title="停止播放并释放文件占用（删除/移动/重命名前可用）">停止</button>
    <button class="btn ghost" data-act="reveal" title="在资源管理器中定位文件">打开位置</button>
    <button class="btn ghost" data-act="reload" title="重新读取文件与元数据">重载</button>
  </div>
  <div class="info" data-role="info"></div>
`;

const panes = {};
const app = {
  link: true,
  syncVolume: true,
  guardUntil: 0,
};

function otherPane(pane) {
  return panes[pane.side === 'left' ? 'right' : 'left'];
}

function createPane(side, mount) {
  const node = el('section', `pane ${side}`);
  node.dataset.side = side;
  const label = side === 'left' ? '左' : '右';
  node.innerHTML = PANE_HTML.replaceAll('{{side}}', side).replaceAll('{{label}}', label);
  mount.appendChild(node);

  const pane = {
    side,
    node,
    meta: null,
    path: null,
    mode: 'none',          // none | video | frames
    transcoded: false,
    transcoding: false,
    jobId: null,
    frameTime: 0,
    frameTimer: null,
    frameSeq: 0,
    watchdog: null,
    video: $('[data-role="video"]', node),
    img: $('[data-role="frame"]', node),
    fname: $('[data-role="fname"]', node),
    overlay: $('[data-role="overlay"]', node),
    overlayTitle: $('[data-role="overlay-title"]', node),
    overlaySub: $('[data-role="overlay-sub"]', node),
    overlayActions: $('[data-role="overlay-actions"]', node),
    tag: $('[data-role="tag"]', node),
    progress: $('[data-role="progress"]', node),
    progressBar: $('[data-role="progress-bar"]', node),
    progressText: $('[data-role="progress-text"]', node),
    timeLabel: $('[data-role="time"]', node),
    seek: $('[data-role="seek"]', node),
    vol: $('[data-role="vol"]', node),
    speed: $('[data-role="speed"]', node),
    infoBox: $('[data-role="info"]', node),
    playBtn: $('[data-act="play"]', node),
    muteBtn: $('[data-act="mute"]', node),
  };
  panes[side] = pane;
  wirePane(pane);
  renderInfo(pane);
  return pane;
}

/* ------------------------------ 面板事件 ------------------------------ */
function wirePane(pane) {
  const { node, video } = pane;

  node.addEventListener('click', (event) => {
    const btn = event.target.closest('[data-act]');
    if (!btn) return;
    event.stopPropagation();
    handleAction(pane, btn.dataset.act);
  });

  video.addEventListener('loadedmetadata', () => {
    clearWatchdog(pane);
    pane.seek.disabled = false;
    pane.vol.disabled = false;
    hideOverlay(pane);
    updateTimeDisplay(pane);
    applySpeed(pane);
    if (pane.vol.dataset.touched !== '1') {
      pane.vol.value = String(Math.round(video.volume * 100));
    }
  });
  video.addEventListener('timeupdate', () => updateTimeDisplay(pane));
  video.addEventListener('durationchange', () => updateTimeDisplay(pane));
  video.addEventListener('play', () => {
    setPlayIcon(pane, true);
    if (app.link && now() > app.guardUntil) playOther(pane, true);
  });
  video.addEventListener('pause', () => {
    setPlayIcon(pane, false);
    if (app.link && now() > app.guardUntil) playOther(pane, false);
  });
  video.addEventListener('seeked', () => {
    updateTimeDisplay(pane);
    if (app.link && now() > app.guardUntil) mirrorSeek(pane);
  });
  video.addEventListener('volumechange', () => {
    pane.muteBtn.textContent = video.muted || video.volume === 0 ? '🔇' : '🔊';
    if (app.syncVolume && now() > app.guardUntil) {
      const other = otherPane(pane);
      if (other && other.meta && other.mode === 'video') {
        app.guardUntil = now() + 180;
        other.video.volume = video.volume;
        other.video.muted = video.muted;
        other.vol.value = String(Math.round(video.volume * 100));
      }
    }
  });
  video.addEventListener('error', () => {
    if (pane.mode === 'video') handleDecodeFailure(pane, 'error', video.error);
  });
  video.addEventListener('ended', () => {
    setPlayIcon(pane, false);
    if (pane.clipWindow) showClipEnded(pane);
  });

  pane.seek.addEventListener('input', () => {
    const duration = paneDuration(pane);
    if (!duration) return;
    seekTo(pane, (Number(pane.seek.value) / 1000) * duration);
    if (app.link) mirrorSeek(pane);
  });

  pane.vol.addEventListener('input', () => {
    pane.vol.dataset.touched = '1';
    const value = Number(pane.vol.value) / 100;
    app.guardUntil = now() + 120;
    pane.video.volume = value;
    pane.video.muted = value === 0;
  });

  pane.speed.addEventListener('change', () => {
    applySpeed(pane);
    const other = otherPane(pane);
    if (app.link && other && other.meta) {
      other.speed.value = pane.speed.value;
      applySpeed(other);
    }
  });

  const cancelBtn = $('[data-role="progress-cancel"]', node);
  cancelBtn.addEventListener('click', async () => {
    if (pane.jobId) {
      try { await postJSON('/api/transcode/cancel', { jobId: pane.jobId }); } catch (e) { /* ignore */ }
    }
    pane.transcoding = false;
    hideProgress(pane);
    toast('已取消转码', 'warn');
  });
}

function now() {
  return performance.now();
}

/* ------------------------------ 界面辅助 ------------------------------ */
function setPlayIcon(pane, playing) {
  pane.playBtn.textContent = playing ? '⏸' : '▶';
}

function setTag(pane, text, kind) {
  if (!text) {
    pane.tag.hidden = true;
    return;
  }
  pane.tag.hidden = false;
  pane.tag.textContent = text;
  pane.tag.className = 'stage-tag' + (kind ? ' ' + kind : '');
}

function showOverlay(pane, title, sub, actions) {
  pane.overlay.hidden = false;
  pane.overlayTitle.textContent = title || '';
  pane.overlaySub.textContent = sub || '';
  pane.overlayActions.innerHTML = '';
  (actions || []).forEach((action) => {
    const btn = el('button', 'btn ' + (action.kind || 'ghost'), action.label);
    btn.addEventListener('click', action.onClick);
    pane.overlayActions.appendChild(btn);
  });
}

function hideOverlay(pane) {
  pane.overlay.hidden = true;
}

function showProgress(pane, text, ratio) {
  pane.progress.hidden = false;
  pane.progressText.textContent = text;
  pane.progressBar.style.width = `${Math.round((ratio || 0) * 100)}%`;
}

function hideProgress(pane) {
  pane.progress.hidden = true;
  pane.progressBar.style.width = '0%';
}

function clearWatchdog(pane) {
  if (pane.watchdog) {
    clearTimeout(pane.watchdog);
    pane.watchdog = null;
  }
}

/* ------------------------------ 时间与进度 ------------------------------ */
function paneDuration(pane) {
  // 片段转码时，进度条仍按“整片时长”显示，方便和另一侧对齐
  if (pane.clipWindow) return (pane.meta && pane.meta.duration) || pane.video.duration || 0;
  if (pane.mode === 'video') {
    const d = pane.video.duration;
    if (Number.isFinite(d) && d > 0) return d;
  }
  return (pane.meta && pane.meta.duration) || 0;
}

function paneTime(pane) {
  if (pane.mode === 'video') return (pane.video.currentTime || 0) + (pane.clipOffset || 0);
  return pane.frameTime || 0;
}

function updateTimeDisplay(pane) {
  const duration = paneDuration(pane);
  const current = paneTime(pane);
  pane.timeLabel.textContent = `${fmtClock(current)} / ${fmtClock(duration)}`;
  if (duration > 0) {
    pane.seek.value = String(Math.min(1000, Math.round((current / duration) * 1000)));
  }
}

function seekTo(pane, seconds) {
  const duration = paneDuration(pane);
  const target = Math.max(0, Math.min(duration || seconds, seconds));
  if (pane.mode === 'video') {
    const offset = pane.clipOffset || 0;
    if (pane.clipWindow) {
      const local = target - offset;
      if (local >= -0.2 && local <= pane.clipWindow - 0.3) {
        app.guardUntil = now() + 150;
        try { pane.video.currentTime = Math.max(0, local); } catch (e) { /* ignore */ }
        updateTimeDisplay(pane);
        return;
      }
      // 目标不在当前片段内：从该位置重新转码一段
      requestClipAt(pane, target);
      updateTimeDisplay(pane);
      return;
    }
    app.guardUntil = now() + 150;
    try { pane.video.currentTime = target; } catch (e) { /* ignore */ }
  } else if (pane.mode === 'frames') {
    pane.frameTime = target;
    requestFrame(pane);
  }
  updateTimeDisplay(pane);
}

let clipSeekTimer = null;

function requestClipAt(pane, globalTime) {
  if (clipSeekTimer) clearTimeout(clipSeekTimer);
  clipSeekTimer = setTimeout(() => {
    clipSeekTimer = null;
    if (pane.meta) startTranscode(pane, { start: globalTime });
  }, 350);
}

function applySpeed(pane) {
  const value = Number(pane.speed.value) || 1;
  if (pane.mode === 'video') {
    try { pane.video.playbackRate = value; } catch (e) { /* ignore */ }
  }
}

function stopVideo(pane, silent) {
  // 停止播放并释放浏览器侧占用：断开 <video> 的网络流，让服务器句柄落下
  if (!pane.meta) {
    if (!silent) toast('尚未选择视频', 'warn');
    return;
  }
  const wasLoaded = Boolean(pane.meta);
  clearWatchdog(pane);
  if (pane.frameTimer) { clearInterval(pane.frameTimer); pane.frameTimer = null; }
  const mode = pane.mode;
  pane.video.pause();
  pane.video.removeAttribute('src');
  try { pane.video.load(); } catch (e) { /* ignore */ }
  pane.img.removeAttribute('src');
  setPlayIcon(pane, false);
  pane.seek.value = '0';
  pane.seek.disabled = true;
  pane.vol.disabled = true;
  pane.timeLabel.textContent = '00:00.00 / 00:00.00';
  setTag(pane, mode === 'frames' ? '已停止（帧预览已断开）' : '已停止（已释放文件占用）');
  if (!silent && wasLoaded) toast('已停止播放并释放文件占用', 'ok');
  updateDiff();
}

/* ------------------------------ 播放控制 ------------------------------ */
function setPlaying(pane, playing) {
  if (!pane.meta) return;
  if (pane.mode === 'video') {
    app.guardUntil = now() + 200;
    if (playing) {
      const promise = pane.video.play();
      if (promise && promise.catch) promise.catch(() => setPlayIcon(pane, false));
    } else {
      pane.video.pause();
    }
  } else if (pane.mode === 'frames') {
    framePlay(pane, playing);
  }
}

function isPlaying(pane) {
  if (pane.mode === 'video') return !pane.video.paused && !pane.video.ended;
  if (pane.mode === 'frames') return Boolean(pane.frameTimer);
  return false;
}

function playOther(pane, playing) {
  const other = otherPane(pane);
  if (!other || !other.meta) return;
  if (playing === isPlaying(other)) return;
  app.guardUntil = now() + 220;
  setPlaying(other, playing);
}

function mirrorSeek(pane) {
  const other = otherPane(pane);
  if (!other || !other.meta) return;
  const target = paneTime(pane);
  if (Math.abs(paneTime(other) - target) < 0.25) return;
  app.guardUntil = now() + 220;
  seekTo(other, target);
}

/* ------------------------------ 帧预览 ------------------------------ */
function requestFrame(pane) {
  if (!pane.path) return;
  pane.frameSeq += 1;
  const url = `/api/frame?path=${encodeURIComponent(pane.path)}&t=${pane.frameTime.toFixed(3)}&w=1600&_=${pane.frameSeq}`;
  pane.img.src = url;
}

function framePlay(pane, playing) {
  if (pane.frameTimer) {
    clearInterval(pane.frameTimer);
    pane.frameTimer = null;
  }
  setPlayIcon(pane, playing);
  if (!playing) return;
  const fps = (pane.meta && pane.meta.video && pane.meta.video.fps) || 25;
  const tick = 120;
  const duration = paneDuration(pane);
  pane.frameTimer = setInterval(() => {
    pane.frameTime += Math.max(1 / fps, tick / 1000);
    if (duration && pane.frameTime >= duration) {
      pane.frameTime = duration;
      framePlay(pane, false);
    }
    requestFrame(pane);
    updateTimeDisplay(pane);
    if (app.link && now() > app.guardUntil) {
      const other = otherPane(pane);
      if (other && other.mode === 'frames' && other.meta) {
        app.guardUntil = now() + 200;
        other.frameTime = pane.frameTime;
        requestFrame(other);
        updateTimeDisplay(other);
      }
    }
  }, tick);
}

function enterFrameMode(pane, reason) {
  if (!pane.meta) return;
  if (pane.frameTimer) { clearInterval(pane.frameTimer); pane.frameTimer = null; }
  pane.mode = 'frames';
  pane.video.pause();
  pane.video.removeAttribute('src');
  try { pane.video.load(); } catch (e) { /* ignore */ }
  pane.video.hidden = true;
  pane.img.hidden = false;
  pane.frameTime = Math.min(pane.frameTime || 0, paneDuration(pane) || 0);
  requestFrame(pane);
  setPlayIcon(pane, false);
  hideOverlay(pane);
  pane.seek.disabled = false;
  pane.vol.disabled = true;
  setTag(pane, reason || '帧预览模式（浏览器无法解码，逐帧服务端解码）', 'warn');
  updateTimeDisplay(pane);
}

/* ------------------------------ 解码失败处理 ------------------------------ */
const CLIP_WINDOW = 60;        // 片段转码默认时长（秒）
const CLIP_FULL_LIMIT = 1.2e9; // 小于该体积才提供“整片转码”（约 1.2 GB）

function handleDecodeFailure(pane, reason = 'error', err = null) {
  clearWatchdog(pane);
  if (pane.transcoding) return;

  const codeText = {
    1: '加载被中止',
    2: '网络错误',
    3: '解码失败',
    4: '编码或容器不受支持',
  }[err && err.code] || '';
  const codec = (pane.meta && pane.meta.video && pane.meta.video.codec) || '未知编码';

  if (pane.transcoded && pane.clipWindow && reason !== 'timeout') {
    toast('该片段仍无法播放，改用帧预览', 'warn');
    enterFrameMode(pane);
    return;
  }

  const isTimeout = reason === 'timeout';
  const title = isTimeout ? '视频加载超时' : '浏览器无法解码该视频';
  const sub = isTimeout
    ? '文件较大或网络较慢时可能超时。可以重试、转码当前片段，或用帧预览。'
    : `${codec}${codeText ? '（' + codeText + '）' : ''}。可转码当前片段（约 ${CLIP_WINDOW} 秒）后用 H.264 播放，或用逐帧预览。`;

  const actions = [
    {
      label: `转码当前片段（${CLIP_WINDOW} 秒）`,
      kind: 'primary',
      onClick: () => { hideOverlay(pane); startTranscode(pane, { start: paneTime(pane) }); },
    },
  ];
  const size = (pane.meta && pane.meta.size) || 0;
  if (!size || size < CLIP_FULL_LIMIT) {
    actions.push({
      label: '整片转码',
      kind: 'ghost',
      onClick: () => { hideOverlay(pane); startTranscode(pane, { start: 0, window: null }); },
    });
  }
  actions.push({ label: '帧预览', kind: 'ghost', onClick: () => { hideOverlay(pane); enterFrameMode(pane); } });
  actions.push({
    label: '重试',
    kind: 'ghost',
    onClick: () => {
      hideOverlay(pane);
      if (pane.meta) setVideoSource(pane, pane.meta.path, { autoplay: true, startTime: paneTime(pane) });
    },
  });

  showOverlay(pane, title, sub, actions);
}

/* ------------------------------ 转码 ------------------------------ */
async function startTranscode(pane, options = {}) {
  if (!pane.meta) return;
  if (pane.transcoding) return;
  const total = pane.meta.duration || 0;
  const wantWindow = options.window === undefined ? CLIP_WINDOW : options.window;
  let start = Math.max(0, options.start === undefined ? paneTime(pane) : options.start);
  let window = wantWindow;
  if (window && total) {
    if (start >= total - 1) start = Math.max(0, total - 5);
    window = Math.min(window, Math.max(5, total - start));
  }

  pane.transcoding = true;
  pane.jobId = null;
  showProgress(
    pane,
    window ? `正在转码片段（${fmtClock(start)} 起 ${Math.round(window)} 秒）…` : '正在准备整片转码…',
    0,
  );
  try {
    const { job } = await postJSON('/api/transcode', {
      path: pane.meta.path,
      duration: total,
      start,
      window,
    });
    pane.jobId = job.jobId;
    await pollTranscode(pane, job, { start, window });
  } catch (err) {
    pane.transcoding = false;
    hideProgress(pane);
    toast(`转码启动失败：${err.message}`, 'error', 5000);
    enterFrameMode(pane, '帧预览模式（转码不可用）');
  }
}

async function pollTranscode(pane, job, clip) {
  let state = job;
  while (pane.transcoding) {
    if (state.state === 'done') {
      pane.transcoding = false;
      hideProgress(pane);
      pane.transcoded = true;
      applyClipSource(pane, state.out, clip);
      toast(
        clip && clip.window
          ? `片段就绪：${fmtClock(clip.start)} 起 ${Math.round(clip.window)} 秒`
          : '转码完成，已切换为兼容播放',
        'ok',
      );
      return;
    }
    if (state.state === 'error') {
      pane.transcoding = false;
      hideProgress(pane);
      toast(`转码失败：${state.message || '未知错误'}`, 'error', 6000);
      enterFrameMode(pane, '帧预览模式（转码失败）');
      return;
    }
    showProgress(pane, `转码中… ${Math.round((state.progress || 0) * 100)}%`, state.progress || 0);
    await new Promise((r) => setTimeout(r, 700));
    try {
      const res = await api(`/api/transcode/status?jobId=${encodeURIComponent(state.jobId)}`);
      state = res.job;
    } catch (err) {
      pane.transcoding = false;
      hideProgress(pane);
      toast(`转码状态查询失败：${err.message}`, 'error');
      enterFrameMode(pane, '帧预览模式');
      return;
    }
  }
}

function applyClipSource(pane, outPath, clip) {
  const start = (clip && clip.start) || 0;
  const window = (clip && clip.window) || null;
  pane.clipOffset = start;
  pane.clipWindow = window;
  setVideoSource(pane, outPath, { transcoded: true, startTime: start, autoplay: true });
  if (window) {
    setTag(pane, `转码片段 ${fmtClock(start)} – ${fmtClock(start + window)}`, 'ok');
  } else {
    setTag(pane, '整片转码播放（H.264 + AAC）', 'ok');
  }
}

function showClipEnded(pane) {
  const start = pane.clipOffset || 0;
  const window = pane.clipWindow || 0;
  const total = (pane.meta && pane.meta.duration) || 0;
  const next = start + window;
  const actions = [];
  if (!total || next < total - 1) {
    actions.push({
      label: '继续下一段',
      kind: 'primary',
      onClick: () => { hideOverlay(pane); startTranscode(pane, { start: next }); },
    });
    actions.push({
      label: '跳 5 分钟',
      kind: 'ghost',
      onClick: () => { hideOverlay(pane); startTranscode(pane, { start: Math.min(Math.max(0, total - 5), next + 300) }); },
    });
  }
  actions.push({ label: '从头开始', kind: 'ghost', onClick: () => { hideOverlay(pane); startTranscode(pane, { start: 0 }); } });
  actions.push({ label: '帧预览', kind: 'ghost', onClick: () => { hideOverlay(pane); enterFrameMode(pane); } });
  showOverlay(
    pane,
    '本段播放结束',
    `已播完 ${fmtClock(start)} – ${fmtClock(next)}。继续播放会从下一段开始转码（约 ${CLIP_WINDOW} 秒）。`,
    actions,
  );
}

/* =====================================================================
 * 载入视频
 * ===================================================================== */
function resetPane(pane) {
  clearWatchdog(pane);
  if (pane.frameTimer) { clearInterval(pane.frameTimer); pane.frameTimer = null; }
  pane.transcoded = false;
  pane.transcoding = false;
  pane.jobId = null;
  pane.frameTime = 0;
  pane.clipOffset = 0;
  pane.clipWindow = null;
  pane.mode = 'none';
  pane.video.pause();
  pane.video.removeAttribute('src');
  try { pane.video.load(); } catch (e) { /* ignore */ }
  pane.video.hidden = false;
  pane.img.hidden = true;
  pane.img.removeAttribute('src');
  setPlayIcon(pane, false);
  setTag(pane, '');
  hideProgress(pane);
  pane.seek.value = '0';
  pane.seek.disabled = true;
  pane.vol.disabled = true;
  pane.timeLabel.textContent = '00:00.00 / 00:00.00';
}

async function loadVideo(side, path, options = {}) {
  const pane = panes[side];
  if (!pane) return;
  resetPane(pane);
  showOverlay(pane, '正在读取视频信息…', path, []);
  let meta;
  try {
    const res = await api(`/api/probe?path=${encodeURIComponent(path)}`);
    meta = res.meta;
  } catch (err) {
    showOverlay(pane, '无法读取视频', err.message, [
      { label: '重新选择', kind: 'primary', onClick: () => pickAndLoad(side) },
    ]);
    toast(`读取失败：${err.message}`, 'error', 5000);
    return;
  }
  pane.meta = meta;
  pane.path = meta.path;
  pane.fname.textContent = meta.name;
  pane.fname.classList.add('loaded');
  pane.fname.title = meta.path;
  renderInfo(pane);
  updateDiff();
  try { localStorage.setItem(`vc.dir.${side}`, meta.dir); } catch (e) { /* ignore */ }

  if (!meta.video) {
    showOverlay(pane, '未识别到视频流', '该文件可能不是视频，或解析失败', [
      { label: '重新选择', kind: 'primary', onClick: () => pickAndLoad(side) },
    ]);
    return;
  }
  setVideoSource(pane, meta.path, {
    startTime: options.startTime || 0,
    autoplay: options.autoplay !== false,
  });
}

function setVideoSource(pane, path, options = {}) {
  pane.mode = 'video';
  pane.video.hidden = false;
  pane.img.hidden = true;
  pane.video.src = `/stream?path=${encodeURIComponent(path)}&_=${Date.now()}`;
  try { pane.video.load(); } catch (e) { /* ignore */ }
  if (options.transcoded) {
    setTag(pane, '转码播放（H.264 + AAC）', 'ok');
  } else if (pane.meta && pane.meta.browser_playable === false) {
    setTag(pane, '浏览器可能不支持该编码', 'warn');
  } else {
    setTag(pane, '');
  }
  hideOverlay(pane);
  pane.seek.disabled = false;
  pane.vol.disabled = false;

  const offset = pane.clipOffset || 0;
  const startAt = Math.max(0, (options.startTime || 0) - offset);
  const onMeta = () => {
    pane.video.removeEventListener('loadedmetadata', onMeta);
    if (startAt > 0 && startAt < (pane.video.duration || 1e9)) {
      try { pane.video.currentTime = startAt; } catch (e) { /* ignore */ }
    }
    if (options.autoplay) {
      const promise = pane.video.play();
      if (promise && promise.catch) {
        promise.catch(() => {
          setPlayIcon(pane, false);
          showOverlay(pane, '已就绪，点击播放', '浏览器阻止了自动播放', [
            { label: '播放', kind: 'primary', onClick: () => { hideOverlay(pane); setPlaying(pane, true); } },
          ]);
        });
      }
    }
    updateTimeDisplay(pane);
  };
  pane.video.addEventListener('loadedmetadata', onMeta);

  armWatchdog(pane, 0);
  updateTimeDisplay(pane);
}

function armWatchdog(pane, attempt) {
  clearWatchdog(pane);
  pane.watchdog = setTimeout(() => {
    if (pane.mode !== 'video' || !pane.meta) return;
    if (pane.video.readyState > 0) return;              // 已经拿到元数据，正常
    const err = pane.video.error;
    if (!err && attempt < 2) {                          // 没有错误、仍在缓冲：续期再等
      armWatchdog(pane, attempt + 1);
      return;
    }
    console.warn('[vc] decode failure', err && err.code, 'attempt', attempt);
    handleDecodeFailure(pane, err ? 'error' : 'timeout', err);
  }, 6000);
}

/* =====================================================================
 * 信息渲染 & 差异对比
 * ===================================================================== */
function renderInfo(pane) {
  const box = pane.infoBox;
  box.innerHTML = '';
  if (!pane.meta) {
    box.appendChild(el('div', 'picker-empty', '尚未选择视频'));
    return;
  }
  const meta = pane.meta;
  for (const group of FIELD_GROUPS) {
    if (group.title === '音频' && !meta.video) continue;
    box.appendChild(el('div', 'info-group-title', group.title));
    for (const row of group.rows) {
      let value = '';
      try { value = row.get(meta) || ''; } catch (e) { value = ''; }
      const node = el('div', 'info-row');
      node.dataset.key = row.key;
      if (row.noDiff) node.dataset.nodiff = '1';
      node.appendChild(el('span', 'k', row.label));
      const valueNode = el('span', 'v' + (row.mono ? ' mono' : ''), value || DASH);
      if (!value) valueNode.classList.add('dim');
      if (row.title) {
        const title = row.title(meta);
        if (title) valueNode.title = title;
      }
      node.appendChild(valueNode);
      box.appendChild(node);
    }
  }
}

function updateDiff() {
  const left = panes.left;
  const right = panes.right;
  const badge = $('#diff-badge');
  if (!left.meta || !right.meta) {
    badge.hidden = true;
    $$('.info-row.diff').forEach((n) => n.classList.remove('diff'));
    return;
  }
  let count = 0;
  const rows = $$('.info-row', left.infoBox);
  for (const row of rows) {
    const key = row.dataset.key;
    if (!key || row.dataset.nodiff === '1') continue;
    const counterpart = $(`.info-row[data-key="${key}"]`, right.infoBox);
    if (!counterpart) continue;
    const leftValue = ($('.v', row) || {}).textContent || '';
    const rightValue = ($('.v', counterpart) || {}).textContent || '';
    const differs = leftValue && rightValue && leftValue !== rightValue;
    row.classList.toggle('diff', Boolean(differs));
    counterpart.classList.toggle('diff', Boolean(differs));
    if (differs) count += 1;
  }
  if (count > 0) {
    badge.hidden = false;
    badge.textContent = `差异 ${count}`;
    badge.title = `左右两侧共有 ${count} 项元数据不同`;
  } else {
    badge.hidden = false;
    badge.textContent = '一致';
  }
}

/* =====================================================================
 * 操作
 * ===================================================================== */
function handleAction(pane, action) {
  switch (action) {
    case 'pick':
      pickAndLoad(pane.side);
      break;
    case 'play':
      togglePlay(pane);
      break;
    case 'prev-frame':
      stepFrame(pane, -1);
      break;
    case 'next-frame':
      stepFrame(pane, 1);
      break;
    case 'mute':
      toggleMute(pane);
      break;
    case 'rename':
      openRename(pane);
      break;
    case 'move':
      moveFile(pane);
      break;
    case 'delete':
      deleteFile(pane);
      break;
    case 'stop':
      stopVideo(pane);
      break;
    case 'reveal':
      revealFile(pane);
      break;
    case 'reload':
      if (pane.meta) loadVideo(pane.side, pane.meta.path, { startTime: paneTime(pane) });
      break;
    default:
      break;
  }
}

async function pickAndLoad(side) {
  const path = await openPicker(side, 'file');
  if (path) loadVideo(side, path);
}

function togglePlay(pane) {
  if (!pane.meta) return;
  const playing = isPlaying(pane);
  if (app.link) {
    app.guardUntil = now() + 200;
    setPlaying(pane, !playing);
    const other = otherPane(pane);
    if (other.meta) setPlaying(other, !playing);
  } else {
    setPlaying(pane, !playing);
  }
}

function stepFrame(pane, direction) {
  if (!pane.meta) return;
  const fps = (pane.meta.video && pane.meta.video.fps) || 25;
  const delta = direction / fps;
  if (pane.mode === 'frames') {
    framePlay(pane, false);
    pane.frameTime = Math.max(0, pane.frameTime + delta);
    requestFrame(pane);
    updateTimeDisplay(pane);
    if (app.link) {
      const other = otherPane(pane);
      if (other.meta && other.mode === 'frames') {
        other.frameTime = pane.frameTime;
        requestFrame(other);
        updateTimeDisplay(other);
      }
    }
    return;
  }
  setPlaying(pane, false);
  seekTo(pane, paneTime(pane) + delta);
  if (app.link) {
    const other = otherPane(pane);
    if (other.meta) seekTo(other, paneTime(other) + delta);
  }
}

function toggleMute(pane) {
  const other = otherPane(pane);
  app.guardUntil = now() + 150;
  pane.video.muted = !pane.video.muted;
  if (app.syncVolume && other.meta && other.mode === 'video') other.video.muted = pane.video.muted;
}

/* ------------------------------ 重命名 ------------------------------ */
function openRename(pane) {
  if (!pane.meta) { toast('请先选择视频', 'warn'); return; }
  // 记住当前播放位置（停播后 paneTime 会归零，所以先取）
  const resumeTime = paneTime(pane);
  const modal = $('#rename');
  const input = $('[data-role="input"]', modal);
  const hint = $('[data-role="hint"]', modal);
  $('[data-role="current"]', modal).textContent = pane.meta.name;
  input.value = pane.meta.name;
  hint.textContent = '回车确认，Esc 取消';
  hint.style.color = '';
  modal.hidden = false;
  setTimeout(() => {
    const dot = input.value.lastIndexOf('.');
    input.focus();
    input.setSelectionRange(0, dot > 0 ? dot : input.value.length);
  }, 30);

  const close = () => { modal.hidden = true; };
  const submit = async () => {
    const name = input.value.trim();
    if (!name) { hint.textContent = '文件名不能为空'; hint.style.color = 'var(--red)'; return; }
    if (name === pane.meta.name) { close(); return; }
    try {
      const wasPlaying = !pane.video.paused;
      stopVideo(pane, true);  // 确认改名后才停播（服务端也会释放，双保险）
      const res = await postJSON('/api/fs/rename', { path: pane.meta.path, newName: name });
      close();
      if (res.renamed) {
        toast(`已重命名为 ${name}`, 'ok');
        await loadVideo(pane.side, res.path, { startTime: resumeTime, autoplay: wasPlaying });
      }
    } catch (err) {
      hint.textContent = err.message;
      hint.style.color = 'var(--red)';
    }
  };

  const onKey = (event) => {
    if (event.key === 'Enter') { event.preventDefault(); submit(); }
    if (event.key === 'Escape') { event.preventDefault(); close(); }
  };
  input.onkeydown = onKey;
  $('[data-role="ok"]', modal).onclick = submit;
  $('[data-role="cancel"]', modal).onclick = close;
  $('[data-role="close"]', modal).onclick = close;
}

/* ------------------------------ 移动 ------------------------------ */
async function moveFile(pane) {
  if (!pane.meta) { toast('请先选择视频', 'warn'); return; }
  const resumeTime = paneTime(pane);   // 先记住位置（选完文件夹后可能要停播释放占用）
  const startDir = pane.meta.dir;
  let dest = null;
  try {
    const res = await postJSON('/api/dialog/folder', { dir: startDir, title: '选择要移动到哪个文件夹' });
    dest = res.path;
  } catch (err) {
    toast(`系统对话框不可用：${err.message}，改用内置选择器`, 'warn');
    dest = await openPicker(pane.side, 'folder');
  }
  if (!dest) {
    // 用户取消移动：重新加载回原位置继续播
    await loadVideo(pane.side, pane.meta.path, { startTime: resumeTime, autoplay: true });
    return;
  }
  const wasPlaying = !pane.video.paused;
  stopVideo(pane, true);  // 确定要移动才停播（服务端也会释放，双保险）
  try {
    const res = await postJSON('/api/fs/move', { path: pane.meta.path, destDir: dest });
    if (!res.moved) {
      toast('文件已在该文件夹中', 'warn');
      await loadVideo(pane.side, pane.meta.path, { startTime: resumeTime, autoplay: wasPlaying });
      return;
    }
    toast('已移动到 ' + dest, 'ok');
    await loadVideo(pane.side, res.path, { startTime: resumeTime, autoplay: wasPlaying });
  } catch (err) {
    toast(`移动失败：${err.message}`, 'error', 5000);
  }
}

/* ------------------------------ 删除 ------------------------------ */
function deleteFile(pane) {
  if (!pane.meta) { toast('请先选择视频', 'warn'); return; }
  openConfirm({
    title: '删除文件',
    text: '确定要删除这个视频吗？文件将被移到 Windows 回收站。',
    file: `${pane.meta.name}\n${pane.meta.path}`,
    okLabel: '移到回收站',
    onOk: async () => {
      try {
        stopVideo(pane, true);  // 确认删除才停播（服务端同时释放句柄）
        await postJSON('/api/fs/delete', { path: pane.meta.path, mode: 'recycle' });
        toast('已移到回收站', 'ok');
        clearPane(pane);
      } catch (err) {
        if (err.payload && err.payload.canPermanent) {
          openConfirm({
            title: '永久删除？',
            text: '无法移入回收站，是否直接永久删除该文件（不可恢复）？',
            file: pane.meta.path,
            okLabel: '永久删除',
            onOk: async () => {
              try {
                stopVideo(pane, true);
                await postJSON('/api/fs/delete', { path: pane.meta.path, mode: 'permanent' });
                toast('文件已永久删除', 'ok');
                clearPane(pane);
              } catch (err2) {
                toast(`删除失败：${err2.message}`, 'error', 5000);
              }
            },
          });
        } else {
          toast(`删除失败：${err.message}`, 'error', 5000);
        }
      }
    },
  });
}

function clearPane(pane) {
  resetPane(pane);
  pane.meta = null;
  pane.path = null;
  pane.fname.textContent = '未选择视频';
  pane.fname.classList.remove('loaded');
  pane.fname.title = '';
  renderInfo(pane);
  updateDiff();
  showOverlay(
    pane,
    `点击「选择视频…」载入${pane.side === 'left' ? '左' : '右'}侧视频`,
    '支持 MP4 / MKV / MOV / AVI / WebM 等格式',
    [],
  );
}

/* ------------------------------ 资源管理器定位 ------------------------------ */
async function revealFile(pane) {
  if (!pane.meta) { toast('请先选择视频', 'warn'); return; }
  try {
    const res = await postJSON('/api/fs/reveal', { path: pane.meta.path });
    if (res.mode === 'shell') {
      toast('已在资源管理器中定位并选中该文件', 'ok');
    } else {
      toast('已打开文件所在文件夹（当前权限下无法自动选中）', 'ok');
    }
  } catch (err) {
    toast(`无法打开资源管理器：${err.message}`, 'error', 5000);
  }
}

/* =====================================================================
 * 确认框
 * ===================================================================== */
let confirmHandler = null;

function openConfirm({ title, text, file, okLabel, okKind = 'danger', onOk }) {
  const modal = $('#confirm');
  $('[data-role="title"]', modal).textContent = title;
  $('[data-role="text"]', modal).textContent = text;
  $('[data-role="file"]', modal).textContent = file || '';
  const okBtn = $('[data-role="ok"]', modal);
  okBtn.textContent = okLabel || '确定';
  okBtn.className = `btn ${okKind}`;
  confirmHandler = onOk;
  modal.hidden = false;
  const close = () => { modal.hidden = true; confirmHandler = null; };
  $('[data-role="cancel"]', modal).onclick = close;
  $('[data-role="close"]', modal).onclick = close;
  okBtn.onclick = async () => {
    const handler = confirmHandler;
    close();
    if (handler) await handler();
  };
}

/* =====================================================================
 * 文件选择器
 * ===================================================================== */
const picker = {
  side: 'left',
  mode: 'file',
  dir: '',
  selected: null,
  resolve: null,
  node: $('#picker'),
  list: null,
};

function initPicker() {
  const node = picker.node;
  picker.list = $('[data-role="list"]', node);
  picker.pathInput = $('[data-role="path"]', node);
  picker.okBtn = $('[data-role="ok"]', node);
  picker.upBtn = $('[data-role="up"]', node);
  picker.hint = $('[data-role="hint"]', node);

  $('[data-role="close"]', node).onclick = () => pickerClose(null);
  $('[data-role="cancel"]', node).onclick = () => pickerClose(null);
  picker.okBtn.onclick = () => {
    if (picker.mode === 'folder') return pickerClose(picker.dir || null);
    if (picker.selected) return pickerClose(picker.selected);
    return null;
  };
  picker.upBtn.onclick = () => {
    if (picker.parent) loadDir(picker.parent);
  };
  picker.pathInput.addEventListener('keydown', (event) => {
    if (event.key === 'Enter') loadDir(picker.pathInput.value.trim());
  });
  $('[data-role="go"]', node).onclick = () => gotoPath(picker.pathInput.value.trim());
  $('[data-role="net"]', node).onclick = () => {
    picker.pathInput.value = '\\\\';
    picker.pathInput.focus();
    picker.pathInput.setSelectionRange(2, 2);
    picker.hint.textContent = '输入 \\\\服务器 可列出共享；输入 \\\\服务器\\共享\\目录 可直接浏览，粘贴完整文件路径则直接打开';
  };
  $('[data-role="sys"]', node).onclick = async () => {
    try {
      const res = await postJSON('/api/dialog/open', { dir: picker.dir, title: '选择视频' });
      if (res.path) pickerClose(res.path);
    } catch (err) {
      toast(`系统对话框不可用：${err.message}`, 'warn');
    }
  };
  node.addEventListener('click', (event) => {
    if (event.target === node) pickerClose(null);
  });
}

function openPicker(side, mode = 'file') {
  picker.side = side || 'left';
  picker.mode = mode;
  picker.selected = null;
  const label = picker.side === 'left' ? '左' : '右';
  $('[data-role="title"]', picker.node).textContent =
    mode === 'folder' ? '选择目标文件夹' : `选择${label}侧视频`;
  picker.hint.textContent =
    mode === 'folder' ? '双击进入文件夹，点击「确定」选择当前文件夹' : '双击文件夹进入，双击视频直接打开';
  picker.node.hidden = false;

  let startDir = '';
  try {
    const pane = panes[picker.side];
    startDir = (pane && pane.meta && pane.meta.dir) || localStorage.getItem(`vc.dir.${picker.side}`) || '';
  } catch (e) { /* ignore */ }

  renderDrives();
  return new Promise((resolve) => {
    picker.resolve = resolve;
    loadDir(startDir);
  });
}

function pickerClose(value) {
  picker.node.hidden = true;
  picker.selected = null;
  picker.okBtn.disabled = true;
  if (picker.resolve) {
    const resolve = picker.resolve;
    picker.resolve = null;
    resolve(value);
  }
}

async function renderDrives() {
  const box = $('[data-role="drives"]', picker.node);
  if (box.dataset.loaded === '1') return;
  try {
    const res = await api('/api/drives');
    box.innerHTML = '';
    for (const drive of res.drives) {
      const btn = el('button', 'drive-btn', drive.label);
      btn.onclick = () => loadDir(drive.path);
      box.appendChild(btn);
    }
    const netBtn = el('button', 'drive-btn', '网络');
    netBtn.title = '浏览 \\\\服务器\\共享';
    netBtn.onclick = () => {
      picker.pathInput.value = '\\\\';
      picker.pathInput.focus();
      picker.hint.textContent = '输入 \\\\服务器 回车可列出共享，例如 \\\\NAS';
    };
    box.appendChild(netBtn);
    box.dataset.loaded = '1';
  } catch (err) {
    console.warn(err);
  }
}

function normalizeWinPath(value) {
  // 把用户输入的各种网络路径形态统一成 \\server\share\…
  let v = (value || '').trim().replace(/^"+|"+$/g, '');
  if (/^[\\/][^\\/]/.test(v)) {
    // 单反斜杠/单斜杠开头的 server\share… → \\server\share…
    v = v.replace(/^[\\/]+/, '\\\\');
  }
  return v;
}

async function gotoPath(input) {
  const value = normalizeWinPath(input);
  if (!value) return;
  let stat;
  try {
    stat = await api(`/api/stat?path=${encodeURIComponent(value)}`);
  } catch (err) {
    toast(`无法访问：${err.message}`, 'error', 5000);
    return;
  }
  if (stat.type === 'file') {
    if (stat.video) {
      pickerClose(stat.path);
    } else {
      toast('这不是视频文件', 'warn');
    }
    return;
  }
  if (stat.type === 'dir' || stat.type === 'server') {
    loadDir(stat.path);
    return;
  }
  toast('路径不存在，或没有访问权限', 'error', 5000);
}

async function loadDir(dir) {
  let data;
  try {
    data = await api(`/api/list?dir=${encodeURIComponent(dir || '')}`);
  } catch (err) {
    toast(`打开目录失败：${err.message}`, 'error');
    return;
  }
  picker.dir = data.dir || '';
  picker.parent = data.parent;
  picker.pathInput.value = picker.dir;
  picker.upBtn.disabled = !picker.parent;
  if (data.hint) picker.hint.textContent = data.hint;
  picker.okBtn.disabled = picker.mode === 'file';
  if (picker.mode === 'folder') {
    picker.okBtn.disabled = !picker.dir;
    picker.okBtn.textContent = '选择此文件夹';
  } else {
    picker.okBtn.textContent = '确定';
  }

  const list = picker.list;
  list.innerHTML = '';
  if (!data.entries.length) {
    list.appendChild(el('div', 'picker-empty', '该目录下没有子文件夹或视频文件'));
    return;
  }
  for (const entry of data.entries) {
    const row = el('div', `picker-row ${entry.type}`);
    row.appendChild(el('span', 'ico', entry.type === 'dir' ? '📁' : entry.type === 'drive' ? '💽' : '🎬'));
    row.appendChild(el('span', 'nm', entry.name));
    row.appendChild(el('span', 'sz', entry.size_human || ''));
    row.appendChild(el('span', 'dt', entry.mtime || ''));
    row.title = entry.path;
    row.onclick = () => {
      if (entry.type === 'drive') {
        loadDir(entry.path);
        return;
      }
      if (entry.type === 'dir') {
        picker.selected = null;
        $$('.picker-row', list).forEach((n) => n.classList.remove('selected'));
        row.classList.add('selected');
        picker.selected = entry.path;
        picker.okBtn.disabled = picker.mode === 'file';
      } else {
        picker.selected = entry.path;
        $$('.picker-row', list).forEach((n) => n.classList.remove('selected'));
        row.classList.add('selected');
        picker.okBtn.disabled = false;
      }
    };
    row.ondblclick = () => {
      if (entry.type === 'file') {
        picker.selected = entry.path;
        pickerClose(entry.path);
      } else {
        loadDir(entry.path);
      }
    };
    list.appendChild(row);
  }
}

/* =====================================================================
 * 顶栏与快捷键
 * ===================================================================== */
function initTopbar() {
  $('#chk-link').onchange = (event) => { app.link = event.target.checked; };
  $('#chk-vol').onchange = (event) => { app.syncVolume = event.target.checked; };

  $('#btn-sync-play').onclick = () => {
    app.guardUntil = now() + 250;
    for (const side of ['left', 'right']) {
      const pane = panes[side];
      if (pane.meta) setPlaying(pane, true);
    }
  };
  $('#btn-sync-pause').onclick = () => {
    app.guardUntil = now() + 250;
    for (const side of ['left', 'right']) {
      const pane = panes[side];
      if (pane.meta) setPlaying(pane, false);
    }
  };
  $('#btn-align-left').onclick = () => {
    const left = panes.left;
    const right = panes.right;
    if (!left.meta || !right.meta) { toast('两侧都载入视频后才能对齐', 'warn'); return; }
    const time = paneTime(left);
    app.guardUntil = now() + 250;
    seekTo(right, time);
    toast(`右侧已对齐到 ${fmtClock(time)}`, 'ok');
  };
  $('#btn-rewind').onclick = () => {
    app.guardUntil = now() + 250;
    for (const side of ['left', 'right']) {
      const pane = panes[side];
      if (pane.meta) seekTo(pane, 0);
    }
  };
  $('#btn-swap').onclick = () => swapPanes();
  $('#btn-quit').onclick = () => {
    openConfirm({
      title: '退出工具',
      text: '确定关闭视频对比工具吗？本窗口随后会自动关闭。',
      file: '服务端会随之停止，正在播放的视频不受影响。',
      okLabel: '退出',
      onOk: async () => {
        try { await postJSON('/api/shutdown', {}); } catch (e) { /* ignore */ }
        setTimeout(() => window.close(), 300);
      },
    });
  };

  api('/api/env').then((res) => {
    const badge = $('#env-badge');
    if (res.has_ffprobe && res.has_ffmpeg) {
      badge.textContent = 'ffmpeg 已就绪';
      badge.title = `ffprobe: ${res.ffprobe}\nffmpeg: ${res.ffmpeg}`;
    } else if (res.has_ffprobe) {
      badge.textContent = '仅 ffprobe（无转码）';
      badge.title = res.ffprobe;
    } else {
      badge.textContent = '内置解析器（无 ffmpeg）';
      badge.title = '未找到 ffmpeg，元数据由内置解析器读取，无法转码';
    }
  }).catch(() => {
    $('#env-badge').textContent = '环境未知';
  });
}

function swapPanes() {
  const left = panes.left;
  const right = panes.right;
  if (!left.meta && !right.meta) { toast('两侧都还没有载入视频', 'warn'); return; }
  const leftInfo = left.meta ? { path: left.meta.path, time: paneTime(left) } : null;
  const rightInfo = right.meta ? { path: right.meta.path, time: paneTime(right) } : null;
  app.guardUntil = now() + 300;
  if (rightInfo) loadVideo('left', rightInfo.path, { startTime: rightInfo.time, autoplay: false });
  else clearPane(left);
  if (leftInfo) loadVideo('right', leftInfo.path, { startTime: leftInfo.time, autoplay: false });
  else clearPane(right);
  toast('已交换左右', 'ok');
}

function initShortcuts() {
  document.addEventListener('keydown', (event) => {
    if (event.target.matches('input, select, textarea')) return;
    if (!$('#picker').hidden || !$('#rename').hidden || !$('#confirm').hidden) return;

    if (event.code === 'Space') {
      event.preventDefault();
      const pane = panes.left.meta ? panes.left : panes.right;
      if (pane.meta) togglePlay(pane);
    } else if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
      event.preventDefault();
      const dir = event.key === 'ArrowLeft' ? -1 : 1;
      app.guardUntil = now() + 220;
      for (const side of ['left', 'right']) {
        const pane = panes[side];
        if (!pane.meta) continue;
        if (event.shiftKey) stepFrame(pane, dir);
        else seekTo(pane, paneTime(pane) + dir * 5);
      }
    }
  });
}

/* =====================================================================
 * 启动
 * ===================================================================== */
function initDropIntegration() {
  // ---- 上报两侧播放区矩形，供透明落点层（原生窗口）精确覆盖 ----
  let lastSig = '';
  const reportZones = async () => {
    try {
      const box = (pane) => {
        // 即使还没载入视频也要上报（空栏也要能拖进来）
        if (!pane || !pane.node) return null;
        const el = pane.video.parentElement;            // .stage 播放区
        if (!el) return null;
        const r = el.getBoundingClientRect();
        if (r.width < 40 || r.height < 40) return null;
        return { x: Math.round(r.left), y: Math.round(r.top),
                 w: Math.round(r.width), h: Math.round(r.height) };
      };
      const payload = {
        vw: window.innerWidth, vh: window.innerHeight,
        left: box(panes.left), right: box(panes.right),
      };
      const sig = JSON.stringify(payload);
      if (sig === lastSig) return;
      lastSig = sig;
      await postJSON('/api/drop-zones', payload);
    } catch (err) { /* 服务未就绪，下次再试 */ }
  };
  reportZones();
  setInterval(reportZones, 1200);
  window.addEventListener('resize', () => { lastSig = ''; reportZones(); });

  // ---- 轮询队列：拖放载入 / 播放区点击 ----
  const poll = async () => {
    try {
      const res = await api('/api/drop/poll');
      for (const item of res.items || []) {
        const side = item.side === 'right' ? 'right' : 'left';
        if (item.type === 'toggle') {
          const pane = panes[side];
          if (pane && pane.meta) togglePlay(pane);
          continue;
        }
        const name = String(item.path || '').split(/[\\/]/).pop();
        toast(`拖放载入：${name} → ${side === 'left' ? '左栏' : '右栏'}`, 'ok');
        await loadVideo(side, item.path, { autoplay: true });
      }
    } catch (err) { /* 忽略 */ }
  };
  poll();
  setInterval(poll, 1000);
}

function init() {
  createPane('left', $('#mount-left'));
  createPane('right', $('#mount-right'));
  initPicker();
  initTopbar();
  initDropIntegration();
  initShortcuts();

  // 心跳：后台据此判断界面是否还开着（exe 模式下关掉窗口会自动退出）
  const ping = () => fetch('/api/ping').catch(() => {});
  ping();
  setInterval(ping, 5000);

  // 支持通过 URL 参数直接载入
  const params = new URLSearchParams(location.search);
  const left = params.get('left');
  const right = params.get('right');
  if (left) loadVideo('left', left);
  if (right) loadVideo('right', right);
}

init();
