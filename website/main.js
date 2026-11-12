(function () {
  var root = document.documentElement;
  var button = document.getElementById('lang');
  function setLang(lang) {
    root.dataset.lang = lang;
    root.lang = lang === 'en' ? 'en' : 'zh-CN';
    var guide = root.dataset.page === 'tutorial';
    document.title = guide
      ? (lang === 'en' ? 'Perch · Getting started' : 'Perch · 使用教程')
      : (lang === 'en' ? 'Perch · a small island at the top of your screen' : 'Perch · 屏幕顶部的效率岛');
    button.setAttribute('aria-label', lang === 'en' ? '切换到中文' : 'Switch to English');
    try { localStorage.setItem('perch-lang', lang); } catch (e) { /* Storage may be disabled. */ }
  }
  var saved;
  try { saved = localStorage.getItem('perch-lang'); } catch (e) { /* Use the default. */ }
  setLang(saved === 'zh' ? 'zh' : 'en');
  button.addEventListener('click', function () {
    setLang(root.dataset.lang === 'en' ? 'zh' : 'en');
  });
})();
