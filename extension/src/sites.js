// Per-site rules for reading tags, the artist, the title and the full-size files from a page.
// Injected into the page next to page.js. To support a new site, add one entry below.
//
//   name        source name shown in nodeck (Sources)
//   hosts       host names; a rule matches the host itself and its subdomains
//   underscores true for booru sites, whose tag names use "_" instead of spaces
//   tags        { category: CSS selector } for tag links; category is one of
//               artist, character, copyright, general, meta (anything else becomes custom)
//   title(ctx)  title for the card or comic
//   info(ctx)   async; { title, tags: [{ name, category }], source: { name, url }, warning } for the page
//               or, when saving one file, for that file (ctx.srcUrl, ctx.element)
//   original(ctx)  async; URL of the full-size file for the image that was clicked (ctx.srcUrl)
//   fullImage(url) URL of the full-size file for a thumbnail URL (gallery pages)
//   video(ctx)  async; a downloadable video file for the <video> in ctx.element (or the page),
//               or { error } saying why there is none
//   needsSession   true when files are served only to the logged-in browser; they are then sent from
//               the browser instead of nodeck downloading them itself
//   collect(ctx)   async; the files of the page in order: [{ url, mediaType, fallbackUrl, thumbUrl, error }];
//               return null to use the generic collector (all <img> and <video> in page order)
//
// ctx = { doc, url (URL of the page), h (helpers from page.js), hints (data read from the page's
// own scripts), srcUrl, mediaType, element }. Rules are best effort: sites change their markup.
// The generic fallback (meta keywords, rel="tag" links, og:site_name) applies when a rule finds nothing.
(() => {
  const gelbooruTags = {
    artist: '.tag-type-artist a[href*="tags="]',
    character: '.tag-type-character a[href*="tags="]',
    copyright: '.tag-type-copyright a[href*="tags="]',
    general: '.tag-type-general a[href*="tags="]',
    meta: '.tag-type-metadata a[href*="tags="], .tag-type-meta a[href*="tags="]',
  };
  const linkByText = (doc, pattern) =>
    [...doc.querySelectorAll('a[href]')].find((a) => pattern.test(a.textContent.trim()))?.href ?? null;
  const STREAM_ONLY = 'This video is only streamed in pieces (HLS/DASH) and the page offers no single file, so it cannot be saved.';
  const memo = new Map();
  const once = (key, load) => {
    if (!memo.has(key)) memo.set(key, load().catch((error) => { memo.delete(key); throw error; }));
    return memo.get(key);
  };

  // ---------- Pixiv ----------
  const pixivArtworkId = (url) => url.pathname.match(/\/artworks\/(\d+)/)?.[1] ?? url.searchParams.get('illust_id');
  /** [illust id, page index] from an i.pximg.net file name such as 12345_p2_master1200.jpg. */
  const pximgPage = (src) => src?.match(/i\.pximg\.net\/.*\/(\d+)_p(\d+)[_.]/)?.slice(1).map(Number) ?? null;
  const pixivIllust = (h, id) => once(`pixiv:${id}`, async () => (await h.json(`/ajax/illust/${id}`)).body);
  const pixivPages = (h, id) => once(`pixiv-pages:${id}`, async () => (await h.json(`/ajax/illust/${id}/pages`)).body);

  // ---------- Pixiv Fanbox ----------
  const fanboxPostId = (url) => url.pathname.match(/\/posts\/(\d+)/)?.[1];
  const fanboxPost = (h, id) => once(`fanbox:${id}`, async () =>
    (await h.json(`https://api.fanbox.cc/post.info?postId=${id}`)).body);
  const MEDIA_EXTENSION = /^(jpe?g|png|gif|webp|avif|bmp|mp4|webm|mov)$/i;
  function fanboxFiles(post) {
    const body = post?.body;
    if (!body) return [];
    const files = [];
    const image = (img) => img?.originalUrl && files.push({
      url: img.originalUrl, fallbackUrl: img.thumbnailUrl ?? null, thumbUrl: img.thumbnailUrl ?? null });
    const file = (f) => f?.url && MEDIA_EXTENSION.test(f.extension ?? '') &&
      files.push({ url: f.url, mediaType: /^(mp4|webm|mov)$/i.test(f.extension) ? 'video' : 'image' });
    for (const block of body.blocks ?? []) {
      if (block.type === 'image') image(body.imageMap?.[block.imageId]);
      else if (block.type === 'file') file(body.fileMap?.[block.fileId]);
    }
    (body.images ?? []).forEach(image);
    (body.files ?? []).forEach(file);
    return files;
  }

  // ---------- X / Twitter ----------
  function xOriginal(url) {
    try {
      const parsed = new URL(url);
      if (parsed.hostname !== 'pbs.twimg.com' || !parsed.pathname.startsWith('/media/')) return url;
      parsed.pathname = parsed.pathname.replace(/:(thumb|small|medium|large|orig)$/, '');
      if (!parsed.searchParams.has('format')) {
        const extension = parsed.pathname.match(/\.(\w+)$/)?.[1];
        if (extension) { parsed.pathname = parsed.pathname.replace(/\.\w+$/, ''); parsed.searchParams.set('format', extension); }
      }
      parsed.searchParams.set('name', 'orig');
      return parsed.href;
    } catch { return url; }
  }
  const xStatusId = (url) => url.pathname.match(/\/status\/(\d+)/)?.[1] ?? null;
  /** The tweet an element belongs to: author handle and id from its timestamp link. */
  function xTweet(article) {
    const link = article?.querySelector('a[href*="/status/"] time')?.closest('a');
    const match = link?.getAttribute('href')?.match(/^\/([^/]+)\/status\/(\d+)/);
    return match ? { handle: match[1], id: match[2] } : null;
  }
  function xFocalArticle(ctx) {
    const id = xStatusId(ctx.url);
    if (!id) return null;
    const articles = [...ctx.doc.querySelectorAll('article')];
    return articles.find((article) => article.querySelector(`a[href*="/status/${id}"] time`)) ?? articles[0] ?? null;
  }
  /** Videos and GIFs play from a media stream; x-hook.js remembers their MP4 files from X's own API answers. */
  function xVideo(ctx, video) {
    const media = ctx.hints.xMedia;
    const poster = video?.getAttribute('poster')?.split('?')[0];
    if (poster && media?.byPoster?.[poster]) return media.byPoster[poster];
    const tweet = xTweet(video?.closest('article'));
    return tweet ? media?.byTweet?.[tweet.id]?.[0] ?? null : null;
  }
  const X_VIDEO_MISSING = 'This video plays as a stream. Reload the post with the extension installed so it can see the video file, then try again.';

  // ---------- PornHub ----------
  async function phVideo(ctx) {
    const definitions = ctx.hints.mediaDefinitions ?? ctx.h.scriptJson(ctx.doc, '"mediaDefinitions":');
    if (!Array.isArray(definitions) || definitions.length === 0) return null;
    const quality = (d) => Math.max(0, ...[].concat(d.quality ?? []).map(Number).filter(Number.isFinite));
    const best = (list) => list.filter((d) => d.format === 'mp4' && d.videoUrl && !/\.m3u8/.test(d.videoUrl))
      .sort((a, b) => quality(b) - quality(a))[0]?.videoUrl ?? null;
    const direct = best(definitions.filter((d) => /\.mp4(\?|$)/.test(d.videoUrl ?? '')));
    if (direct) return direct;
    const remote = definitions.find((d) => d.format === 'mp4' && d.videoUrl);
    if (remote) {
      const list = await ctx.h.json(new URL(remote.videoUrl, ctx.url).href);
      if (Array.isArray(list)) return best(list);
    }
    return null;
  }

  globalThis.nodeckSites = [
    {
      name: 'Danbooru',
      hosts: ['donmai.us'],
      underscores: true,
      tags: {
        artist: '.artist-tag-list [data-tag-name]',
        character: '.character-tag-list [data-tag-name]',
        copyright: '.copyright-tag-list [data-tag-name]',
        general: '.general-tag-list [data-tag-name]',
        meta: '.meta-tag-list [data-tag-name]',
      },
      original: ({ doc }) => doc.querySelector('#post-info-size a[href], .image-view-original-link')?.href
        ?? linkByText(doc, /^download$/i),
    },
    {
      name: 'Gelbooru',
      hosts: ['gelbooru.com'],
      underscores: true,
      tags: gelbooruTags,
      original: ({ doc }) => linkByText(doc, /^original image$/i),
    },
    {
      name: 'Safebooru',
      hosts: ['safebooru.org'],
      underscores: true,
      tags: gelbooruTags,
      original: ({ doc }) => linkByText(doc, /^original image$/i),
    },
    {
      name: 'Rule 34',
      hosts: ['rule34.xxx'],
      underscores: true,
      tags: gelbooruTags,
      title: ({ doc, h }) => {
        const artist = h.texts(doc, gelbooruTags.artist).slice(0, 2).join(', ');
        const subject = h.texts(doc, `${gelbooruTags.character}, ${gelbooruTags.copyright}`).slice(0, 2).join(', ');
        return [subject, artist && `by ${artist}`].filter(Boolean).join(' ');
      },
      original: ({ doc }) => linkByText(doc, /^original image$/i),
      video: ({ doc }) => doc.querySelector('#gelcomVideoPlayer source[src], video#gelcomVideoPlayer[src]')?.src ?? null,
      // A post page holds one file: the original image or the video.
      collect: ({ doc, url }) => {
        if (url.searchParams.get('s') !== 'view') return null;
        const video = doc.querySelector('#gelcomVideoPlayer source[src], video#gelcomVideoPlayer[src]')?.src;
        if (video) return [{ url: video, mediaType: 'video', thumbUrl: doc.querySelector('#gelcomVideoPlayer')?.getAttribute('poster') ?? null }];
        const sample = doc.querySelector('#image')?.src ?? null;
        const image = linkByText(doc, /^original image$/i) ?? sample;
        return image ? [{ url: image, fallbackUrl: sample, thumbUrl: sample ?? image }] : null;
      },
    },
    {
      name: 'yande.re',
      hosts: ['yande.re'],
      underscores: true,
      tags: gelbooruTags,
      original: ({ doc }) => doc.querySelector('a#png, a#highres, a.original-file-unchanged')?.href ?? null,
    },
    {
      name: 'Konachan',
      hosts: ['konachan.com', 'konachan.net'],
      underscores: true,
      tags: gelbooruTags,
      original: ({ doc }) => doc.querySelector('a#png, a#highres, a.original-file-unchanged')?.href ?? null,
    },
    {
      name: 'e621',
      hosts: ['e621.net', 'e926.net'],
      underscores: true,
      tags: {
        artist: '.artist-tag-list [data-name], .category-1 a.search-tag',
        copyright: '.copyright-tag-list [data-name], .category-3 a.search-tag',
        character: '.character-tag-list [data-name], .category-4 a.search-tag',
        general: '.general-tag-list [data-name], .category-0 a.search-tag',
        meta: '.meta-tag-list [data-name], .category-7 a.search-tag',
      },
      original: ({ doc }) => doc.querySelector('#image-container[data-file-url]')?.dataset.fileUrl
        ?? linkByText(doc, /^download$/i),
    },
    {
      name: 'Pixiv',
      hosts: ['pixiv.net'],
      async info(ctx) {
        const id = pximgPage(ctx.srcUrl)?.[0] ?? pixivArtworkId(ctx.url);
        if (!id) return null;
        const illust = await pixivIllust(ctx.h, id);
        return {
          title: illust.title || illust.illustTitle,
          tags: [
            { name: illust.userName, category: 'artist' },
            ...(illust.tags?.tags ?? []).map((tag) => ({ name: tag.tag, category: 'general' })),
          ],
          source: { name: 'Pixiv', url: `https://www.pixiv.net/artworks/${id}` },
          warning: illust.illustType === 2
            ? 'This is an ugoira (animation). Pixiv offers its frames only as a ZIP, so the first frame is saved as an image.'
            : undefined,
        };
      },
      async original(ctx) {
        const page = pximgPage(ctx.srcUrl);
        if (!page) return null;
        const pages = await pixivPages(ctx.h, page[0]);
        return pages[page[1]]?.urls?.original ?? null;
      },
      async collect(ctx) {
        const id = pixivArtworkId(ctx.url);
        if (!id) return null;
        return (await pixivPages(ctx.h, id)).map((page) => ({
          url: page.urls.original, fallbackUrl: page.urls.regular ?? null, thumbUrl: page.urls.small ?? page.urls.regular,
          width: page.width, height: page.height,
        }));
      },
    },
    {
      name: 'Pixiv Fanbox',
      hosts: ['fanbox.cc'],
      // Files are only served to the logged-in browser, so nodeck cannot download them itself.
      needsSession: true,
      async info(ctx) {
        const id = fanboxPostId(ctx.url);
        if (!id) return null;
        const post = await fanboxPost(ctx.h, id);
        return {
          title: post.title,
          tags: [
            { name: post.user?.name ?? post.creatorId, category: 'artist' },
            ...(post.tags ?? []).map((name) => ({ name, category: 'general' })),
          ],
          source: { name: 'Pixiv Fanbox', url: `https://www.fanbox.cc/@${post.creatorId}/posts/${id}` },
          warning: post.body ? undefined : 'This post is not available on your plan, so its files cannot be saved.',
        };
      },
      // Thumbnails live under /w/<width>/; the original is the same file without it.
      original: ({ srcUrl }) => srcUrl?.replace(/(downloads\.fanbox\.cc\/images\/post\/\d+\/)w\/\d+\//, '$1') ?? null,
      fullImage: (url) => url.replace(/(downloads\.fanbox\.cc\/images\/post\/\d+\/)w\/\d+\//, '$1'),
      async collect(ctx) {
        const id = fanboxPostId(ctx.url);
        return id ? fanboxFiles(await fanboxPost(ctx.h, id)) : null;
      },
    },
    {
      name: 'X',
      hosts: ['x.com', 'twitter.com'],
      info(ctx) {
        const article = ctx.element?.closest('article') ?? xFocalArticle(ctx);
        const tweet = xTweet(article);
        const handle = tweet?.handle ?? ctx.url.pathname.split('/')[1];
        const text = article?.querySelector('[data-testid="tweetText"]')?.textContent.replace(/\s+/g, ' ').trim() ?? '';
        const tags = [...(article?.querySelectorAll('[data-testid="tweetText"] a[href^="/hashtag/"]') ?? [])]
          .map((a) => ({ name: a.textContent.trim().replace(/^#/, ''), category: 'general' }));
        if (handle && !['home', 'i', 'search', 'explore', 'notifications'].includes(handle))
          tags.unshift({ name: handle, category: 'artist' });
        return {
          title: text ? text.slice(0, 120) : handle ? `@${handle}` : undefined,
          tags,
          source: { name: 'X', url: tweet ? `https://x.com/${tweet.handle}/status/${tweet.id}` : ctx.url.href },
        };
      },
      original: ({ srcUrl }) => (srcUrl ? xOriginal(srcUrl) : null),
      fullImage: xOriginal,
      video: (ctx) => xVideo(ctx, ctx.element) ?? { error: X_VIDEO_MISSING },
      collect(ctx) {
        const article = xFocalArticle(ctx);
        if (!article) return null;
        const items = [];
        for (const el of article.querySelectorAll('img[src*="pbs.twimg.com/media/"], video')) {
          if (el.tagName === 'IMG') items.push({ url: xOriginal(el.src), fallbackUrl: el.src, thumbUrl: el.src });
          else {
            const url = xVideo(ctx, el);
            const thumbUrl = el.getAttribute('poster');
            items.push(url ? { url, mediaType: 'video', thumbUrl } : { url: null, mediaType: 'video', error: X_VIDEO_MISSING, thumbUrl });
          }
        }
        return items;
      },
    },
    {
      name: 'PornHub',
      hosts: ['pornhub.com'],
      title: ({ doc, h }) => h.text(doc, 'h1.title .inlineFree') || h.text(doc, 'h1.title'),
      tags: {
        artist: '.video-detailed-info .usernameWrap a, .userInfo .usernameWrap a',
        character: '.pornstarsWrapper a.pstar-list-btn',
        general: '.tagsWrapper a.item, .categoriesWrapper a.item',
      },
      video: phVideo,
      async collect(ctx) {
        if (!/view_video/.test(ctx.url.pathname)) return null;
        const url = await phVideo(ctx);
        const thumbUrl = ctx.doc.querySelector('meta[property="og:image"]')?.content ?? null;
        return [url ? { url, mediaType: 'video', thumbUrl } : { url: null, mediaType: 'video', error: STREAM_ONLY, thumbUrl }];
      },
    },
    {
      name: 'DeviantArt',
      hosts: ['deviantart.com'],
      info: ({ doc, h }) => ({
        tags: [
          ...h.texts(doc, 'a[href*="deviantart.com/tag/"], a[href^="/tag/"]').map((name) => ({ name, category: 'general' })),
          ...h.texts(doc, 'a[data-hook="user_link"][data-username]').slice(0, 1).map((name) => ({ name, category: 'artist' })),
        ],
      }),
    },
    {
      name: 'ArtStation',
      hosts: ['artstation.com'],
      info: ({ doc, h }) => ({
        tags: [
          ...h.texts(doc, '.project-tags a').map((name) => ({ name, category: 'general' })),
          ...h.texts(doc, '.project-author-name a, .artist-name a').slice(0, 1).map((name) => ({ name, category: 'artist' })),
        ],
      }),
    },
    {
      name: 'E-Hentai',
      hosts: ['e-hentai.org', 'exhentai.org'],
      title: ({ doc, h }) => h.text(doc, '#gj') || h.text(doc, '#gn') || h.text(doc, '#i1 h1'),
      info: ({ doc }) => ({
        tags: [...doc.querySelectorAll('#taglist a[id^="ta_"]')].map((a) => {
          const [namespace, ...rest] = a.id.slice(3).split(':');
          const name = (rest.length ? rest.join(':') : namespace).replace(/_/g, ' ');
          const category = { artist: 'artist', group: 'artist', parody: 'copyright', character: 'character' }[namespace]
            ?? 'general';
          return { name, category };
        }),
      }),
    },
    {
      name: 'nhentai',
      hosts: ['nhentai.net'],
      title: ({ doc, h }) => h.text(doc, '#info h1.title') || h.text(doc, '#info h1'),
      info: ({ doc }) => ({
        tags: [...doc.querySelectorAll('#tags .tag-container')].flatMap((container) => {
          const label = container.firstChild?.textContent?.trim().toLowerCase() ?? '';
          const category = label.startsWith('artist') || label.startsWith('group') ? 'artist'
            : label.startsWith('parod') ? 'copyright'
            : label.startsWith('character') ? 'character'
            : label.startsWith('tag') ? 'general' : null;
          if (!category) return [];
          return [...container.querySelectorAll('a .name')].map((el) => ({ name: el.textContent.trim(), category }));
        }),
      }),
      fullImage: (url) => url.replace(/\/\/t(\d*)\.nhentai\.net\/galleries\/(\d+)\/(\d+)t\.(\w+)/,
        '//i$1.nhentai.net/galleries/$2/$3.$4'),
    },
  ];
})();
