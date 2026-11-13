// Per-site rules for reading tags, the artist, the title and the full-size file from a page.
// Injected into the page next to page.js. To support a new site, add one entry below:
//
//   name      source name shown in nodeck (Sources)
//   hosts     host names; a rule matches the host itself and its subdomains
//   tags      { category: CSS selector } for tag links; category is one of
//             artist, character, copyright, general, meta (anything else becomes custom)
//   tagsFrom  (doc, h) => [{ name, category }]  when selectors are not enough
//   underscores  true for booru sites, whose tag names use "_" instead of spaces
//   original  (doc, h, srcUrl) => URL of the full-size file for the post on this page
//   fullImage (url) => URL of the full-size file for a thumbnail URL (gallery pages)
//   title     (doc, h) => title for the card or comic
//
// Rules are best effort: sites change their markup. The generic fallback (meta keywords,
// rel="tag" links, og:site_name) still applies when a rule finds nothing.
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
      original: (doc) => doc.querySelector('#post-info-size a[href], .image-view-original-link')?.href
        ?? linkByText(doc, /^download$/i),
    },
    {
      name: 'Gelbooru',
      hosts: ['gelbooru.com'],
      underscores: true,
      tags: gelbooruTags,
      original: (doc) => linkByText(doc, /^original image$/i),
    },
    {
      name: 'Safebooru',
      hosts: ['safebooru.org'],
      underscores: true,
      tags: gelbooruTags,
      original: (doc) => linkByText(doc, /^original image$/i),
    },
    {
      name: 'Rule 34',
      hosts: ['rule34.xxx'],
      underscores: true,
      tags: gelbooruTags,
      original: (doc) => linkByText(doc, /^original image$/i),
    },
    {
      name: 'yande.re',
      hosts: ['yande.re'],
      underscores: true,
      tags: gelbooruTags,
      original: (doc) => doc.querySelector('a#png, a#highres, a.original-file-unchanged')?.href ?? null,
    },
    {
      name: 'Konachan',
      hosts: ['konachan.com', 'konachan.net'],
      underscores: true,
      tags: gelbooruTags,
      original: (doc) => doc.querySelector('a#png, a#highres, a.original-file-unchanged')?.href ?? null,
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
      original: (doc) => doc.querySelector('#image-download-link a, #image-container [data-file-url]')
        ?.closest('[data-file-url]')?.dataset.fileUrl ?? linkByText(doc, /^download$/i),
    },
    {
      name: 'Pixiv',
      hosts: ['pixiv.net'],
      tagsFrom: (doc, h) => [
        ...h.texts(doc, 'footer a[href*="/tags/"]').map((name) => ({ name, category: 'general' })),
        ...h.texts(doc, 'aside a[href*="/users/"] div[title], main a[href*="/users/"] h2')
          .slice(0, 1).map((name) => ({ name, category: 'artist' })),
      ],
      original: (doc) => doc.querySelector('a[href*="i.pximg.net/img-original"]')?.href ?? null,
      fullImage: (url) => url,
    },
    {
      name: 'X',
      hosts: ['x.com', 'twitter.com'],
      tagsFrom: (doc, h) => {
        const handle = location.pathname.split('/')[1];
        const tags = h.texts(doc, 'article a[href^="/hashtag/"]')
          .map((name) => ({ name: name.replace(/^#/, ''), category: 'general' }));
        if (handle && !['home', 'i', 'search', 'explore'].includes(handle))
          tags.unshift({ name: handle, category: 'artist' });
        return tags;
      },
      fullImage: (url) => /pbs\.twimg\.com\/media\//.test(url)
        ? url.replace(/([?&]name=)[^&]+/, '$1orig') : url,
      original: (doc, h, srcUrl) => /pbs\.twimg\.com\/media\//.test(srcUrl)
        ? srcUrl.replace(/([?&]name=)[^&]+/, '$1orig') : null,
    },
    {
      name: 'DeviantArt',
      hosts: ['deviantart.com'],
      tagsFrom: (doc, h) => [
        ...h.texts(doc, 'a[href*="deviantart.com/tag/"], a[href^="/tag/"]').map((name) => ({ name, category: 'general' })),
        ...h.texts(doc, 'a[data-hook="user_link"][data-username]').slice(0, 1)
          .map((name) => ({ name, category: 'artist' })),
      ],
    },
    {
      name: 'ArtStation',
      hosts: ['artstation.com'],
      tagsFrom: (doc, h) => [
        ...h.texts(doc, '.project-tags a, a[href*="/search?"][href*="query="]').map((name) => ({ name, category: 'general' })),
        ...h.texts(doc, '.project-author-name a, .artist-name a').slice(0, 1).map((name) => ({ name, category: 'artist' })),
      ],
    },
    {
      name: 'E-Hentai',
      hosts: ['e-hentai.org', 'exhentai.org'],
      title: (doc, h) => h.text(doc, '#gj') || h.text(doc, '#gn') || h.text(doc, '#i1 h1'),
      tagsFrom: (doc) => [...doc.querySelectorAll('#taglist a[id^="ta_"]')].map((a) => {
        const [namespace, ...rest] = a.id.slice(3).split(':');
        const name = (rest.length ? rest.join(':') : namespace).replace(/_/g, ' ');
        const category = { artist: 'artist', group: 'artist', parody: 'copyright', character: 'character' }[namespace]
          ?? 'general';
        return { name, category };
      }),
    },
    {
      name: 'nhentai',
      hosts: ['nhentai.net'],
      title: (doc, h) => h.text(doc, '#info h1.title') || h.text(doc, '#info h1'),
      tagsFrom: (doc) => [...doc.querySelectorAll('#tags .tag-container')].flatMap((container) => {
        const label = container.firstChild?.textContent?.trim().toLowerCase() ?? '';
        const category = label.startsWith('artist') || label.startsWith('group') ? 'artist'
          : label.startsWith('parod') ? 'copyright'
          : label.startsWith('character') ? 'character'
          : label.startsWith('tag') ? 'general' : null;
        if (!category) return [];
        return [...container.querySelectorAll('a .name')].map((el) => ({ name: el.textContent.trim(), category }));
      }),
      fullImage: (url) => url.replace(/\/\/t(\d*)\.nhentai\.net\/galleries\/(\d+)\/(\d+)t\.(\w+)/,
        '//i$1.nhentai.net/galleries/$2/$3.$4'),
    },
  ];
})();
