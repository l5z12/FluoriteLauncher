import type { HastPluginDefinition } from 'satteri';
import { blob } from '../site';

// A Sätteri hast plugin for the repository's own Markdown (PRIVACY.md, TERMS.md), which is written for GitHub:
// - repo-relative links point at the site's page for that document, or at the file on GitHub;
// - h2/h3 headings link to themselves, so a section can be shared;
// - tables sit in a scroller, so a wide table doesn't widen the page on a phone.

const sitePages: Record<string, string> = { 'PRIVACY.md': '/privacy/', 'TERMS.md': '/terms/' };

// GitHub's heading slugs for plain text: lower case, punctuation dropped, spaces to hyphens. Astro's heading-ids
// plugin runs after this one and keeps an id that's already set, so the table of contents gets these same slugs.
const slugify = (text: string) =>
    text
        .trim()
        .toLowerCase()
        .replace(/[^\p{L}\p{N}\s_-]/gu, '')
        .replace(/\s/g, '-');

/** A factory, so each document gets its own slug counts. */
export function repoDocs(): HastPluginDefinition {
    const seen = new Map<string, number>();
    return {
        name: 'repo-docs',
        element: [
            {
                filter: ['a'],
                visit(node, ctx) {
                    const href = node.properties.href;
                    // Leave absolute URLs (any scheme), in-page anchors and site paths alone.
                    if (typeof href !== 'string' || /^([a-z][a-z\d+.-]*:|#|\/)/i.test(href)) return;
                    const [path, hash] = href.split('#');
                    const page = sitePages[path];
                    ctx.setProperty(node, 'href', page ? page + (hash ? `#${hash}` : '') : blob(href));
                },
            },
            {
                filter: ['h2', 'h3'],
                visit(node, ctx) {
                    let slug = slugify(ctx.textContent(node));
                    const count = seen.get(slug) ?? 0;
                    seen.set(slug, count + 1);
                    if (count) slug += `-${count}`;
                    ctx.setProperty(node, 'id', slug);
                    ctx.setField(node, 'children', [
                        {
                            type: 'element',
                            tagName: 'a',
                            properties: { href: `#${slug}`, className: ['heading-link'] },
                            children: node.children,
                        },
                    ]);
                },
            },
            {
                filter: ['table'],
                visit(node, ctx) {
                    ctx.wrapNode(node, { raw: '<div class="table-wrap"></div>' });
                },
            },
        ],
    };
}
