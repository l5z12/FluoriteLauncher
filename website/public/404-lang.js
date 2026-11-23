// Loaded by the English 404 page only. A browser that prefers Chinese goes to the same path under /zh-hans/,
// which Cloudflare answers with the Chinese 404 page (zh-hans/404.html, the nearest one to that path).
(() => {
    const preferred = (navigator.languages || [navigator.language])
        .map((tag) => tag.toLowerCase().split('-')[0])
        .find((lang) => lang === 'zh' || lang === 'en');
    if (preferred === 'zh' && !location.pathname.startsWith('/zh-hans/')) {
        location.replace(`/zh-hans${location.pathname}${location.search}${location.hash}`);
    }
})();
