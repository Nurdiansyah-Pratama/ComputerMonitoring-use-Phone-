// Laptop Control (MyASUS):
// - Display Refresh Rate (Fixed 60 / 120 Hz + optional opt-in Adaptive toggle)
// - Laptop Screen Brightness slider (0-100%)
// - Keyboard Backlight (Off / Low / Med / High)
// - ASUS Fan Profile (Whisper / Standard / Performance)
// - Battery Health Charging Limit (60% / 80% / 100%)
import { W } from '../registry.js';
import { api, h, haptic, icon, toast } from '../main.js';

W.asus = {
  needs: ['tick'],
  create(box, { o }) {
    const titleText = (o.title || 'MyASUS Control').trim();
    const statusSub = h('span', 'w-sub', '');

    // Row 1: Display Refresh Rate (+ opt-in Adaptive button, OFF by default)
    const hzBadge = h('span', 'as-val', '—');
    const hzBtns = h('div', 'as-seg');
    const hzRow = h('div', 'as-group',
      h('div', 'as-head', h('span', 'as-ico', icon('monitor')), h('span', 'as-lbl', 'Refresh Rate'), hzBadge),
      hzBtns);

    // Row 2: Screen Brightness Slider
    const brBadge = h('span', 'as-val', '—');
    const brSlider = h('input', 'as-range');
    brSlider.type = 'range';
    brSlider.min = '0';
    brSlider.max = '100';
    brSlider.value = '50';
    let brDragging = false;
    let brTimer = 0;
    const sendBrightness = async (val) => {
      try {
        const res = await api(`/asus/brightness?level=${val}`, 'POST', 4000);
        if (res && res.state) data = res.state;
      } catch (_) {}
    };
    brSlider.addEventListener('pointerdown', () => { brDragging = true; });
    brSlider.addEventListener('pointerup', () => { brDragging = false; sendBrightness(Number(brSlider.value)); });
    brSlider.addEventListener('pointercancel', () => { brDragging = false; });
    brSlider.addEventListener('input', () => {
      const v = Number(brSlider.value);
      brBadge.textContent = `${v}%`;
      brSlider.style.setProperty('--p', `${v}%`);
      clearTimeout(brTimer);
      brTimer = setTimeout(() => sendBrightness(v), 70);
    });
    const brRow = h('div', 'as-group as-compact',
      h('div', 'as-head', h('span', 'as-ico', icon('sun')), h('span', 'as-lbl', 'Brightness'), brBadge),
      h('div', 'as-slider-row', brSlider));

    // Row 3: Keyboard Backlight (0..3)
    const kbBadge = h('span', 'as-val', '—');
    const kbBtns = h('div', 'as-seg');
    const kbRow = h('div', 'as-group',
      h('div', 'as-head', h('span', 'as-ico', icon('keyboard')), h('span', 'as-lbl', 'Keyboard LED'), kbBadge),
      kbBtns);

    // Row 4: Fan Profile
    const fanBadge = h('span', 'as-val', '—');
    const fanBtns = h('div', 'as-seg');
    const fanRow = h('div', 'as-group',
      h('div', 'as-head', h('span', 'as-ico', icon('fan')), h('span', 'as-lbl', 'Fan Profile'), fanBadge),
      fanBtns);

    // Row 5: Battery Health Charging
    const batBadge = h('span', 'as-val', '—');
    const batBtns = h('div', 'as-seg');
    const batRow = h('div', 'as-group',
      h('div', 'as-head', h('span', 'as-ico', icon('battery')), h('span', 'as-lbl', 'Battery Health'), batBadge),
      batBtns);

    box.append(
      h('div', 'w-title', titleText, statusSub),
      h('div', 'as-body as-grid2',
        h('div', 'as-col', hzRow, brRow, kbRow),
        h('div', 'as-col', fanRow, batRow)),
    );

    let data = null;
    let busy = false;
    let lastFetch = 0;

    const makeBtn = (label, sub, active, cls, onClick) => {
      const b = h('button', 'as-btn' + (active ? ' on' : '') + (cls ? ' ' + cls : ''),
        h('b', null, label),
        sub ? h('small', null, sub) : null);
      b.disabled = busy;
      b.addEventListener('click', onClick);
      return b;
    };

    const run = async (path, doneMsg) => {
      if (busy) return;
      haptic();
      busy = true;
      render();
      try {
        const res = await api(path, 'POST', 6000);
        if (res && res.state) data = res.state;
        if (doneMsg) toast(doneMsg, 'ok', titleText, 3);
      } catch (e) {
        toast((e && e.message) || 'Gagal mengubah pengaturan laptop', 'err', titleText, 4);
      } finally {
        busy = false;
        render();
      }
    };

    const render = () => {
      if (!data) return;
      const disp = data.display || {};
      const br = data.brightness || {};
      const kb = data.keyboard || {};
      const fan = data.fan || {};
      const bat = data.battery || {};

      // Display refresh rate (+ manual opt-in Adaptive button)
      const rates = Array.isArray(disp.supportedHz) && disp.supportedHz.length ? disp.supportedHz : [60, 120];
      const adaptiveOn = disp.adaptive === true;
      statusSub.textContent = disp.width && disp.height
        ? `${disp.width}×${disp.height}${disp.onAcPower === false ? ' · BAT' : ' · AC'}`
        : '';
      hzBadge.textContent = disp.currentHz ? `${disp.currentHz} Hz${adaptiveOn ? ' · Auto' : ''}` : '—';
      const hzItems = rates.map((hz) =>
        makeBtn(`${hz} Hz`, hz >= 120 ? 'Smooth' : 'Battery', !adaptiveOn && disp.currentHz === hz, hz >= 120 ? 'c-hi' : 'c-lo',
          () => run(`/asus/refresh?hz=${hz}`, `Refresh rate → ${hz} Hz`)));
      if (rates.length >= 2) {
        hzItems.push(makeBtn('Auto', adaptiveOn ? 'Active' : 'Opt-in', adaptiveOn, 'c-mid',
          () => run(`/asus/adaptive?enabled=${!adaptiveOn}`, !adaptiveOn ? 'Adaptive Refresh ON' : 'Adaptive Refresh OFF')));
      }
      hzBtns.replaceChildren(...hzItems);

      // Screen brightness
      brRow.classList.toggle('dimmed', br.supported === false);
      if (br.supported !== false && !brDragging) {
        const lv = Math.max(0, Math.min(100, Number(br.level) || 0));
        brSlider.value = String(lv);
        brSlider.style.setProperty('--p', `${lv}%`);
        brBadge.textContent = `${lv}%`;
      } else if (br.supported === false) {
        brBadge.textContent = 'N/A';
      }

      // Keyboard backlight (0-3)
      kbRow.classList.toggle('dimmed', kb.supported === false);
      const kbLabels = ['Off', 'Low', 'Med', 'Max'];
      const curKb = Math.max(0, Math.min(3, Number(kb.level) || 0));
      kbBadge.textContent = kb.supported === false ? 'N/A' : kbLabels[curKb];
      kbBtns.replaceChildren(...kbLabels.map((lbl, lv) =>
        makeBtn(lbl, null, kb.supported !== false && curKb === lv, lv === 0 ? 'c-lo' : lv === 3 ? 'c-hot' : 'c-hi',
          () => run(`/asus/keyboard?level=${lv}`, `Keyboard LED → ${lbl}`))));

      // Fan profile
      const fanModes = [
        ['whisper', 'Whisper', 'Quiet', 'c-lo'],
        ['standard', 'Standard', 'Balanced', 'c-mid'],
        ['performance', 'Perf', 'Max cool', 'c-hot'],
      ];
      fanRow.classList.toggle('dimmed', fan.supported === false);
      const curFan = (fan.mode || 'standard').toLowerCase();
      fanBadge.textContent = fan.supported === false ? 'N/A' : curFan.charAt(0).toUpperCase() + curFan.slice(1);
      fanBtns.replaceChildren(...fanModes.map(([id, lbl, sub, cls]) =>
        makeBtn(lbl, sub, curFan === id, cls, () => run(`/asus/fan?mode=${id}`, `Fan Profile → ${lbl}`))));

      // Battery health charging
      const batModes = [
        [60, '60%', 'Lifespan', 'c-lo'],
        [80, '80%', 'Balanced', 'c-mid'],
        [100, '100%', 'Full', 'c-hi'],
      ];
      batRow.classList.toggle('dimmed', bat.supported === false);
      batBadge.textContent = bat.supported === false ? 'N/A' : `${bat.limit || 100}%`;
      batBtns.replaceChildren(...batModes.map(([lim, lbl, sub, cls]) =>
        makeBtn(lbl, sub, bat.limit === lim, cls, () => run(`/asus/battery?limit=${lim}`, `Battery limit → ${lim}%`))));
    };

    const load = async (force = false) => {
      if (busy || brDragging) return;
      const now = Date.now();
      if (!force && now - lastFetch < 8000) return;
      lastFetch = now;
      try {
        data = await api('/asus', 'GET', 4000);
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
