import icon from '../../src/Fluorite.App/Assets/Square44x44Logo.targetsize-256_altform-unplated.png?url';
import { type Lang, locales, strings } from './i18n';
import { repo, storeUrl } from './site';

/** schema.org data for a home page: the site (search engines show its name) and the app it's about. */
export function homeStructuredData(lang: Lang, site: URL) {
    return {
        '@context': 'https://schema.org',
        '@graph': [
            {
                '@type': 'WebSite',
                '@id': `${site.href}#website`,
                name: 'Fluorite Launcher',
                url: site.href,
                inLanguage: Object.values(locales).map((l) => l.htmlLang),
            },
            {
                '@type': 'SoftwareApplication',
                '@id': `${site.href}#app`,
                name: 'Fluorite Launcher',
                description: strings[lang].description,
                url: new URL(locales[lang].home, site).href,
                image: new URL(icon, site).href,
                applicationCategory: 'GameApplication',
                operatingSystem: 'Windows 10, Windows 11',
                license: 'https://www.gnu.org/licenses/gpl-3.0.html',
                isAccessibleForFree: true,
                offers: { '@type': 'Offer', price: '0', priceCurrency: 'USD' },
                sameAs: [repo],
                inLanguage: locales[lang].htmlLang,
                ...(storeUrl ? { downloadUrl: storeUrl } : {}),
            },
        ],
    };
}

const months = ['january', 'february', 'march', 'april', 'may', 'june', 'july', 'august', 'september', 'october', 'november', 'december'];

/** The "Last updated: 22 November 2026" line of PRIVACY.md or TERMS.md, as an ISO date (2026-11-22). */
export function lastUpdated(markdown: string): string | undefined {
    const match = /^Last updated: (\d{1,2}) ([A-Za-z]+) (\d{4})\s*$/m.exec(markdown);
    const month = match ? months.indexOf(match[2].toLowerCase()) + 1 : 0;
    if (!match || !month) return undefined;
    return `${match[3]}-${String(month).padStart(2, '0')}-${match[1].padStart(2, '0')}`;
}
