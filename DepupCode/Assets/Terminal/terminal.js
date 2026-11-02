import { Terminal } from './vendor/xterm.mjs';
import { FitAddon } from './vendor/addon-fit.mjs';
import { ImageAddon } from './vendor/addon-image.mjs';

const host = document.getElementById('terminal');
const terminal = new Terminal({
  allowProposedApi: true,
  convertEol: false,
  cursorBlink: true,
  fontFamily: 'Cascadia Code, Consolas, monospace',
  fontSize: 12,
  scrollback: 10000,
  theme: {
    background: '#1e1e1e',
    foreground: '#d4d4d4',
    cursor: '#aeafad',
    selectionBackground: '#264f78'
  }
});
const imageAddon = new ImageAddon({
  enableSizeReports: false,
  pixelLimit: 2097152,
  storageLimit: 32,
  showPlaceholder: true,
  sixelSupport: true,
  sixelScrolling: true,
  sixelPaletteLimit: 256,
  sixelSizeLimit: 4194304,
  iipSupport: true,
  iipSizeLimit: 4194304
});
terminal.loadAddon(imageAddon);
const fit = new FitAddon();
terminal.loadAddon(fit);
terminal.open(host);

function post(message) {
  try { window.chrome?.webview?.postMessage(JSON.stringify(message)); } catch { }
}

window.addEventListener('error', event => post({ type: 'renderer-error', message: String(event.message || 'Renderer error') }));
window.addEventListener('unhandledrejection', event => post({ type: 'renderer-error', message: String(event.reason || 'Renderer promise rejected') }));

terminal.onData(data => post({ type: 'input', data }));
terminal.onBinary(data => {
  const bytes = Uint8Array.from(data, character => character.charCodeAt(0) & 0xff);
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  post({ type: 'binary', data: btoa(binary) });
});
terminal.onResize(({ cols, rows }) => post({ type: 'resize', cols, rows }));

window.chrome?.webview?.addEventListener('message', event => {
  const message = event.data;
  if (!message || typeof message !== 'object') return;
  if (message.type === 'output' && typeof message.data === 'string') {
    const callback = typeof message.probeId === 'string'
      ? () => post({ type: 'output-processed', probeId: message.probeId, imageStorageUsage: imageAddon.storageUsage })
      : undefined;
    terminal.write(message.data, callback);
  }
  else if (message.type === 'clear') terminal.clear();
  else if (message.type === 'focus') terminal.focus();
  else if (message.type === 'refresh') {
    let renderSubscription;
    if (typeof message.requestId === 'string') {
      renderSubscription = terminal.onRender(() => {
        renderSubscription.dispose();
        const result = { type: 'refresh-completed', requestId: message.requestId };
        if (message.includeImageStats === true) {
          const canvas = document.querySelector('.xterm-image-layer');
          let pixels = 0;
          if (canvas?.width && canvas.height) {
            const data = canvas.getContext('2d')?.getImageData(0, 0, canvas.width, canvas.height).data;
            if (data) for (let i = 3; i < data.length; i += 4) if (data[i] !== 0) pixels++;
          }
          Object.assign(result, { pixels, width: canvas?.width || 0, height: canvas?.height || 0 });
        }
        post(result);
      });
    }
    terminal.refresh(0, terminal.rows - 1);
  }
});

let fitScheduled = false;
function scheduleFit() {
  if (fitScheduled) return;
  fitScheduled = true;
  requestAnimationFrame(() => {
    fitScheduled = false;
    try { fit.fit(); } catch { }
  });
}

new ResizeObserver(scheduleFit).observe(host);
window.addEventListener('load', scheduleFit, { once: true });
scheduleFit();
terminal.focus();
post({ type: 'ready' });
