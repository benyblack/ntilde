import { defineConfig } from 'astro/config';
import tailwind from '@astrojs/tailwind';
import sitemap from '@astrojs/sitemap';
import path from 'node:path';

// `site` is the canonical origin (no trailing path) used for the sitemap
// and OpenGraph URLs. `base` is the path prefix Astro adds to every page
// and asset. Defaults assume a project Pages site at `/Ntilde/`.
// The sentinel `__default__` (substituted by the workflow when the
// variable is unset) means "use the default". An empty value means
// "explicitly no prefix" — used for custom-domain deployments.
const DEFAULT_SITE = 'https://benyblack.github.io';
const DEFAULT_BASE = '/ntilde';
const USE_DEFAULT = '__default__';
const resolveEnv = (name, fallback) => {
  const v = process.env[name];
  if (v === undefined || v === USE_DEFAULT) return fallback;
  return v;
};
const site = resolveEnv('ASTRO_SITE', DEFAULT_SITE);
const base = resolveEnv('ASTRO_BASE', DEFAULT_BASE);

const REPO_URL = 'https://github.com/benyblack/ntilde';

/**
 * The user guide's links are written for GitHub (`ssh.md#ssh-backends`,
 * `../../CHANGELOG.md`). On the site, a link to another guide page goes to its
 * route under /docs/, and a link to anything else in the repository goes to
 * that file on GitHub. Only pages scripts/sync-docs.mjs copied into
 * src/content/docs/ are touched; their links are relative to docs/user/.
 */
function userGuideLinks() {
  const docsBase = `${base.replace(/\/$/, '')}/docs/`;
  const rewrite = (url) => {
    if (!url || /^[a-z][a-z0-9+.-]*:/i.test(url) || url.startsWith('#') || url.startsWith('/')) return url;
    const [target, fragment] = url.split('#', 2);
    const hash = fragment === undefined ? '' : `#${fragment}`;
    const resolved = path.posix.normalize(`docs/user/${target}`);
    if (resolved.startsWith('../')) return url;
    const page = /^docs\/user\/([\w-]+)\.md$/.exec(resolved);
    if (page) return `${docsBase}${page[1] === 'README' ? '' : `${page[1]}/`}${hash}`;
    const kind = target.endsWith('/') ? 'tree' : 'blob';
    return `${REPO_URL}/${kind}/main/${resolved}${hash}`;
  };
  // GFM turns placeholders such as `user@host` in running text into mailto: links.
  const isPlaceholderEmail = (node) =>
    node.type === 'link' && /^mailto:[^@]+@[^.]+$/.test(node.url ?? '');
  const walk = (node) => {
    if (node.type === 'link' || node.type === 'definition') node.url = rewrite(node.url);
    if (node.children) {
      node.children = node.children.flatMap((child) => (isPlaceholderEmail(child) ? child.children : [child]));
      node.children.forEach(walk);
    }
  };
  return () => (tree, file) => {
    const where = (file.path ?? file.history?.[0] ?? '').split(path.sep).join('/');
    if (where.includes('/src/content/docs/')) walk(tree);
  };
}

// https://astro.build/config
export default defineConfig({
  site,
  base,
  trailingSlash: 'ignore',
  integrations: [
    tailwind({
      // We use a dedicated base CSS file (src/styles/global.css) so the
      // Tailwind directives are explicit; this keeps `apply` and component
      // classes in one place.
      applyBaseStyles: false,
    }),
    sitemap(),
  ],
  build: {
    // Inline tiny stylesheets so the page renders with a single round-trip
    // on the first paint; the rest of the build output is small.
    inlineStylesheets: 'auto',
  },
  compressHTML: true,
  markdown: {
    remarkPlugins: [userGuideLinks()],
  },
});
