/* Theme toggle — persists to localStorage */
(function () {
    const STORAGE_KEY = 'setgame-theme';

    function applyTheme(theme) {
        document.documentElement.setAttribute('data-theme', theme);
        const btn = document.getElementById('theme-toggle');
        if (btn) {
            btn.textContent = theme === 'light' ? '🌙' : '☀️';
            btn.title = theme === 'light' ? 'Switch to dark theme' : 'Switch to light theme';
            btn.setAttribute('aria-label', btn.title);
        }
    }

    function getPreferred() {
        const stored = localStorage.getItem(STORAGE_KEY);
        if (stored === 'light' || stored === 'dark') return stored;
        return window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
    }

    function toggle() {
        const current = document.documentElement.getAttribute('data-theme') || 'dark';
        const next = current === 'dark' ? 'light' : 'dark';
        localStorage.setItem(STORAGE_KEY, next);
        applyTheme(next);
    }

    /* Apply immediately (before paint) to avoid flash */
    applyTheme(getPreferred());

    document.addEventListener('DOMContentLoaded', function () {
        applyTheme(getPreferred());
        const btn = document.getElementById('theme-toggle');
        if (btn) btn.addEventListener('click', toggle);
    });
})();
