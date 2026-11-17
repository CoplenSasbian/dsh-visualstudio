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
    // A chip at a root shows only `file.cs`; the extension tries every open root.
    // No dots in the base: DOM class names (`tool.call.toolview`) must not look
    // like a file name.
    if (/^[A-Za-z0-9_@-]+\.[A-Za-z0-9]{1,8}$/.test(text)) return text;
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
    if (node.children && node.children.length > 0) {
      // The click can land on the tool card while the path is in its child chip.
      for (var childIndex = 0; childIndex < Math.min(node.children.length, 4); childIndex += 1) {
        var child = node.children[childIndex];
        if (!child || (child.children && child.children.length > 0)) continue;
        var nested = pathOf(child);
        if (nested) return nested;
      }
      return '';
    }
    return looksLikePath(node.textContent || '');
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
          // The shell resolves relative paths against the bound session's
          // working directory and forwards the result to the extension.
          window.parent.postMessage({ type: 'quickdock:open-file', path: path + '\u0000bridge' }, '*');
        } catch (error) {
          // The shell is gone; nothing to tell.
        }
        return;
      }
      node = node.parentElement;
    }
  }, true);
})();
