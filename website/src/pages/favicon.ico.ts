import type { APIRoute } from 'astro';
// The app's own multi-size icon, inlined so the build doesn't depend on file paths. Browsers and crawlers ask for
// /favicon.ico whatever the pages link; the pages themselves link the PNG sizes (layouts/Base.astro).
import dataUrl from '../../../src/Fluorite.App/Assets/AppIcon.ico?inline';

export const GET: APIRoute = () =>
    new Response(Buffer.from(dataUrl.slice(dataUrl.indexOf(',') + 1), 'base64'), {
        headers: { 'Content-Type': 'image/x-icon' },
    });
