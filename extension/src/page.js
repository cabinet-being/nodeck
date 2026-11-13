// Runs in the page (content-script world) when the user saves something.
// Reads the title, tags, source and image URLs; never changes the page.
(() => {
  const h = {
    text: (doc, selector) => doc.querySelector(selector)?.textContent?.trim() ?? '',
    texts: (doc, selector) => [...doc.querySelectorAll(selector)]
      .map((el) => el.textContent.trim()).filter(Boolean),
  };
  const CATEGORIES = new Set(['artist', 'character', 'copyright', 'general', 'meta']);

  function siteRule() {
    const host = location.hostname.replace(/^www\./, '');
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
    return el.getAttribute('data-tag-name') ?? el.getAttribute('data-name') ?? el.textContent;
  }

  function readTags(rule) {
    const found = [];
    if (rule?.tags) {
      for (const [category, selector] of Object.entries(rule.tags))
        for (const el of document.querySelectorAll(selector)) found.push({ name: tagName(el), category });
    }
    if (rule?.tagsFrom) {
      try { found.push(...rule.tagsFrom(document, h)); } catch { /* broken rule: fall back below */ }
    }
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
      const name = cleanTag(tag.name, rule?.underscores);
      const key = name.toLowerCase();
      if (!name || name.length < 2 || seen.has(key)) continue;
      seen.add(key);
      tags.push({ name, category: CATEGORIES.has(tag.category) ? tag.category : 'custom' });
      if (tags.length >= 150) break;
    }
    return tags;
  }

  function pageTitle(rule) {
    let title = '';
    try { title = rule?.title?.(document, h) ?? ''; } catch { /* ignore */ }
    return (title || meta('og:title') || document.title || location.hostname).trim().slice(0, 200);
  }

  function sourceInfo(rule) {
    const canonical = document.querySelector('link[rel="canonical"]')?.href;
    const url = canonical && new URL(canonical).hostname === location.hostname ? canonical : location.href;
    return {
      name: rule?.name ?? (meta('og:site_name') || location.hostname.replace(/^www\./, '')),
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

  function describe(srcUrl, mediaType) {
    const rule = siteRule();
    let fileUrl = srcUrl;
    if (mediaType === 'image') {
      try { fileUrl = rule?.original?.(document, h, srcUrl) ?? srcUrl; } catch { /* keep srcUrl */ }
    }
    const element = [...document.querySelectorAll(mediaType === 'video' ? 'video, video source' : 'img')]
      .find((el) => el.currentSrc === srcUrl || el.src === srcUrl);
    const alt = (element?.getAttribute('alt') || element?.getAttribute('title') || '').trim();
    return {
      mode: 'single',
      mediaType,
      pageUrl: location.href,
      title: (alt.length >= 3 && alt.length <= 200 ? alt : pageTitle(rule)),
      tags: readTags(rule),
      source: sourceInfo(rule),
      items: [{ url: absolute(fileUrl) ?? srcUrl, fallbackUrl: fileUrl !== srcUrl ? srcUrl : null }],
    };
  }

  function collect() {
    const rule = siteRule();
    const seen = new Set();
    const items = [];
    for (const img of document.querySelectorAll('img')) {
      const url = imageUrl(img, rule);
      if (!url || seen.has(url)) continue;
      seen.add(url);
      const fallback = img.currentSrc || img.src;
      items.push({
        url,
        fallbackUrl: fallback && fallback !== url && !fallback.startsWith('data:') ? fallback : null,
        width: img.naturalWidth || Number(img.getAttribute('width')) || 0,
        height: img.naturalHeight || Number(img.getAttribute('height')) || 0,
      });
      if (items.length >= 500) break;
    }
    return {
      mode: 'gallery',
      mediaType: 'image',
      pageUrl: location.href,
      title: pageTitle(rule),
      tags: readTags(rule),
      source: sourceInfo(rule),
      items,
    };
  }

  /** Fetches a file the way the page sees it (cookies, blob: URLs) and returns it as a data URL. */
  async function fetchAsDataUrl(url) {
    const response = await fetch(url, { credentials: 'include' });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
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
