// Injected by Kaku into every previewed chapter. Talks to the host through chrome.webview.
(function () {
  var webview = window.chrome && window.chrome.webview;
  if (!webview) return;
  var post = function (m) { webview.postMessage(m); };
  var current = null;

  function block(i) { return document.querySelector('[data-kaku-b="' + i + '"]'); }

  function mark(i, scroll) {
    var el = block(i);
    if (!el) return;
    if (current) current.classList.remove('kaku-current');
    el.classList.add('kaku-current');
    current = el;
    if (scroll) el.scrollIntoView({ block: 'center', inline: 'center', behavior: 'auto' });
  }

  document.addEventListener('click', function (e) {
    if (e.target.closest && e.target.closest('a[href]')) return; // let links navigate
    var el = e.target.closest ? e.target.closest('[data-kaku-b]') : null;
    if (!el) return;
    var i = parseInt(el.getAttribute('data-kaku-b'), 10);
    mark(i, false);
    post({ type: 'block', index: i });
  });

  // Kaku shortcuts must keep working while the preview has focus.
  var keys = ['p', 'P', 'g', 'e', 'o', 'w', 's', 'S', 'z', 'y', 'f', 'F', 'h', 'j', 'PageDown', 'PageUp', 'F2'];
  document.addEventListener('keydown', function (e) {
    var plain = !e.ctrlKey && !e.altKey && (e.key === 'F2' || e.key === 'F3');
    var alt = e.altKey && !e.ctrlKey && (e.key === 'ArrowLeft' || e.key === 'ArrowRight');
    if ((e.ctrlKey && keys.indexOf(e.key) >= 0) || plain || alt) {
      e.preventDefault();
      post({ type: 'key', key: e.key, shift: e.shiftKey, ctrl: e.ctrlKey, alt: e.altKey });
    }
  }, true);

  webview.addEventListener('message', function (e) {
    var m = e.data;
    if (!m) return;
    if (m.type === 'scrollTo') mark(m.index, true);
    if (m.type === 'replace') {
      var el = block(m.index);
      if (el) {
        try { el.innerHTML = m.html; } catch (err) { location.reload(); }
      }
    }
  });

  post({ type: 'loaded', path: decodeURIComponent(location.pathname.replace(/^\//, '')) });
})();
