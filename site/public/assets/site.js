(() => {
  const root = document.documentElement;
  const KEY = 'cfgdash-site-theme';
  const NEXT = { system: 'light', light: 'dark', dark: 'system' };
  const ICON = { system: '#i-sun-moon', light: '#i-sun', dark: '#i-moon' };

  const themeBtn = document.querySelector('.theme-btn');
  const mode = () => root.getAttribute('data-theme') || 'system';
  const showTheme = () => {
    themeBtn.setAttribute('aria-label', `Theme: ${mode()}. Switch to ${NEXT[mode()]}`);
    themeBtn.querySelector('use').setAttribute('href', ICON[mode()]);
  };
  themeBtn.addEventListener('click', () => {
    const next = NEXT[mode()];
    try {
      if (next === 'system') {
        localStorage.removeItem(KEY);
      } else {
        localStorage.setItem(KEY, next);
      }
    } catch {}
    if (next === 'system') {
      root.removeAttribute('data-theme');
    } else {
      root.setAttribute('data-theme', next);
    }
    showTheme();
  });
  showTheme();

  const menuBtn = document.querySelector('.menu-btn');
  const nav = document.getElementById('site-nav');
  const isOpen = () => menuBtn.getAttribute('aria-expanded') === 'true';
  const setMenu = (open, focus) => {
    menuBtn.setAttribute('aria-expanded', String(open));
    menuBtn.querySelector('use').setAttribute('href', open ? '#i-x' : '#i-menu');
    nav.classList.toggle('open', open);
    if (focus) {
      menuBtn.focus();
    }
  };
  menuBtn.addEventListener('click', () => setMenu(!isOpen()));
  nav.addEventListener('click', (event) => {
    if (event.target.closest('a')) {
      setMenu(false);
    }
  });
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && isOpen()) {
      setMenu(false, true);
    }
  });
  document.addEventListener('click', (event) => {
    if (isOpen() && !event.target.closest('.site-head')) {
      setMenu(false);
    }
  });
  matchMedia('(min-width: 768px)').addEventListener('change', () => setMenu(false));

  const demo = document.querySelector('.demo');
  if (demo) {
    const rows = [...demo.querySelectorAll('.kv')];
    const input = demo.querySelector('.filter-input');
    const chips = [...demo.querySelectorAll('.chip')];
    const count = demo.querySelector('.count');
    const none = demo.querySelector('.kv-none');
    let chip = 'all';
    const apply = () => {
      const text = input.value.trim().toLowerCase();
      let shown = 0;
      for (const row of rows) {
        const hay = `${row.querySelector('.kv-key').textContent} ${row.querySelector('.kv-val').textContent}`.toLowerCase();
        const match = (!text || hay.includes(text)) && (chip === 'all' || row.dataset.flags === chip);
        row.hidden = !match;
        shown += match ? 1 : 0;
      }
      none.hidden = shown > 0;
      count.textContent = !text && chip === 'all' ? '' : shown ? `${shown} of ${rows.length} rows` : 'No rows match';
      for (const c of chips) {
        c.setAttribute('aria-pressed', String(c.dataset.chip === chip));
      }
    };
    input.addEventListener('input', apply);
    for (const c of chips) {
      c.addEventListener('click', () => {
        chip = c.dataset.chip === chip ? 'all' : c.dataset.chip;
        apply();
      });
    }
    for (const button of demo.querySelectorAll('.kv-btn')) {
      button.addEventListener('click', () => button.setAttribute('aria-expanded', String(button.getAttribute('aria-expanded') !== 'true')));
    }
  }

  const tabs = [...document.querySelectorAll('[role="tab"]')];
  const select = (tab) => {
    for (const t of tabs) {
      const on = t === tab;
      t.setAttribute('aria-selected', String(on));
      t.tabIndex = on ? 0 : -1;
      document.getElementById(t.getAttribute('aria-controls')).classList.toggle('is-active', on);
    }
  };
  tabs.forEach((tab, index) => {
    tab.addEventListener('click', () => select(tab));
    tab.addEventListener('keydown', (event) => {
      const n = tabs.length;
      const to = { ArrowRight: index + 1, ArrowLeft: index - 1 + n, Home: 0, End: n - 1 }[event.key];
      if (to === undefined) {
        return;
      }
      event.preventDefault();
      select(tabs[to % n]);
      tabs[to % n].focus();
    });
  });

  const status = document.getElementById('copy-status');
  for (const button of document.querySelectorAll('.copy')) {
    button.addEventListener('click', async () => {
      let ok = true;
      try {
        await navigator.clipboard.writeText(button.dataset.copy);
      } catch {
        ok = false;
      }
      const message = ok ? 'Copied' : "Couldn't copy";
      status.textContent = '';
      setTimeout(() => {
        status.textContent = message;
      }, 50);
      const use = button.querySelector('use');
      const label = button.querySelector('.copy-label');
      use.setAttribute('href', ok ? '#i-check' : '#i-copy');
      button.classList.toggle('done', ok);
      if (label) {
        label.textContent = message;
      }
      clearTimeout(button.timer);
      button.timer = setTimeout(() => {
        use.setAttribute('href', '#i-copy');
        button.classList.remove('done');
        if (label) {
          label.textContent = 'Copy';
        }
      }, 2000);
    });
  }
})();
