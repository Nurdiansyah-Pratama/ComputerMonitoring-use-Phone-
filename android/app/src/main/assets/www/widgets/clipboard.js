// Two-Way Clipboard & Quick Send (Phone <-> PC over Wi-Fi or USB)
import { W } from '../registry.js';
import { api, bridge, h, haptic, icon, toast } from '../main.js';

W.clipboard = {
  needs: ['tick'],
  create(box, { o }) {
    const titleText = (o.title || 'Clipboard & Quick Send').trim();
    const sub = h('span', 'w-sub', 'PC ↔ HP');

    const pcText = h('div', 'cb-pc-text', '—');
    const copyBtn = h('button', 'cb-btn', icon('clipboard'), 'Copy ke HP');
    const refreshBtn = h('button', 'cb-btn icon-only', icon('refresh-cw'));
    refreshBtn.setAttribute('aria-label', 'Refresh');

    const input = h('input', 'cb-input');
    input.type = 'text';
    input.placeholder = 'Ketik / paste teks atau link ke laptop…';
    input.autocomplete = 'off';

    const sendBtn = h('button', 'cb-btn primary', 'Kirim PC');
    const openBtn = h('button', 'cb-btn', 'Buka URL');
    const histEl = h('div', 'cb-hist');

    box.append(
      h('div', 'w-title', titleText, sub),
      h('div', 'cb-box',
        h('div', 'cb-pc-row',
          h('div', 'cb-pc-body', h('small', 'cb-lbl', 'CLIPBOARD LAPTOP'), pcText),
          h('div', 'cb-pc-acts', copyBtn, refreshBtn)),
        h('div', 'cb-send-row', input, sendBtn, openBtn),
        histEl),
    );

    let currentText = '';
    let history = [];
    let lastFetch = 0;

    const copyToPhone = async (txt) => {
      if (!txt) { toast('Clipboard laptop kosong', 'warn', titleText, 2); return; }
      haptic();
      let copied = false;
      try {
        if (bridge?.copyText) {
          copied = !!bridge.copyText(txt);
        }
      } catch (_) {}
      if (!copied && navigator.clipboard?.writeText) {
        try { await navigator.clipboard.writeText(txt); copied = true; } catch (_) {}
      }
      if (!copied) {
        try {
          const ta = document.createElement('textarea');
          ta.value = txt;
          ta.style.position = 'fixed';
          ta.style.opacity = '0';
          document.body.appendChild(ta);
          ta.select();
          copied = document.execCommand('copy');
          ta.remove();
        } catch (_) {}
      }
      toast(copied ? 'Disalin ke clipboard HP' : 'Gagal menyalin ke HP', copied ? 'ok' : 'err', titleText, 3);
    };

    const sendToPc = async (openUrl = false) => {
      const txt = (input.value || '').trim();
      if (!txt) return;
      haptic();
      try {
        const res = await api('/clipboard', 'POST', 5000, { text: txt, openUrl });
        input.value = '';
        if (res) {
          currentText = res.text ?? txt;
          history = Array.isArray(res.history) ? res.history : history;
          render();
        }
        toast(openUrl ? 'Link dibuka di laptop' : 'Terkirim ke clipboard laptop', 'ok', titleText, 3);
      } catch (e) {
        toast((e && e.message) || 'Gagal mengirim ke laptop', 'err', titleText, 4);
      }
    };

    copyBtn.addEventListener('click', () => copyToPhone(currentText));
    refreshBtn.addEventListener('click', () => { haptic(); load(true); });
    sendBtn.addEventListener('click', () => sendToPc(false));
    openBtn.addEventListener('click', () => sendToPc(true));
    input.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') { e.preventDefault(); sendToPc(false); }
    });

    const render = () => {
      pcText.textContent = currentText || '(Kosong)';
      pcText.classList.toggle('muted', !currentText);
      const items = history.filter((x) => x && x !== currentText).slice(0, 3);
      histEl.replaceChildren(...items.map((item) => {
        const chip = h('button', 'cb-chip', item);
        chip.title = item;
        chip.addEventListener('click', () => copyToPhone(item));
        return chip;
      }));
    };

    const load = async (force = false) => {
      const now = Date.now();
      if (!force && now - lastFetch < 6000) return;
      lastFetch = now;
      try {
        const res = await api('/clipboard', 'GET', 4000);
        if (res) {
          currentText = res.text || '';
          history = Array.isArray(res.history) ? res.history : [];
          render();
        }
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
