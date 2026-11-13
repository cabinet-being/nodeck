// Runs in X's own page world. X plays videos and GIFs from a media stream, so the page never shows
// their MP4 files; they are only in the answers of X's API. This keeps the best MP4 of each video
// seen in those answers, keyed by its poster image and its post id, for "Save to nodeck…".
(() => {
  if (window.__nodeckXMedia) return;
  const store = { byPoster: {}, byTweet: {} };
  let posters = [];
  Object.defineProperty(window, '__nodeckXMedia', { value: store });

  function add(tweetId, media) {
    const variants = media?.video_info?.variants?.filter((v) => v.content_type === 'video/mp4' && v.url);
    if (!variants?.length) return;
    const url = variants.sort((a, b) => (b.bitrate ?? 0) - (a.bitrate ?? 0))[0].url;
    const poster = media.media_url_https?.split('?')[0];
    if (poster && !store.byPoster[poster]) {
      store.byPoster[poster] = url;
      posters.push(poster);
      if (posters.length > 3000) { delete store.byPoster[posters[0]]; posters = posters.slice(1); }
    }
    if (tweetId) {
      const list = (store.byTweet[tweetId] ??= []);
      if (!list.includes(url)) list.push(url);
    }
  }

  function walk(value, depth) {
    if (!value || typeof value !== 'object' || depth > 80) return;
    if (Array.isArray(value)) { for (const item of value) walk(item, depth + 1); return; }
    const legacy = value.legacy && typeof value.legacy === 'object' ? value.legacy : null;
    const entities = legacy?.extended_entities ?? value.extended_entities;
    if (Array.isArray(entities?.media)) {
      const tweetId = value.rest_id ?? legacy?.id_str ?? value.id_str ?? null;
      for (const media of entities.media) add(tweetId, media);
    }
    for (const key in value) walk(value[key], depth + 1);
  }

  const interesting = (url) => /\/graphql\/|\/i\/api\//.test(String(url ?? ''));
  const read = (text) => { try { walk(JSON.parse(text), 0); } catch { /* not JSON */ } };

  const originalFetch = window.fetch;
  window.fetch = function (...args) {
    const promise = originalFetch.apply(this, args);
    const url = args[0] instanceof Request ? args[0].url : args[0];
    if (interesting(url)) promise.then((response) => response.clone().text().then(read)).catch(() => {});
    return promise;
  };

  const open = XMLHttpRequest.prototype.open;
  XMLHttpRequest.prototype.open = function (method, url, ...rest) {
    if (interesting(url)) {
      this.addEventListener('load', () => {
        try {
          if (this.responseType === '' || this.responseType === 'text') read(this.responseText);
          else if (this.responseType === 'json') walk(this.response, 0);
        } catch { /* ignore */ }
      });
    }
    return open.call(this, method, url, ...rest);
  };
})();
