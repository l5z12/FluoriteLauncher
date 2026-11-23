import { blob, repo, storeUrl } from '../site';
import en from './en.json';
import zhHans from './zh-hans.json';

// The site's text lives in one JSON file per language. Code keeps what isn't text: URLs, icons and the order items
// appear in. Strings may hold HTML (rendered with set:html; they're ours, never user input), and links in them use
// {placeholders} that `fill` swaps for the URLs below.

export type Lang = 'en' | 'zh-hans';
export type Strings = typeof en;

export const locales: Record<Lang, { htmlLang: string; home: string; ogLocale: string; name: string }> = {
    en: { htmlLang: 'en', home: '/', ogLocale: 'en_US', name: 'English' },
    'zh-hans': { htmlLang: 'zh-Hans', home: '/zh-hans/', ogLocale: 'zh_CN', name: '简体中文' },
};

// Each file must have exactly the other's keys, so a missing or stray translation fails `pnpm check`.
export const strings: Record<Lang, Strings> = { en, 'zh-hans': zhHans satisfies Strings };
en satisfies typeof zhHans;

/** The other language, for the switch in the header. */
export const otherLang = (lang: Lang): Lang => (lang === 'en' ? 'zh-hans' : 'en');

const urls: Record<string, string | undefined> = {
    license: blob('LICENSE'),
    contributing: blob('CONTRIBUTING.md'),
    issues: `${repo}/issues`,
    security: `${repo}/security/policy`,
    store: storeUrl,
};

/** Replaces each {placeholder} in a string with its URL. An unknown placeholder fails the build. */
export function fill(text: string): string {
    return text.replace(/\{(\w+)\}/g, (match, key: string) => {
        const url = urls[key];
        if (!url) throw new Error(`No URL for ${match} in: ${text}`);
        return url;
    });
}
