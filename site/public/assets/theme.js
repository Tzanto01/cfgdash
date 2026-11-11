(function () {
  var root = document.documentElement;
  root.classList.add('js');
  try {
    var theme = localStorage.getItem('cfgdash-site-theme');
    if (theme === 'light' || theme === 'dark') {
      root.setAttribute('data-theme', theme);
    }
  } catch (e) {}
  window.addEventListener('load', function () {
    if (!root.classList.contains('js-ready')) {
      root.classList.remove('js');
    }
  });
})();
