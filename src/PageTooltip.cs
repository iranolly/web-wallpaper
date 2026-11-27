namespace WebWallpaper;

/// <summary>
/// 网页里 &lt;title&gt; 的原生悬浮提示在桌面层画不出来（那是浏览器自己的小窗口，落在桌面层下面），
/// 所以注入一段脚本：悬停带 title 的元素时由页面自己画一个提示框，长得和原生的一致。
/// </summary>
internal static class PageTooltip
{
    public const string Script = """
(function () {
  if (window.__wpTipInstalled) return;
  window.__wpTipInstalled = true;

  var tip = null, host = null, timer = 0;

  function box() {
    if (tip && tip.isConnected) return tip;
    tip = document.createElement('div');
    tip.setAttribute('data-wp-tip', '1');
    tip.style.cssText = [
      'position:fixed', 'z-index:2147483647', 'max-width:340px', 'padding:6px 9px',
      'border-radius:6px', 'background:rgba(30,32,38,.98)', 'color:#eaeaec',
      'font:12px/1.5 "Segoe UI",system-ui,sans-serif', 'border:1px solid rgba(255,255,255,.09)',
      'box-shadow:0 6px 20px rgba(0,0,0,.5)', 'pointer-events:none', 'display:none',
      'white-space:pre-wrap', 'word-break:break-word', 'opacity:0', 'transition:opacity .09s linear'
    ].join(';');
    document.documentElement.appendChild(tip);
    return tip;
  }

  function restore() {
    if (host && host.__wpTitle != null) {
      try { host.setAttribute('title', host.__wpTitle); } catch (e) { }
      try { delete host.__wpTitle; } catch (e) { }
    }
    host = null;
  }

  function hide() {
    clearTimeout(timer);
    restore();
    if (tip) { tip.style.display = 'none'; tip.style.opacity = '0'; }
  }

  function show(x, y, text) {
    var b = box();
    b.textContent = text;
    b.style.display = 'block';
    var w = b.offsetWidth, h = b.offsetHeight;
    var left = Math.min(x + 14, window.innerWidth - w - 6);
    var top = y + 18;
    if (top + h > window.innerHeight - 6) top = Math.max(6, y - h - 10);
    b.style.left = Math.max(6, left) + 'px';
    b.style.top = top + 'px';
    b.style.opacity = '1';
    try { window.chrome.webview.postMessage({ wpTip: String(text).slice(0, 60) }); } catch (e) { }
  }

  function nearest(el) {
    for (var n = el; n && n !== document; n = n.parentNode) {
      if (n.nodeType === 1 && n.hasAttribute && n.hasAttribute('title')) return n;
    }
    return null;
  }

  document.addEventListener('mouseover', function (e) {
    var el = nearest(e.target);
    if (!el || el === host) return;
    hide();
    var text = el.getAttribute('title');
    if (!text) return;
    host = el;
    el.__wpTitle = text;
    el.removeAttribute('title');       // 别让系统去画那个画不出来的原生提示
    var x = e.clientX, y = e.clientY;
    timer = setTimeout(function () { if (host === el) show(x, y, text); }, 400);
  }, true);

  document.addEventListener('mouseout', function (e) {
    if (!host) return;
    // 在同一个元素内部的子节点之间移动也会发 mouseout，不能当成“离开”
    var to = e.relatedTarget;
    if (to && (to === host || (host.contains && host.contains(to)))) return;
    if (e.target === host || (host.contains && host.contains(e.target))) hide();
  }, true);

  ['mousedown', 'wheel', 'keydown'].forEach(function (t) {
    document.addEventListener(t, hide, true);
  });
  window.addEventListener('scroll', hide, true);
  window.addEventListener('blur', hide);
})();
""";
}
