// Language switch (Chinese default, remembered), screenshot placeholders and a light scroll reveal.
(function () {
  var root = document.documentElement;

  function setLang(lang) {
    root.dataset.lang = lang;
    root.lang = lang === 'en' ? 'en' : 'zh-CN';
    document.title = lang === 'en' ? 'Perch · a small island at the top of your screen' : 'Perch · 屏幕顶部的效率岛';
    try { localStorage.setItem('perch-lang', lang); } catch (e) { /* private mode */ }
    labelMissing();
  }

  var saved = null;
  try { saved = localStorage.getItem('perch-lang'); } catch (e) { /* ignore */ }
  // English is the default; a visitor's own choice (the switch button) is remembered.
  setLang(saved === 'zh' ? 'zh' : 'en');

  document.getElementById('lang').addEventListener('click', function () {
    setLang(root.dataset.lang === 'en' ? 'zh' : 'en');
  });

  // A screenshot that does not exist yet becomes a labelled placeholder, so the page still reads well.
  // Run tools/Capture-Shots.ps1 (or drop PNGs into assets/shots/) and they appear on the next reload.
  function labelMissing() {
    var en = root.dataset.lang === 'en';
    document.querySelectorAll('.shot.missing').forEach(function (fig) {
      var file = fig.dataset.file;
      var manual = fig.dataset.manual === '1';
      fig.dataset.label = (en ? 'Screenshot to come' : '截图待补') + '\n' + file + (manual ? (en ? '\n(take this one by hand)' : '\n（需要手动截取）') : '');
    });
  }

  document.querySelectorAll('.shot img').forEach(function (img) {
    function miss() { img.closest('.shot').classList.add('missing'); labelMissing(); }
    if (img.complete && img.naturalWidth === 0) miss();
    img.addEventListener('error', miss);
  });
  labelMissing();

  if ('IntersectionObserver' in window) {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) { if (e.isIntersecting) { e.target.classList.add('in'); io.unobserve(e.target); } });
    }, { threshold: 0.12 });
    document.querySelectorAll('.row, .card, .split, .get-box, .bullets').forEach(function (el) { el.classList.add('reveal'); io.observe(el); });
  }
})();
