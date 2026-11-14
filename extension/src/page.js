// Runs in the page (content-script world) when the user saves something.
// Reads the title, tags, source and file URLs; never changes the page.
(() => {
  const h = {
    text: (doc, selector) => doc.querySelector(selector)?.textContent?.trim() ?? '',
    texts: (doc, selector) => [...doc.querySelectorAll(selector)]
      .map((el) => el.textContent.trim()).filter(Boolean),
    /** Fetches JSON as the page would (same cookies). */
    async json(url, init = {}) {
      const response = await fetch(url, { credentials: 'include', ...init });
      if (!response.ok) throw new Error(`${new URL(url, location.href).hostname} answered ${response.status}`);
      return await response.json();
    },
    /** Parses the JSON array or object that follows `key` in one of the page's inline scripts. */
    scriptJson(doc, key) {
      for (const script of doc.querySelectorAll('script:not([src])')) {
        const text = script.textContent;
        const start = text.indexOf(key);
        if (start < 0) continue;
        const value = balanced(text, start + key.length);
        if (value) try { return JSON.parse(value); } catch { /* not plain JSON */ }
      }
      return null;
    },
  };

  function balanced(text, from) {
    let index = from;
    while (/\s/.test(text[index] ?? '')) index++;
    const open = text[index];
    if (open !== '[' && open !== '{') return null;
    let depth = 0;
    let quote = null;
    for (let i = index; i < text.length; i++) {
      const char = text[i];
      if (quote) {
        if (char === '\\') i++;
        else if (char === quote) quote = null;
      } else if (char === '"' || char === "'") quote = char;
      else if (char === '[' || char === '{') depth++;
      else if (char === ']' || char === '}') {
        depth--;
        if (depth === 0) return text.slice(index, i + 1);
      }
    }
    return null;
  }

  const CATEGORIES = new Set(['artist', 'character', 'copyright', 'general', 'meta']);
  // Tests load a saved copy of a page and set this to the address it came from.
  const pageUrl = () => new URL(globalThis.nodeckPageUrl ?? location.href);

  function siteRule() {
    const host = pageUrl().hostname.replace(/^www\./, '');
    return (globalThis.nodeckSites ?? []).find((rule) =>
      rule.hosts.some((ruleHost) => host === ruleHost || host.endsWith(`.${ruleHost}`))) ?? null;
  }

  function meta(name) {
    return document.querySelector(`meta[name="${name}" i], meta[property="${name}" i]`)?.content?.trim() ?? '';
  }

  function cleanTag(name, underscores) {
    let value = String(name ?? '').replace(/\s+/g, ' ').trim();
    if (underscores) value = value.replace(/ /g, '_');
    return value.slice(0, 100);
  }

  function tagName(el) {
    return el.getAttribute('data-tag-name') ?? el.getAttribute('data-name') ?? el.getAttribute('data-mxptext')
      ?? el.textContent;
  }

  async function call(fn, ctx, warnings) {
    if (!fn) return null;
    try { return (await fn(ctx)) ?? null; } catch (error) {
      warnings.push(`Could not read everything from this site (${error?.message ?? error}).`);
      return null;
    }
  }

  function readTags(rule, extra) {
    const found = [];
    if (rule?.tags) {
      for (const [category, selector] of Object.entries(rule.tags))
        for (const el of document.querySelectorAll(selector)) found.push({ name: tagName(el), category });
    }
    found.push(...(extra ?? []));
    if (found.length === 0) {
      const keywords = meta('keywords').split(',');
      const articleTags = [...document.querySelectorAll('meta[property="article:tag"]')].map((m) => m.content);
      const relTags = [...document.querySelectorAll('a[rel~="tag"]')].map((a) => a.textContent);
      for (const name of [...articleTags, ...relTags, ...keywords]) found.push({ name, category: 'custom' });
      const author = meta('author') || meta('article:author') || meta('twitter:creator').replace(/^@/, '');
      if (author && !/^https?:/.test(author)) found.push({ name: author, category: 'artist' });
    }
    const seen = new Set();
    const tags = [];
    for (const tag of found) {
      const name = cleanTag(tag?.name, rule?.underscores);
      const key = name.toLowerCase();
      if (!name || name.length < 2 || seen.has(key)) continue;
      seen.add(key);
      tags.push({ name, category: CATEGORIES.has(tag.category) ? tag.category : 'custom' });
      if (tags.length >= 150) break;
    }
    return tags;
  }

  function pageTitle(rule, ctx) {
    let title = '';
    try { title = rule?.title?.(ctx) ?? ''; } catch { /* ignore */ }
    return (title || meta('og:title') || document.title || ctx.url.hostname).trim().slice(0, 200);
  }

  function sourceInfo(rule, ctx) {
    const canonical = document.querySelector('link[rel="canonical"]')?.href;
    let url = ctx.url.href;
    try { if (canonical && new URL(canonical).hostname === ctx.url.hostname) url = canonical; } catch { /* keep */ }
    return {
      name: rule?.name ?? (meta('og:site_name') || ctx.url.hostname.replace(/^www\./, '')),
      url,
    };
  }

  function absolute(url) {
    if (!url) return null;
    try { return new URL(url, document.baseURI).href; } catch { return null; }
  }

  function largestFromSrcset(srcset) {
    let best = null;
    let bestSize = -1;
    for (const part of (srcset ?? '').split(/,\s+/)) {
      const [url, descriptor = '1x'] = part.trim().split(/\s+/);
      const size = parseFloat(descriptor) || 1;
      if (url && size > bestSize) { best = url; bestSize = size; }
    }
    return best;
  }

  const LAZY_ATTRIBUTES = ['data-src', 'data-original', 'data-lazy-src', 'data-url', 'data-full', 'data-file'];
  const IMAGE_LINK = /\.(jpe?g|png|gif|webp|avif|bmp)(\?|#|$)/i;
  const VIDEO_FILE = /\.(mp4|webm|mov|m4v)(\?|#|$)/i;
  const STREAM = /\.(m3u8|mpd)(\?|#|$)/i;

  /** Best full-size URL for an <img>: a direct image link around it, a lazy attribute, srcset, src. */
  function imageUrl(img, rule) {
    const link = img.closest('a[href]');
    const lazy = LAZY_ATTRIBUTES.map((name) => img.getAttribute(name)).find((value) => value && !value.startsWith('data:'));
    const srcset = largestFromSrcset(img.getAttribute('data-srcset') || img.getAttribute('srcset'));
    let url = null;
    if (link && IMAGE_LINK.test(link.href) && !link.href.startsWith('data:')) url = link.href;
    else url = absolute(lazy) ?? absolute(srcset) ?? (img.currentSrc || img.src || null);
    if (url && rule?.fullImage) {
      try { url = rule.fullImage(url) ?? url; } catch { /* keep url */ }
    }
    return url && !url.startsWith('data:image/gif') ? url : null;
  }

  function backgroundImage(el) {
    const value = getComputedStyle(el).backgroundImage;
    const match = value && value !== 'none' ? value.match(/url\(["']?(.*?)["']?\)/) : null;
    return match && !match[1].startsWith('data:') ? absolute(match[1]) : null;
  }

  /**
   * The image or video under the last right-click, also when the site covers it with a transparent
   * layer (then the browser offers no image menu). context.js records where the click was.
   */
  function mediaAtPointer() {
    const last = globalThis.nodeckLastContext;
    if (!last) return null;
    const stack = [last.target, ...document.elementsFromPoint(last.x, last.y)].filter(Boolean);
    for (const el of stack) {
      if (el.tagName === 'VIDEO') return { element: el, mediaType: 'video', url: el.currentSrc || el.src || null };
      if (el.tagName === 'IMG' && (el.currentSrc || el.src))
        return { element: el, mediaType: 'image', url: el.currentSrc || el.src };
    }
    for (const el of stack) {
      if (el === document.body || el === document.documentElement) continue;
      const video = el.querySelector?.('video');
      if (video && video.getBoundingClientRect().width > 100)
        return { element: video, mediaType: 'video', url: video.currentSrc || video.src || null };
    }
    for (const el of stack) {
      const url = el instanceof Element ? backgroundImage(el) : null;
      if (url) return { element: el, mediaType: 'image', url };
    }
    return null;
  }

  /** What the page itself shows: used as the preview, so nothing large is downloaded to look at it. */
  function displayedSrc(el) {
    if (el?.tagName !== 'IMG') return el ? backgroundImage(el) : null;
    const src = el.currentSrc || el.src;
    return src && !src.startsWith('data:') ? src : null;
  }

  function posterOf(video) {
    return absolute(video?.getAttribute?.('poster')) ?? (absolute(meta('og:image')) || null);
  }

  function findElement(srcUrl, mediaType) {
    const selector = mediaType === 'video' ? 'video' : 'img';
    return [...document.querySelectorAll(selector)].find((el) => el.currentSrc === srcUrl || el.src === srcUrl ||
      [...el.querySelectorAll('source')].some((source) => source.src === srcUrl)) ?? null;
  }

  function streamReason(urls) {
    if (urls.some((url) => url?.startsWith('blob:') || url?.startsWith('mediasource:')))
      return 'This video plays as a stream assembled by the page, and the page offers no single file to download, so it cannot be saved.';
    if (urls.some((url) => STREAM.test(url ?? '')))
      return 'This video is only streamed in pieces (HLS/DASH) and the page offers no single file, so it cannot be saved.';
    return 'No downloadable video file was found for this video.';
  }

  /** A downloadable file for a <video>: the site rule, a direct src or <source>, then the page's metadata. */
  async function resolveVideo(element, srcUrl, rule, ctx, warnings) {
    // A rule answers with the file URL, or { error } when it knows why there is none.
    const fromRule = await call(rule?.video, { ...ctx, element }, warnings);
    if (typeof fromRule === 'string') return { url: fromRule };
    const ruleError = fromRule?.error ?? null;
    const urls = [srcUrl, element?.currentSrc, element?.src,
      ...[...(element?.querySelectorAll('source') ?? [])].map((source) => source.src)].filter(Boolean);
    const direct = urls.find((url) => /^https?:/.test(url) && !STREAM.test(url));
    if (direct) return { url: direct };
    const described = [meta('og:video:secure_url'), meta('og:video:url'), meta('og:video'), meta('twitter:player:stream'),
      ...jsonLdVideos()].map(absolute).find((url) => url && VIDEO_FILE.test(url));
    if (described) return { url: described };
    return { url: null, error: ruleError ?? streamReason(urls) };
  }

  function jsonLdVideos() {
    const urls = [];
    for (const script of document.querySelectorAll('script[type="application/ld+json"]')) {
      try {
        const data = JSON.parse(script.textContent);
        for (const item of [].concat(data, data?.['@graph'] ?? []))
          if (item?.['@type'] === 'VideoObject' && item.contentUrl) urls.push(item.contentUrl);
      } catch { /* ignore */ }
    }
    return urls;
  }

  async function describe(srcUrl, mediaType, hints) {
    const rule = siteRule();
    const warnings = [];
    let element = null;
    if (!srcUrl || (mediaType !== 'image' && mediaType !== 'video')) {
      const found = mediaAtPointer();
      if (found) ({ url: srcUrl, mediaType, element } = found);
    }
    if (srcUrl && !element) element = findElement(srcUrl, mediaType);
    if (mediaType === 'image' && element?.tagName === 'VIDEO') mediaType = 'video';
    const ctx = { doc: document, url: pageUrl(), h, hints: hints ?? {}, srcUrl, mediaType, element };
    const info = await call(rule?.info, ctx, warnings) ?? {};
    if (info.warning) warnings.push(info.warning);
    const alt = (element?.getAttribute('alt') || element?.getAttribute('title') || '').trim();
    const job = {
      mode: 'single',
      mediaType: mediaType ?? 'image',
      pageUrl: ctx.url.href,
      title: info.title || (alt.length >= 3 && alt.length <= 200 ? alt : pageTitle(rule, ctx)),
      tags: readTags(rule, info.tags),
      source: info.source ?? sourceInfo(rule, ctx),
      needsSession: !!rule?.needsSession,
      items: [],
    };
    if (!srcUrl && !element) {
      job.error = 'There is no image or video under the pointer. Use “Save page images to nodeck…” to choose from everything on the page.';
    } else if (mediaType === 'video') {
      const video = await resolveVideo(element, srcUrl, rule, ctx, warnings);
      job.items = [{ url: video.url, mediaType: 'video', error: video.error ?? null, thumbUrl: posterOf(element) }];
    } else {
      const original = await call(rule?.original, ctx, warnings);
      // A thumbnail that links to its full image, or has a larger srcset/lazy source, saves the larger file.
      const larger = element?.tagName === 'IMG' ? imageUrl(element, rule) : null;
      const url = absolute(original) ?? larger ?? absolute(srcUrl) ?? srcUrl;
      job.items = [{ url, mediaType: 'image', fallbackUrl: url !== srcUrl ? srcUrl : null,
        thumbUrl: displayedSrc(element) ?? srcUrl }];
    }
    if (warnings.length) job.warning = warnings.join(' ');
    return job;
  }

  async function collect(hints) {
    const rule = siteRule();
    const warnings = [];
    const ctx = { doc: document, url: pageUrl(), h, hints: hints ?? {}, srcUrl: null, mediaType: null, element: null };
    const info = await call(rule?.info, ctx, warnings) ?? {};
    if (info.warning) warnings.push(info.warning);
    let items = await call(rule?.collect, ctx, warnings);
    if (!items) items = await genericCollect(rule, ctx, warnings);
    const seen = new Set();
    items = items.filter((item) => {
      if (!item.url) return true;
      if (seen.has(item.url)) return false;
      seen.add(item.url);
      return true;
    }).slice(0, 500).map((item) => ({
      url: item.url ?? null,
      mediaType: item.mediaType ?? 'image',
      fallbackUrl: item.fallbackUrl ?? null,
      width: item.width ?? 0,
      height: item.height ?? 0,
      tiny: !!item.tiny,
      error: item.error ?? null,
      thumbUrl: item.thumbUrl ?? (item.mediaType === 'video' ? null : item.fallbackUrl ?? item.url ?? null),
    }));
    return {
      mode: 'gallery',
      mediaType: 'image',
      pageUrl: ctx.url.href,
      title: info.title || pageTitle(rule, ctx),
      tags: readTags(rule, info.tags),
      source: info.source ?? sourceInfo(rule, ctx),
      needsSession: !!rule?.needsSession,
      items,
      warning: warnings.length ? warnings.join(' ') : undefined,
    };
  }

  /** Every <img> and <video> in page order. */
  async function genericCollect(rule, ctx, warnings) {
    const items = [];
    for (const el of document.querySelectorAll('img, video')) {
      if (el.tagName === 'VIDEO') {
        if (el.getBoundingClientRect().width < 100 && !el.src && !el.querySelector('source')) continue;
        const video = await resolveVideo(el, null, rule, ctx, warnings);
        items.push({ url: video.url, mediaType: 'video', error: video.error ?? null, thumbUrl: posterOf(el) });
        continue;
      }
      const url = imageUrl(el, rule);
      if (!url) continue;
      const fallback = el.currentSrc || el.src;
      const shown = el.getBoundingClientRect();
      const naturalSmall = el.naturalWidth && Math.min(el.naturalWidth, el.naturalHeight) < 120 && url === fallback;
      items.push({
        url,
        thumbUrl: displayedSrc(el) ?? url,
        fallbackUrl: fallback && fallback !== url && !fallback.startsWith('data:') ? fallback : null,
        // Icons, avatars and logos: shown small, or the file itself is small. They start unchosen.
        tiny: !!(naturalSmall || (shown.width && shown.height && Math.min(shown.width, shown.height) < 80)),
      });
      if (items.length >= 500) break;
    }
    return items;
  }

  /** Fetches a file the way the page sees it (cookies, blob: URLs) and returns it as a data URL. */
  async function fetchAsDataUrl(url) {
    const response = await fetch(url, { credentials: 'include' });
    if (!response.ok) throw new Error(`The site answered ${response.status}.`);
    const blob = await response.blob();
    return await new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(reader.result);
      reader.onerror = () => reject(reader.error);
      reader.readAsDataURL(blob);
    });
  }

  globalThis.nodeckPage = { describe, collect, fetchAsDataUrl };
})();
