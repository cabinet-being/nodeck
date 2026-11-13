// Remembers where the user last right-clicked, so "Save to nodeck…" can find the image or video
// there even when the site covers it with a transparent layer. Records nothing else.
addEventListener('contextmenu', (event) => {
  globalThis.nodeckLastContext = { x: event.clientX, y: event.clientY, target: event.target };
}, true);
