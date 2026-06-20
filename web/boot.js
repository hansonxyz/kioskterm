// KioskTerm frontend bootstrap. Kept as an external file so the page can run
// under a strict Content-Security-Policy (no inline scripts).
(function () {
  "use strict";

  var FONT_SIZE = 16;
  var LINE_HEIGHT = 1.2;
  var FONT_FAMILY = "'Cascadia Mono','Cascadia Code','Consolas',monospace";

  var root = document.getElementById('root');
  var headerEl = document.getElementById('header');
  var headerTextEl = document.getElementById('header-text');
  var headerLogoBox = document.getElementById('header-logo');
  var logoImg = document.getElementById('logo-img');

  // Measure a single character cell in the chosen font so margins and the
  // header height are expressed in real character units.
  function cellMetrics() {
    var span = document.createElement('span');
    span.style.cssText =
      'position:absolute;visibility:hidden;white-space:pre;' +
      'font-family:' + FONT_FAMILY + ';font-size:' + FONT_SIZE + 'px;line-height:' + LINE_HEIGHT + ';';
    span.textContent = 'M';
    document.body.appendChild(span);
    var r = span.getBoundingClientRect();
    span.parentNode.removeChild(span);
    return { w: r.width, h: r.height };
  }

  var cell = cellMetrics();

  // Requirement 7: one character of margin around the entire window.
  root.style.padding = cell.h + 'px ' + cell.w + 'px';

  // The header sizes itself to its content (set later via setHeader).
  headerEl.style.fontSize = FONT_SIZE + 'px';
  headerEl.style.lineHeight = LINE_HEIGHT;

  var term = new Terminal({
    fontFamily: FONT_FAMILY,
    fontSize: FONT_SIZE,
    lineHeight: LINE_HEIGHT,
    disableStdin: true,          // display-only: never accept user input
    cursorBlink: false,
    scrollback: 5000,
    theme: {
      background: '#0C0C0C',
      foreground: '#CCCCCC',
      cursor: '#CCCCCC',
      selectionBackground: '#264F78'
    }
  });

  var fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.open(document.getElementById('term'));

  var host = window.chrome && window.chrome.webview;

  function postSize() {
    if (host) host.postMessage('size:' + term.cols + 'x' + term.rows);
  }

  function doFit() {
    try { fit.fit(); } catch (e) {}
    postSize();
  }

  var headerHeightPx = 0;

  // Cap the logo to the text-driven band height so it can never grow the header.
  function capLogoHeight() {
    if (headerHeightPx > 0) logoImg.style.maxHeight = headerHeightPx + 'px';
  }

  function setHeader(text) {
    // Two blank lines, the (possibly multi-line) message, then two blank lines.
    var content = '\n\n' + text + '\n\n';
    headerTextEl.textContent = content;
    headerHeightPx = content.split('\n').length * cell.h;
    capLogoHeight();
    // Band height changed: re-fit the terminal into the remaining space and
    // report the new size to the host (after layout settles).
    requestAnimationFrame(doFit);
  }

  function setLogo(src) {
    // Re-fit once the image's dimensions are known (its width affects where the
    // caption centers, and load is async).
    logoImg.onload = function () { requestAnimationFrame(doFit); };
    logoImg.src = src;
    headerLogoBox.style.display = 'flex';
    // If there's no caption, still reserve a default band so the logo has a height.
    if (!headerTextEl.textContent) setHeader('');
    capLogoHeight();
    requestAnimationFrame(doFit);
  }

  // Input mode: make the terminal accept keystrokes and forward them (already
  // xterm-encoded: text, \r for Enter, escape sequences, etc.) to the host, which
  // writes them to the console's stdin.
  var inputEnabled = false;
  function enableInput() {
    if (inputEnabled) return;
    inputEnabled = true;
    term.options.disableStdin = false;
    term.options.cursorBlink = true;
    term.onData(function (d) { if (host) host.postMessage('i:' + d); });
    term.focus();
  }

  if (host) {
    host.addEventListener('message', function (e) {
      var m = e.data;
      if (typeof m !== 'string') return;
      if (m.charCodeAt(0) === 111 && m.charCodeAt(1) === 58) {       // "o:" PTY output
        var bin = atob(m.slice(2));
        var bytes = new Uint8Array(bin.length);
        for (var i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
        term.write(bytes);
      } else if (m.charCodeAt(0) === 104 && m.charCodeAt(1) === 58) { // "h:" header caption
        setHeader(m.slice(2));
      } else if (m.charCodeAt(0) === 108 && m.charCodeAt(1) === 58) { // "l:" logo url
        setLogo(m.slice(2));
      } else if (m === 'input:1') {                                  // enable keyboard input
        enableInput();
      } else if (m === 'focus') {                                    // reclaim terminal focus
        term.focus();
      }
    });
  }

  window.addEventListener('resize', doFit);

  // First layout, then tell the host our size so it can size & start the PTY.
  doFit();
  if (host) host.postMessage('ready');
  postSize();
})();
