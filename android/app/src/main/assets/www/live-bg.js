// Living, audio-reactive ambient background for PhoneBox.
// - When idle: calm, drifting aurora orbs so the panel never feels flat/monotonous.
// - When music plays: smoothly shifts into a 3-color palette extracted from the album cover
//   and reacts in real time to PC audio FFT (bass beat pulses, mid glow, treble shimmer wave).
// - Pauses completely during Night Mode or when the app is backgrounded.
import { api, D, streaming } from './main.js';

const DEFAULT_PALETTE = [
  [38, 198, 218],  // cyan-teal
  [99, 102, 241],  // indigo
  [168, 85, 247],  // violet
];

function lerp(a, b, t) { return a + (b - a) * t; }

function hslShift(rgb, deg) {
  let r = rgb[0] / 255, g = rgb[1] / 255, b = rgb[2] / 255;
  const max = Math.max(r, g, b), min = Math.min(r, g, b), d = max - min;
  let h = 0;
  const l = (max + min) / 2;
  const s = d === 0 ? 0 : d / (1 - Math.abs(2 * l - 1));
  if (d !== 0) {
    if (max === r) h = ((g - b) / d) % 6;
    else if (max === g) h = (b - r) / d + 2;
    else h = (r - g) / d + 4;
    h *= 60;
    if (h < 0) h += 360;
  }
  h = (h + deg + 360) % 360;
  const s2 = Math.min(1, Math.max(s, 0.52));
  const l2 = Math.min(0.66, Math.max(l, 0.42));
  const c = (1 - Math.abs(2 * l2 - 1)) * s2;
  const x = c * (1 - Math.abs(((h / 60) % 2) - 1));
  const m = l2 - c / 2;
  let rr = 0, gg = 0, bb = 0;
  if (h < 60) { rr = c; gg = x; }
  else if (h < 120) { rr = x; gg = c; }
  else if (h < 180) { gg = c; bb = x; }
  else if (h < 240) { gg = x; bb = c; }
  else if (h < 300) { rr = x; bb = c; }
  else { rr = c; bb = x; }
  return [Math.round((rr + m) * 255), Math.round((gg + m) * 255), Math.round((bb + m) * 255)];
}

/** Extracts 3 distinct vibrant RGB colors from an album art image. */
export function extractPalette(img) {
  try {
    const c = document.createElement('canvas');
    c.width = c.height = 24;
    const g = c.getContext('2d');
    g.drawImage(img, 0, 0, 24, 24);
    const d = g.getImageData(0, 0, 24, 24).data;
    // 12 hue buckets (30 deg each) weighted by saturation & brightness
    const buckets = Array.from({ length: 12 }, () => ({ r: 0, g: 0, b: 0, w: 0 }));
    for (let i = 0; i < d.length; i += 4) {
      const r = d[i], gg = d[i + 1], b = d[i + 2];
      const mx = Math.max(r, gg, b), mn = Math.min(r, gg, b), diff = mx - mn;
      if (mx < 28 || diff < 18) continue;
      let h = 0;
      if (mx === r) h = ((gg - b) / diff) % 6;
      else if (mx === gg) h = (b - r) / diff + 2;
      else h = (r - gg) / diff + 4;
      h = (h * 60 + 360) % 360;
      const idx = Math.floor(h / 30) % 12;
      const sat = diff / (mx || 1);
      const weight = (0.3 + sat * 1.6) * (0.4 + (mx / 255) * 0.8);
      const bk = buckets[idx];
      bk.r += r * weight;
      bk.g += gg * weight;
      bk.b += b * weight;
      bk.w += weight;
    }
    const ranked = buckets
      .map((bk, idx) => ({ idx, w: bk.w, rgb: bk.w > 0 ? [bk.r / bk.w, bk.g / bk.w, bk.b / bk.w] : null }))
      .filter((x) => x.rgb)
      .sort((a, b) => b.w - a.w);
    if (!ranked.length) return null;

    const norm = (rgb) => {
      const k = 225 / (Math.max(rgb[0], rgb[1], rgb[2]) || 1);
      return rgb.map((v) => Math.round(Math.min(255, v * k)));
    };
    const primary = norm(ranked[0].rgb);
    const secItem = ranked.find((x) => Math.min(Math.abs(x.idx - ranked[0].idx), 12 - Math.abs(x.idx - ranked[0].idx)) >= 2);
    const secondary = secItem ? norm(secItem.rgb) : hslShift(primary, 38);
    const accItem = ranked.find((x) =>
      Math.min(Math.abs(x.idx - ranked[0].idx), 12 - Math.abs(x.idx - ranked[0].idx)) >= 3 &&
      (!secItem || Math.min(Math.abs(x.idx - secItem.idx), 12 - Math.abs(x.idx - secItem.idx)) >= 2));
    const accent = accItem ? norm(accItem.rgb) : hslShift(primary, -45);
    return [primary, secondary, accent];
  } catch (_) {
    return null;
  }
}

