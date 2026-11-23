// Audio Output Switcher (Speaker / Headset / Bluetooth TWS) + Per-App Volume Mixer
import { W } from '../registry.js';
import { api, h, haptic, icon, toast } from '../main.js';

W.audiomixer = {
  needs: ['tick'],
  create(box, { o }) {
    const titleText = (o.title || 'Audio Mixer').trim();
    const sub = h('span', 'w-sub', '');
    const outRow = h('div', 'am-outputs');
    const appList = h('div', 'am-apps');

    box.append(
      h('div', 'w-title', titleText, sub),
      outRow,
      appList,
    );

    let data = null;
    let lastFetch = 0;
    let draggingKey = null;
    const timers = new Map();

    const devIcon = (kind) => {
      if (kind === 'bluetooth' || kind === 'headphones') return icon('headphones');
      if (kind === 'hdmi') return icon('monitor');
      return icon('volume-2');
    };

    const setAppAudio = async (key, vol, mute) => {
      const qs = new URLSearchParams({ key });
      if (vol != null) qs.set('volume', String(vol));
      if (mute != null) qs.set('mute', String(mute));
      try {
        const res = await api('/audio/app?' + qs.toString(), 'POST', 4000);
        if (res && res.state) { data = res.state; if (!draggingKey) render(); }
      } catch (_) {}
    };

    const switchOutput = async (dev) => {
      if (dev.isDefault) return;
      haptic();
      try {
        const res = await api('/audio/output?id=' + encodeURIComponent(dev.id), 'POST', 5000);
        if (res && res.state) data = res.state;
        toast(`Output → ${dev.name}`, 'ok', titleText, 3);
        render();
      } catch (e) {
        toast((e && e.message) || 'Gagal mengganti output audio', 'err', titleText, 4);
      }
    };

    const render = () => {
      if (!data) return;
      const outputs = Array.isArray(data.outputs) ? data.outputs : [];
      const apps = Array.isArray(data.apps) ? data.apps : [];
      const curOut = outputs.find((d) => d.isDefault);
      sub.textContent = curOut ? curOut.name : '';

      if (outputs.length) {
        outRow.replaceChildren(...outputs.map((d) => {
          const btn = h('button', 'am-out' + (d.isDefault ? ' on' : ''),
            h('span', 'am-out-ico', devIcon(d.kind)),
            h('span', 'am-out-name', d.name));
          btn.addEventListener('click', () => switchOutput(d));
          return btn;
        }));
      } else {
        outRow.replaceChildren(h('div', 'am-empty-sm', 'No audio outputs'));
      }

      if (!apps.length) {
        appList.replaceChildren(h('div', 'empty', 'Putar audio/video di laptop untuk mengatur volume tiap aplikasi'));
        return;
      }

      appList.replaceChildren(...apps.map((a) => {
        const valEl = h('span', 'am-val', a.muted ? 'MUTE' : `${a.volume}%`);
        const muteBtn = h('button', 'am-mute' + (a.muted ? ' on' : ''), icon(a.muted ? 'volume-x' : 'volume-2'));
        muteBtn.addEventListener('click', () => {
          haptic();
          setAppAudio(a.key, null, !a.muted);
        });

        const slider = h('input', 'am-slider');
        slider.type = 'range';
        slider.min = '0';
        slider.max = '100';
        slider.value = String(a.volume);
        slider.style.setProperty('--p', `${a.volume}%`);
        slider.addEventListener('pointerdown', () => { draggingKey = a.key; });
        slider.addEventListener('pointerup', () => {
          draggingKey = null;
          setAppAudio(a.key, Number(slider.value), null);
        });
        slider.addEventListener('pointercancel', () => { draggingKey = null; });
        slider.addEventListener('input', () => {
          const v = Number(slider.value);
          slider.style.setProperty('--p', `${v}%`);
          valEl.textContent = `${v}%`;
          clearTimeout(timers.get(a.key));
          timers.set(a.key, setTimeout(() => setAppAudio(a.key, v, null), 65));
        });

        return h('div', 'am-app' + (a.active ? ' live' : '') + (a.muted ? ' muted' : ''),
          h('div', 'am-app-top',
            h('span', 'am-dot'),
            h('b', 'am-name', a.name),
            valEl,
            muteBtn),
          slider);
      }));
    };

    const load = async (force = false) => {
      if (draggingKey) return;
      const now = Date.now();
      if (!force && now - lastFetch < 3500) return;
      lastFetch = now;
      try {
        data = await api('/audio/mixer', 'GET', 4000);
        render();
      } catch (_) {}
    };

    load(true);
    return {
      update() {
        load(false);
      },
    };
  },
};
