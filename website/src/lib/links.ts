// A site path under the configured base (astro.config.mjs `base`), so a component's links follow the one place
// the site's address is set: page('/guides/access-rules/') is '/guides/access-rules/' at the root and
// '/<base>/guides/access-rules/' under a base. Markdown links are written root-relative; the links validator fails
// the build on any that would miss a base.
const prefix = import.meta.env.BASE_URL.replace(/\/$/, '');

export const page = (path: `/${string}`): string => `${prefix}${path}`;