export function initLiveBackground() {
  const canvas = document.getElementById('liveBg');
  if (!canvas) return;
  const ctx = canvas.getContext('2d');
  if (!ctx) return;

  let W = 0, H = 0, raf = 0, paused = false;
  let lastT = performance.now();
  let phase = 0;

  let targetPalette = DEFAULT_PALETTE.map((c) => [...c]);
  const curPalette = DEFAULT_PALETTE.map((c) => [...c]);

  // Audio analysis state
  const waveBands = new Float32Array(18);
  const smoothBands = new Float32Array(18);
  let bass = 0, mid = 0, high = 0, energy = 0;
  let bassAvg = 0.08, beatPulse = 0, beatSpin = 0;
  let lastAudioAt = 0;
  let lastSpecKeep = 0;

  function parseCssAmbient() {
    const raw = getComputedStyle(document.documentElement).getPropertyValue('--ambient').trim();
    const parts = raw.split(',').map((s) => parseInt(s.trim(), 10));
    if (parts.length === 3 && parts.every((n) => Number.isFinite(n))) return parts;
    return DEFAULT_PALETTE[0];
  }

  function applyPalette(pal) {
    if (Array.isArray(pal) && pal.length >= 3) {
      targetPalette = [pal[0], pal[1], pal[2]].map((c) => [...c]);
    } else {
      const base = parseCssAmbient();
      targetPalette = [base, hslShift(base, 48), hslShift(base, -55)];
    }
  }

  window.addEventListener('phonebox-palette', (e) => applyPalette(e.detail));

  function ingestSpectrum(bands) {
    if (!Array.isArray(bands) || !bands.length) return;
    lastAudioAt = performance.now();
    const n = bands.length;
    let bSum = 0, bCnt = 0, mSum = 0, mCnt = 0, hSum = 0, hCnt = 0;
    for (let i = 0; i < n; i++) {
      const v = (bands[i] || 0) / 100;
      if (i < 8) { bSum += v * (1.25 - i * 0.04); bCnt++; }
      else if (i < 24) { mSum += v; mCnt++; }
      else { hSum += v; hCnt++; }
    }
    const bVal = bCnt ? Math.min(1, bSum / bCnt) : 0;
    const mVal = mCnt ? Math.min(1, mSum / mCnt) : 0;
    const hVal = hCnt ? Math.min(1, hSum / hCnt) : 0;

    // Adaptive beat onset detection on low frequencies
    const spike = bVal - bassAvg;
    bassAvg = bassAvg * 0.90 + bVal * 0.10;
    if (spike > 0.065 && bVal > 0.18) {
      beatPulse = Math.min(1, Math.max(beatPulse, spike * 3.1));
      beatSpin += spike * 1.4;
    }

    bass = Math.max(bVal, bass * 0.72);
    mid = Math.max(mVal, mid * 0.78);
    high = Math.max(hVal, high * 0.78);
    energy = Math.max(energy * 0.82, Math.min(1, bVal * 0.5 + mVal * 0.35 + hVal * 0.25));

    // Downsample 48 bands into 18 smooth wave points
    for (let i = 0; i < waveBands.length; i++) {
      const a = Math.floor(i * n / waveBands.length);
      const b = Math.max(a + 1, Math.floor((i + 1) * n / waveBands.length));
      let peak = 0;
      for (let k = a; k < b; k++) peak = Math.max(peak, (bands[k] || 0) / 100);
      waveBands[i] = peak;
    }
  }

  window.addEventListener('phonebox-spectrum', (e) => {
    if (e.detail && Array.isArray(e.detail.b)) ingestSpectrum(e.detail.b);
  });

  // Keep agent WASAPI audio spectrum alive while panel is active (not in night mode / paused)
  async function keepSpectrumAlive() {
    const isNight = document.body.classList.contains('night');
    const isAmoled = document.body.dataset.theme === 'amoled';
    if (!paused && !isNight && !isAmoled) {
      const mediaPlaying = !!D.state?.media?.playing;
      const stream = streaming();
      const interval = stream ? (mediaPlaying ? 2200 : 3800) : (mediaPlaying ? 65 : 1500);
      if (Date.now() - lastSpecKeep >= interval) {
        lastSpecKeep = Date.now();
        try {
          const r = await api('/spectrum?ambient=true', 'GET', 1500);
          if (!stream && r && Array.isArray(r.b)) ingestSpectrum(r.b);
        } catch (_) {}
      }
    }
    setTimeout(keepSpectrumAlive, streaming() ? 400 : 80);
  }
  setTimeout(keepSpectrumAlive, 700);

  function resize() {
    // Half-resolution canvas scaled smoothly by CSS: razor-smooth gradients, super low GPU/battery cost
    const scale = 0.5;
    W = Math.max(160, Math.round(window.innerWidth * scale));
    H = Math.max(90, Math.round(window.innerHeight * scale));
    canvas.width = W;
    canvas.height = H;
  }
  window.addEventListener('resize', resize);
  resize();

  function drawOrb(x, y, r, rgb, alpha) {
    if (alpha <= 0.004 || r <= 2) return;
    const grad = ctx.createRadialGradient(x, y, 0, x, y, r);
    grad.addColorStop(0, `rgba(${rgb[0] | 0},${rgb[1] | 0},${rgb[2] | 0},${alpha.toFixed(3)})`);
    grad.addColorStop(0.45, `rgba(${rgb[0] | 0},${rgb[1] | 0},${rgb[2] | 0},${(alpha * 0.42).toFixed(3)})`);
    grad.addColorStop(1, `rgba(${rgb[0] | 0},${rgb[1] | 0},${rgb[2] | 0},0)`);
    ctx.fillStyle = grad;
    ctx.beginPath();
    ctx.arc(x, y, r, 0, Math.PI * 2);
    ctx.fill();
  }

  function drawAudioWave(c0, c1, activeAmt) {
    if (activeAmt < 0.025) return;
    const pts = smoothBands.length;
    const baseY = H * 0.96;
    const maxH = H * (0.14 + beatPulse * 0.08);

    const drawRibbon = (mirrorShift, heightMul, alpha, rgbA, rgbB) => {
      const grad = ctx.createLinearGradient(0, baseY - maxH, W, baseY);
      grad.addColorStop(0, `rgba(${rgbA[0] | 0},${rgbA[1] | 0},${rgbA[2] | 0},${alpha.toFixed(3)})`);
      grad.addColorStop(0.5, `rgba(${rgbB[0] | 0},${rgbB[1] | 0},${rgbB[2] | 0},${(alpha * 1.15).toFixed(3)})`);
      grad.addColorStop(1, `rgba(${rgbA[0] | 0},${rgbA[1] | 0},${rgbA[2] | 0},${alpha.toFixed(3)})`);

      ctx.beginPath();
      ctx.moveTo(0, H);
      for (let i = 0; i < pts; i++) {
        const idx = mirrorShift ? (pts - 1 - i) : i;
        const x = (i / (pts - 1)) * W;
        const wave = Math.sin(phase * 1.6 + i * 0.55 + (mirrorShift ? 1.8 : 0)) * 0.08;
        const amp = Math.max(0, smoothBands[idx] + wave * activeAmt) * maxH * heightMul * activeAmt;
        const y = baseY - amp;
        if (i === 0) ctx.lineTo(x, y);
        else {
          const px = ((i - 1) / (pts - 1)) * W;
          const prevIdx = mirrorShift ? (pts - i) : (i - 1);
          const pWave = Math.sin(phase * 1.6 + (i - 1) * 0.55 + (mirrorShift ? 1.8 : 0)) * 0.08;
          const pAmp = Math.max(0, smoothBands[prevIdx] + pWave * activeAmt) * maxH * heightMul * activeAmt;
          const py = baseY - pAmp;
          const mx = (px + x) * 0.5;
          ctx.quadraticCurveTo(px, py, mx, (py + y) * 0.5);
        }
      }
      ctx.lineTo(W, baseY - smoothBands[mirrorShift ? 0 : pts - 1] * maxH * heightMul * activeAmt);
      ctx.lineTo(W, H);
      ctx.closePath();
      ctx.fillStyle = grad;
      ctx.fill();
    };

    drawRibbon(false, 1.0, 0.22 * activeAmt, c0, c1);
    drawRibbon(true, 0.68, 0.16 * activeAmt, c1, c0);
  }

  function frame(now) {
    raf = 0;
    if (paused) return;
    if (document.body.classList.contains('night') || document.body.dataset.theme === 'amoled') {
      ctx.clearRect(0, 0, W, H);
      raf = requestAnimationFrame(frame);
      return;
    }

    const dt = Math.min(0.08, Math.max(0.004, (now - lastT) / 1000));
    lastT = now;

    // Decay audio metrics smoothly when silent or between frames
    const audioFresh = now - lastAudioAt < 700;
    if (!audioFresh) {
      bass *= Math.exp(-dt * 4);
      mid *= Math.exp(-dt * 4);
      high *= Math.exp(-dt * 4);
      energy *= Math.exp(-dt * 3);
      for (let i = 0; i < waveBands.length; i++) waveBands[i] *= Math.exp(-dt * 5);
    }
    beatPulse *= Math.exp(-dt * 5.2);
    for (let i = 0; i < smoothBands.length; i++) {
      const target = waveBands[i];
      const rate = target > smoothBands[i] ? 18 : 7.5;
      smoothBands[i] += (target - smoothBands[i]) * Math.min(1, dt * rate);
    }

    // Smoothly cross-fade palette toward current song cover / theme
    const colLerp = Math.min(1, dt * 1.9);
    for (let i = 0; i < 3; i++) {
      for (let k = 0; k < 3; k++) {
        curPalette[i][k] = lerp(curPalette[i][k], targetPalette[i][k], colLerp);
      }
    }

    // Advance organic drift phase (faster when music is energetic!)
    const speed = 0.22 + energy * 0.95 + beatPulse * 0.65;
    phase += dt * speed;
    const hueWobble = Math.sin(phase * 0.35 + beatSpin * 0.6) * (10 + energy * 18);
    const c0 = hslShift(curPalette[0], hueWobble * 0.45);
    const c1 = hslShift(curPalette[1], -hueWobble * 0.65);
    const c2 = hslShift(curPalette[2], hueWobble * 0.85);

    ctx.globalCompositeOperation = 'source-over';
    ctx.clearRect(0, 0, W, H);
    const theme = document.body.dataset.theme || 'default';
    const minDim = Math.min(W, H);
    const musicActive = Math.min(1, energy * 2.2);

    if (theme === 'nothing') {
      // Nothing OS Glyph Dot-Matrix Canvas: pure OLED black, subtle breathing Glyph rings & LED dot EQ
      ctx.fillStyle = '#050505';
      ctx.fillRect(0, 0, W, H);

      const cx = W * 0.72 + Math.sin(phase * 0.4) * W * 0.04;
      const cy = H * 0.42 + Math.cos(phase * 0.35) * H * 0.04;
      const baseR = minDim * (0.28 + bass * 0.10 + beatPulse * 0.14);

      // Subtle monochrome Glyph arc + pulsing Nothing Red outer dot ring
      ctx.strokeStyle = `rgba(255,255,255,${(0.08 + mid * 0.14 + beatPulse * 0.12).toFixed(3)})`;
      ctx.lineWidth = Math.max(1.5, minDim * 0.006);
      ctx.setLineDash([4, 6]);
      ctx.beginPath();
      ctx.arc(cx, cy, baseR, phase * 0.3, phase * 0.3 + Math.PI * 1.35);
      ctx.stroke();

      ctx.strokeStyle = `rgba(215,25,33,${(0.14 + bass * 0.25 + beatPulse * 0.35).toFixed(3)})`;
      ctx.lineWidth = Math.max(1.5, minDim * 0.005);
      ctx.setLineDash([2, 8]);
      ctx.beginPath();
      ctx.arc(cx, cy, baseR * 1.24, -phase * 0.45, -phase * 0.45 + Math.PI * 0.9);
      ctx.stroke();
      ctx.setLineDash([]);

      // Soft Nothing Red ambient LED glow on left & beat flash
      drawOrb(W * 0.15, H * 0.28, minDim * (0.36 + beatPulse * 0.2), [215, 25, 33], 0.07 + beatPulse * 0.14);
      drawOrb(cx, cy, minDim * (0.42 + bass * 0.18), [255, 255, 255], 0.045 + mid * 0.06);

      // Dot-matrix LED audio bars along bottom when music is playing
      if (musicActive > 0.03) {
        const cols = smoothBands.length;
        const stepX = W / cols;
        const dotStep = Math.max(5, Math.round(H * 0.032));
        const maxDots = 7;
        for (let i = 0; i < cols; i++) {
          const lit = Math.round(Math.min(1, smoothBands[i] * 1.25) * maxDots * musicActive);
          const x = (i + 0.5) * stepX;
          for (let d = 0; d < lit; d++) {
            const y = H - 8 - d * dotStep;
            const isPeak = d === lit - 1 && lit >= 4;
            ctx.fillStyle = isPeak
              ? `rgba(215,25,33,${(0.65 * musicActive).toFixed(3)})`
              : `rgba(245,245,245,${((0.18 + (d / maxDots) * 0.32) * musicActive).toFixed(3)})`;
            ctx.beginPath();
            ctx.arc(x, y, Math.max(1.4, dotStep * 0.28), 0, Math.PI * 2);
            ctx.fill();
          }
        }
      }
      raf = requestAnimationFrame(frame);
      return;
    }

    if (theme === 'nothing-light') {
      // Nothing OS Light Paper Canvas: matte paper white with subtle charcoal Glyph arcs & Nothing Red LEDs
      ctx.fillStyle = '#efefe9';
      ctx.fillRect(0, 0, W, H);

      const cx = W * 0.72 + Math.sin(phase * 0.4) * W * 0.04;
      const cy = H * 0.42 + Math.cos(phase * 0.35) * H * 0.04;
      const baseR = minDim * (0.28 + bass * 0.10 + beatPulse * 0.14);

      ctx.strokeStyle = `rgba(18,18,18,${(0.10 + mid * 0.14).toFixed(3)})`;
      ctx.lineWidth = Math.max(1.5, minDim * 0.006);
      ctx.setLineDash([4, 6]);
      ctx.beginPath();
      ctx.arc(cx, cy, baseR, phase * 0.3, phase * 0.3 + Math.PI * 1.35);
      ctx.stroke();

      ctx.strokeStyle = `rgba(215,25,33,${(0.18 + bass * 0.25 + beatPulse * 0.35).toFixed(3)})`;
      ctx.lineWidth = Math.max(1.5, minDim * 0.005);
      ctx.setLineDash([2, 8]);
      ctx.beginPath();
      ctx.arc(cx, cy, baseR * 1.24, -phase * 0.45, -phase * 0.45 + Math.PI * 0.9);
      ctx.stroke();
      ctx.setLineDash([]);

      if (musicActive > 0.03) {
        const cols = smoothBands.length;
        const stepX = W / cols;
        const dotStep = Math.max(5, Math.round(H * 0.032));
        const maxDots = 7;
        for (let i = 0; i < cols; i++) {
          const lit = Math.round(Math.min(1, smoothBands[i] * 1.25) * maxDots * musicActive);
          const x = (i + 0.5) * stepX;
          for (let d = 0; d < lit; d++) {
            const y = H - 8 - d * dotStep;
            const isPeak = d === lit - 1 && lit >= 4;
            ctx.fillStyle = isPeak
              ? `rgba(215,25,33,${(0.75 * musicActive).toFixed(3)})`
              : `rgba(18,18,18,${((0.18 + (d / maxDots) * 0.30) * musicActive).toFixed(3)})`;
            ctx.beginPath();
            ctx.arc(x, y, Math.max(1.4, dotStep * 0.28), 0, Math.PI * 2);
            ctx.fill();
          }
        }
      }
      raf = requestAnimationFrame(frame);
      return;
    }

    if (theme === 'retro') {
      // Classic Phosphor CRT Green Terminal Grid + Oscilloscope
      ctx.fillStyle = '#030a04';
      ctx.fillRect(0, 0, W, H);
      const step = Math.max(16, Math.round(minDim * 0.09));
      ctx.strokeStyle = 'rgba(51, 255, 102, 0.07)';
      ctx.lineWidth = 1;
      ctx.beginPath();
      for (let x = 0; x <= W; x += step) { ctx.moveTo(x, 0); ctx.lineTo(x, H); }
      for (let y = 0; y <= H; y += step) { ctx.moveTo(0, y); ctx.lineTo(W, y); }
      ctx.stroke();

      ctx.globalCompositeOperation = 'lighter';
      drawOrb(W * 0.5, H * 0.5, minDim * (0.75 + beatPulse * 0.2), [51, 255, 102], 0.08 + bass * 0.08 + beatPulse * 0.10);
      if (musicActive > 0.03) {
        const pts = smoothBands.length;
        ctx.beginPath();
        for (let i = 0; i < pts; i++) {
          const x = (i / (pts - 1)) * W;
          const y = H * 0.90 - smoothBands[i] * H * 0.22 * musicActive;
          if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
        }
        ctx.strokeStyle = `rgba(51, 255, 102, ${(0.85 * musicActive).toFixed(3)})`;
        ctx.lineWidth = 2;
        ctx.stroke();
      }
      raf = requestAnimationFrame(frame);
      return;
    }

    if (theme === 'cyberpunk') {
      // Night City Neon HUD Grid + Electric Cyan/Magenta/Yellow Plasma + Laser FFT
      const bgGrad = ctx.createLinearGradient(0, 0, W, H);
      bgGrad.addColorStop(0, '#05040f');
      bgGrad.addColorStop(0.55, '#09061d');
      bgGrad.addColorStop(1, '#12051d');
      ctx.fillStyle = bgGrad;
      ctx.fillRect(0, 0, W, H);

      // Subtle glowing cyber HUD grid
      const gridStep = Math.max(18, Math.round(minDim * 0.095));
      ctx.strokeStyle = 'rgba(0, 240, 255, 0.075)';
      ctx.lineWidth = 1;
      ctx.beginPath();
      const scrollY = (phase * 14) % gridStep;
      for (let x = 0; x <= W; x += gridStep) { ctx.moveTo(x, 0); ctx.lineTo(x, H); }
      for (let y = scrollY; y <= H; y += gridStep) { ctx.moveTo(0, y); ctx.lineTo(W, y); }
      ctx.stroke();

      ctx.globalCompositeOperation = 'lighter';
      drawOrb(W * (0.78 + Math.sin(phase * 0.7) * 0.08), H * 0.28, minDim * (0.64 + bass * 0.3 + beatPulse * 0.35), [0, 240, 255], 0.20 + bass * 0.18 + beatPulse * 0.20);
      drawOrb(W * (0.18 + Math.cos(phase * 0.55) * 0.08), H * 0.72, minDim * (0.60 + mid * 0.28), [255, 42, 109], 0.18 + mid * 0.18 + beatPulse * 0.14);
      drawOrb(W * (0.48 + Math.sin(phase * 0.45) * 0.16), H * 0.15, minDim * (0.42 + high * 0.22 + beatPulse * 0.24), [252, 238, 10], 0.09 + beatPulse * 0.16);

      if (musicActive > 0.03) {
        // Neon laser FFT waveform
        const pts = smoothBands.length;
        ctx.beginPath();
        for (let i = 0; i < pts; i++) {
          const x = (i / (pts - 1)) * W;
          const y = H * 0.93 - smoothBands[i] * H * (0.20 + beatPulse * 0.10) * musicActive;
          if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
        }
        ctx.strokeStyle = `rgba(252,238,10,${(0.75 * musicActive).toFixed(3)})`;
        ctx.lineWidth = 2;
        ctx.stroke();
        drawAudioWave([0, 240, 255], [255, 42, 109], musicActive);
      }
      raf = requestAnimationFrame(frame);
      return;
    }

    if (theme === 'macglass') {
      // macOS Sonoma / Sequoia Luminous Fluid Mesh Wallpaper (built for heavy frosted Liquid Glass refraction)
      const bgGrad = ctx.createLinearGradient(0, 0, W, H);
      bgGrad.addColorStop(0, '#0c1836');
      bgGrad.addColorStop(0.5, '#131b3a');
      bgGrad.addColorStop(1, '#1a1230');
      ctx.fillStyle = bgGrad;
      ctx.fillRect(0, 0, W, H);

      const useArt = D.state?.media?.playing && energy > 0.03;
      const m0 = useArt ? c0 : [56, 189, 248];   // Apple Sky Cyan
      const m1 = useArt ? c1 : [129, 140, 248];  // Sonoma Iris Indigo
      const m2 = useArt ? c2 : [244, 114, 182];  // Sequoia Rose Pink
      const m3 = useArt ? hslShift(c0, 80) : [52, 211, 153]; // Aurora Mint

      ctx.globalCompositeOperation = 'lighter';
      drawOrb(W * (0.18 + Math.sin(phase * 0.42) * 0.12), H * (0.26 + Math.cos(phase * 0.36) * 0.14), minDim * (0.85 + bass * 0.25), m0, 0.34 + bass * 0.14);
      drawOrb(W * (0.80 + Math.cos(phase * 0.38) * 0.11), H * (0.30 + Math.sin(phase * 0.44) * 0.14), minDim * (0.88 + mid * 0.24), m1, 0.32 + mid * 0.14);
      drawOrb(W * (0.56 + Math.sin(phase * 0.34 + 1.6) * 0.18), H * (0.82 + Math.cos(phase * 0.40) * 0.10), minDim * (0.78 + high * 0.22), m2, 0.28 + high * 0.12 + beatPulse * 0.10);
      drawOrb(W * (0.30 + Math.cos(phase * 0.30 + 2.2) * 0.15), H * (0.76 + Math.sin(phase * 0.32) * 0.10), minDim * 0.65, m3, 0.18 + beatPulse * 0.08);
      drawAudioWave(m0, m2, musicActive * 0.85);
      raf = requestAnimationFrame(frame);
      return;
    }

    if (theme === 'industrial') {
      // Industrial / Teenage Engineering Machined Gunmetal Chassis & Safety-Orange Oscilloscope
      const bgGrad = ctx.createLinearGradient(0, 0, 0, H);
      bgGrad.addColorStop(0, '#191b20');
      bgGrad.addColorStop(1, '#131519');
      ctx.fillStyle = bgGrad;
      ctx.fillRect(0, 0, W, H);

      // Fine engineering blueprint grid + tick marks
      const step = Math.max(16, Math.round(minDim * 0.085));
      ctx.strokeStyle = 'rgba(255, 255, 255, 0.035)';
      ctx.lineWidth = 1;
      ctx.beginPath();
      for (let x = 0; x <= W; x += step) { ctx.moveTo(x, 0); ctx.lineTo(x, H); }
      for (let y = 0; y <= H; y += step) { ctx.moveTo(0, y); ctx.lineTo(W, y); }
      ctx.stroke();

      // Subtle warm amber indicator glow
      ctx.globalCompositeOperation = 'lighter';
      drawOrb(W * 0.78, H * 0.24, minDim * (0.45 + beatPulse * 0.22), [255, 90, 31], 0.07 + bass * 0.08 + beatPulse * 0.10);
      drawOrb(W * 0.22, H * 0.78, minDim * 0.40, [245, 158, 11], 0.05 + mid * 0.06);

      // Precision amber/orange oscilloscope trace along bottom when audio plays
      if (musicActive > 0.03) {
        const pts = smoothBands.length;
        ctx.beginPath();
        for (let i = 0; i < pts; i++) {
          const x = (i / (pts - 1)) * W;
          const osc = Math.sin(phase * 3.0 + i * 0.9) * 3;
          const y = H * 0.92 - smoothBands[i] * H * 0.16 * musicActive + osc * musicActive;
          if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
        }
        ctx.strokeStyle = `rgba(255, 90, 31, ${(0.72 * musicActive).toFixed(3)})`;
        ctx.lineWidth = 1.8;
        ctx.stroke();
      }
      raf = requestAnimationFrame(frame);
      return;
    }

    // Subtle rich dark wash (Default / Midnight / Warm / Graphite)
    const bgGrad = ctx.createLinearGradient(0, 0, W, H);
    bgGrad.addColorStop(0, '#05070c');
    bgGrad.addColorStop(1, '#070a12');
    ctx.fillStyle = bgGrad;
    ctx.fillRect(0, 0, W, H);

    // Additive aurora / beat-reactive nebula orbs
    ctx.globalCompositeOperation = 'lighter';

    // Orb 1: Primary album color behind media card (right-top), pumps with bass & beats
    const o1x = W * (0.74 + Math.sin(phase * 0.72) * 0.11);
    const o1y = H * (0.32 + Math.cos(phase * 0.58) * 0.14);
    const o1r = minDim * (0.68 + bass * 0.34 + beatPulse * 0.36);
    const o1a = 0.20 + bass * 0.20 + beatPulse * 0.22;
    drawOrb(o1x, o1y, o1r, c0, o1a);

    // Orb 2: Secondary color behind clock & weather (left), blooms with vocals/mids
    const o2x = W * (0.20 + Math.cos(phase * 0.52 + 1.1) * 0.12);
    const o2y = H * (0.44 + Math.sin(phase * 0.64 + 0.5) * 0.18);
    const o2r = minDim * (0.62 + mid * 0.32 + beatPulse * 0.18);
    const o2a = 0.15 + mid * 0.19 + beatPulse * 0.10;
    drawOrb(o2x, o2y, o2r, c1, o2a);

    // Orb 3: Accent color gliding across bottom/center, shimmers with treble & energy
    const o3x = W * (0.50 + Math.sin(phase * 0.46 + 2.4) * 0.22);
    const o3y = H * (0.86 + Math.cos(phase * 0.78) * 0.10);
    const o3r = minDim * (0.56 + high * 0.32 + beatPulse * 0.22);
    const o3a = 0.13 + high * 0.18 + beatPulse * 0.14;
    drawOrb(o3x, o3y, o3r, c2, o3a);

    // Orb 4: Crisp rhythmic beat flash halo when kick/bass hits
    if (beatPulse > 0.08) {
      const bx = W * (0.62 + Math.cos(beatSpin) * 0.16);
      const by = H * (0.46 + Math.sin(beatSpin) * 0.14);
      drawOrb(bx, by, minDim * (0.32 + beatPulse * 0.45), c0, beatPulse * 0.24);
    }

    // Bottom liquid FFT soundwave ribbon when music is playing
    drawAudioWave(c0, c1, musicActive);

    raf = requestAnimationFrame(frame);
  }

  window.addEventListener('phonebox-app-pause', (e) => {
    paused = !!e.detail;
    if (!paused && !raf) {
      lastT = performance.now();
      raf = requestAnimationFrame(frame);
    }
  });

  applyPalette(null);
  raf = requestAnimationFrame(frame);
}
