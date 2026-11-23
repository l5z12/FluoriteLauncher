import { rename, rmdir } from 'node:fs/promises';
import { satteri } from '@astrojs/markdown-satteri';
import type { AstroIntegration } from 'astro';
import { defineConfig } from 'astro/config';
import { repoDocs } from './src/markdown/repo-docs';

// Cloudflare answers a missing path with the nearest 404.html, so the Chinese 404 page has to be zh-hans/404.html.
// Astro only names the root 404 page that way; it builds src/pages/zh-hans/404.astro as zh-hans/404/index.html.
const nestedNotFoundPages: AstroIntegration = {
    name: 'nested-404-pages',
    hooks: {
        'astro:build:done': async ({ dir }) => {
            await rename(new URL('zh-hans/404/index.html', dir), new URL('zh-hans/404.html', dir));
            await rmdir(new URL('zh-hans/404/', dir));
        },
    },
};

export default defineConfig({
    site: 'https://fluorite.l5z12.dev',
    integrations: [nestedNotFoundPages],
    build: {
        // One cacheable stylesheet instead of <style> blocks in every page.
        inlineStylesheets: 'never',
    },
    markdown: {
        processor: satteri({ hastPlugins: [repoDocs] }),
    },
    vite: {
        // The pages import PRIVACY.md, TERMS.md and the app's logo from the repository root.
        server: { fs: { allow: ['..'] } },
        // Small logos stay files rather than data: URIs, which the Content-Security-Policy (public/_headers) blocks.
        build: { assetsInlineLimit: 0 },
    },
});
