import type { APIRoute } from 'astro';
import { type Lang, locales } from '../i18n';

// One sitemap for the whole site: served live by `pnpm dev`, written to dist/sitemap.xml by `pnpm build`.
// It lists every page in src/pages (except the 404 pages), so a new page shows up without touching this file.
// A page and its /zh-hans/ twin list each other as alternates, as their <link rel="alternate" hreflang> do.

const ZH = '/zh-hans';

/** URL path of a page file: './index.astro' → '/', './privacy.astro' → '/privacy/', './zh-hans/index.astro' → '/zh-hans/'. */
const toPath = (file: string) => {
    const path = `/${file.slice(2).replace(/\.astro$/, '').replace(/(^|\/)index$/, '')}`;
    return path.endsWith('/') ? path : `${path}/`;
};

const paths = Object.keys(import.meta.glob('./**/*.astro'))
    .filter((file) => !/(^|\/)404\.astro$/.test(file))
    .map(toPath);

// Each page's versions, keyed by its English path.
const pages = new Map<string, Partial<Record<Lang, string>>>();
for (const path of paths) {
    const lang: Lang = path === `${ZH}/` || path.startsWith(`${ZH}/`) ? 'zh-hans' : 'en';
    const key = lang === 'zh-hans' ? path.slice(ZH.length) : path;
    pages.set(key, { ...pages.get(key), [lang]: path });
}

export const GET: APIRoute = ({ site }) => {
    const abs = (path: string) => new URL(path, site).href;
    const entries = [...pages.values()].flatMap((versions) => {
        const list = Object.entries(versions) as [Lang, string][];
        const alternates =
            list.length > 1
                ? [
                      ...list.map(([lang, path]) => `<xhtml:link rel="alternate" hreflang="${locales[lang].htmlLang}" href="${abs(path)}"/>`),
                      `<xhtml:link rel="alternate" hreflang="x-default" href="${abs(versions.en ?? list[0][1])}"/>`,
                  ]
                : [];
        return list.map(([, path]) => `  <url><loc>${abs(path)}</loc>${alternates.join('')}</url>`);
    });
    const xml = [
        '<?xml version="1.0" encoding="UTF-8"?>',
        '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml">',
        ...entries,
        '</urlset>',
        '',
    ].join('\n');
    return new Response(xml, { headers: { 'Content-Type': 'application/xml; charset=utf-8' } });
};
