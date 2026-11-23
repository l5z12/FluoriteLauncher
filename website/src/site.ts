export const repo = 'https://github.com/l5z12/FluoriteLauncher';

/** A file in the repository on GitHub, e.g. `blob('LICENSE')`. */
export const blob = (path: string) => `${repo}/blob/main/${path}`;

/** The commits that changed a file in the repository. */
export const history = (path: string) => `${repo}/commits/main/${path}`;

/**
 * The Microsoft Store listing. Set it when Fluorite is released: Home then offers the Store instead of the
 * "not released yet" note, and the FAQ says where to get it.
 */
export const storeUrl: string | undefined = undefined;
