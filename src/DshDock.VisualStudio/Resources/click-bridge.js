(function () {
  // Runs in every frame WebView2 creates. Only the DSH app frame (one level
  // below the QuickDock shell) is interesting, and only in a WebView2 host.
  if (!window.chrome || !window.chrome.webview) return;
  if (window === window.top) return;
  if (window.parent !== window.top) return;
  if (location.port === '47910') return;

  function looksLikePath(value) {
    if (typeof value !== 'string') return '';
    var text = value.trim();
    if (!text || text.length > 400) return '';
    if (/^[A-Za-z]:[\\/][^\n\r<>"|?*]+$/.test(text)) return text;
    if (/^(?:[A-Za-z0-9_.@-]+[\\/])+[A-Za-z0-9_.@-]+\.[A-Za-z0-9]{1,8}$/.test(text)) return text;
    return '';
  }

  function pathOf(node) {
    if (!node || !node.getAttribute) return '';
    if (node.attributes) {
      for (var index = 0; index < node.attributes.length; index += 1) {
        var found = looksLikePath(node.attributes[index].value);
        if (found) return found;
      }
    }
    if (!node.children || node.children.length === 0) {
      return looksLikePath(node.textContent || '');
    }
    return '';
  }

  document.addEventListener('mousedown', function (event) {
    if (event.button !== 0) return;
    if (event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    var target = event.target;
    if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable)) return;
    var node = target;
    for (var depth = 0; node && node !== document.body && depth < 6; depth += 1) {
      var path = pathOf(node);
      if (path) {
        // The editor owns the click: keep DSH's own preview from opening too.
        event.preventDefault();
        event.stopImmediatePropagation();
        try {
          window.chrome.webview.postMessage('open-file:' + path + '\u0000bridge');
        } catch (error) {
          // The host is gone; nothing to tell.
        }
        return;
      }
      node = node.parentElement;
    }
  }, true);
})();
