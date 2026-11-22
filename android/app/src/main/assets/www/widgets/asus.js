// Laptop Control (MyASUS): Display Refresh Rate (60/120 Hz), ASUS Fan Profile (Whisper / Standard / Performance),
// and Battery Health Charging Limit (60% / 80% / 100%).
import { W } from '../registry.js';
import { api, h, haptic, icon, toast } from '../main.js';

W.asus = {
  needs: ['tick'],
  create(box, { o }) {
    const titleText = (o.title || 'Laptop Control').trim();
    const statusSub = h('span', 'w-sub', '');

    // Row 1: Display Refresh Rate
    const hzBadge = h('span', 'as-val', '—');
    const hzBtns = h('div', 'as-seg');
    const hzRow = h('div', 'as-group',
      h('div', 'as-head', h('span', 'as-ico', icon('monitor')), h('span', 'as-lbl', 'Refresh Rate'), hzBadge),
      hzBtns);

    // Row 2: Fan Profile
    const fanBadge = h('span', 'as-val', '—');
    const fanBtns = h('div', 'as-seg');
    const fanRow = h('div', 'as-group',
      h('div', 'as-head', h('span', 'as-ico', icon('fan')), h('span', 'as-lbl', 'Fan Profile'), fanBadge),
      fanBtns);

    // Row 3: Battery Health Charging
    const batBadge = h('span', 'as-val', '—');
    const batBtns = h('div', 'as-seg');
    const batRow = h('div', 'as-group',
      h('div', 'as-head', h('span', 'as-ico', icon('battery')), h('span', 'as-lbl', 'Battery Health'), batBadge),
      batBtns);

    box.append(
      h('div', 'w-title', titleText, statusSub),
      h('div', 'as-body', hzRow, fanRow, batRow),
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
      const fan = data.fan || {};
      const bat = data.battery || {};

      // Display refresh rate
      const rates = Array.isArray(disp.supportedHz) && disp.supportedHz.length ? disp.supportedHz : [60, 120];
      statusSub.textContent = disp.width && disp.height ? `${disp.width}×${disp.height}` : '';
      hzBadge.textContent = disp.currentHz ? `${disp.currentHz} Hz` : '—';
      hzBtns.replaceChildren(...rates.map((hz) =>
        makeBtn(`${hz} Hz`, hz >= 120 ? 'Smooth' : 'Battery', disp.currentHz === hz, hz >= 120 ? 'c-hi' : 'c-lo',
          () => run(`/asus/refresh?hz=${hz}`, `Refresh rate → ${hz} Hz`))));

      // Fan profile
      const fanModes = [
        ['whisper', 'Whisper', 'Quiet', 'c-lo'],
        ['standard', 'Standard', 'Balanced', 'c-mid'],
        ['performance', 'Performance', 'Max cool', 'c-hot'],
      ];
      fanRow.classList.toggle('dimmed', fan.supported === false);
      const curFan = (fan.mode || 'standard').toLowerCase();
      fanBadge.textContent = fan.supported === false ? 'N/A' : curFan.charAt(0).toUpperCase() + curFan.slice(1);
      fanBtns.replaceChildren(...fanModes.map(([id, lbl, sub, cls]) =>
        makeBtn(lbl, sub, curFan === id, cls, () => run(`/asus/fan?mode=${id}`, `Fan Profile → ${lbl}`))));

      // Battery health charging
      const batModes = [
        [60, '60%', 'Max lifespan', 'c-lo'],
        [80, '80%', 'Balanced', 'c-mid'],
        [100, '100%', 'Full capacity', 'c-hi'],
      ];
      batRow.classList.toggle('dimmed', bat.supported === false);
      batBadge.textContent = bat.supported === false ? 'N/A' : `${bat.limit || 100}%`;
      batBtns.replaceChildren(...batModes.map(([lim, lbl, sub, cls]) =>
        makeBtn(lbl, sub, bat.limit === lim, cls, () => run(`/asus/battery?limit=${lim}`, `Battery limit → ${lim}%`))));
    };

    const load = async (force = false) => {
      if (busy) return;
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
