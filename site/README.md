# Ntilde site

Marketing and documentation landing page for [Ntilde](../README.md),
hosted on GitHub Pages.

- **Source:** [`site/`](./) (Astro + Tailwind, single-page with anchor sections)
- **Output:** static HTML/CSS/JS, no server runtime
- **Deploy:** GitHub Actions → Pages (see [`.github/workflows/pages.yml`](../.github/workflows/pages.yml))
- **Canonical screenshots:** [`docs/assets/screenshots/`](../docs/assets/screenshots/)
  — kept in git, synced into the build by `scripts/sync-screenshots.mjs`
- **User guide:** [`docs/user/`](../docs/user/) — published at `/docs/`, synced
  into `src/content/docs/` by `scripts/sync-docs.mjs`
- **Blog:** [`src/content/posts/`](./src/content/posts/) — published at `/blog/`;
  written here, not synced from anywhere

## User guide

The pages under `/docs/` are [`docs/user/`](../docs/user/) as it is on `main`;
edit them there, not in `site/`. `scripts/sync-docs.mjs` copies them into the
`docs` content collection before every dev server and build, adding the title,
description and sidebar place (from the order and `##` sections of
`docs/user/README.md`). The remark plugin in `astro.config.mjs` turns links
between guide pages into `/docs/<page>/` routes and links to other repository
files into GitHub URLs.

Because the site follows `main`, every page says that it describes the
development version and names the latest release: the newest `vX.Y.Z` tag,
which the Pages workflow finds and passes in `NTILDE_LATEST_RELEASE`. Set it
yourself to see the same notice locally
(`NTILDE_LATEST_RELEASE=v0.12.0 npm run dev`); without it, the notice links to
the releases list.

## Qualification results

`/qualification/` shows Ntilde's own Core qualification from one frozen publication under
[`docs/qa/terminfo/`](../docs/qa/terminfo/). It has no results of its own: every row, total and
identity field comes from that publication's `report.json`.

- [`qualification-source.json`](./qualification-source.json) is the pin: which publication, the
  SHA-256 of its report, readable report, measurement manifest and requirements, the run and
  revisions it must name, and the totals it must have. Showing a newer result is an explicit edit
  to this file (the self-test fails while a newer Core publication exists and the pin has not
  moved).
- [`scripts/qualification-data.mjs`](./scripts/qualification-data.mjs) builds the page data and
  refuses a changed, missing or malformed source; `scripts/sync-qualification.mjs` writes it to the
  git-ignored `src/data/ntilde-qualification.json` before every dev server, build and check.
- `npm run build` ends by checking the built page (`npm run check:build` repeats that on an existing
  `dist/`): the pinned rows and totals, the claim, that links resolve and that nothing
  machine-specific is in the HTML. The Pages workflow builds with it, so a bad page is not deployed.
- The generator's tests are
  [`scripts/terminfo/site-qualification-data.test.mjs`](../scripts/terminfo/site-qualification-data.test.mjs),
  which CI runs with the other terminfo self-tests. CI does not build the site on pull requests; the
  Pages workflow builds it from `main`.

To move the page to a newer publication, point `publication`, `publicationScript` and `evidenceRef`
at it and update the digests and expectations; the build and the test then say what is wrong.

## Develop

```bash
cd site
npm install
npm run dev      # http://localhost:4321
npm run check    # sync the guide, then type-check
```

The `predev` hook runs `scripts/sync-screenshots.mjs` automatically, so any
file you drop into `docs/assets/screenshots/` shows up at
`/screenshots/<name>` on the dev server.

## Build

```bash
cd site
npm run build    # -> site/dist/
npm run preview  # serve site/dist/ locally
```

The build is pure static output: `index.html`, `_astro/*` (hashed CSS/JS),
`favicon.svg`, `og.svg`, `screenshots/*`, and `sitemap-index.xml`.

## Deploy

The workflow in [`.github/workflows/pages.yml`](../.github/workflows/pages.yml)
runs on every push to `main`:

1. Installs Node 20 + `site/` dependencies (`npm ci`)
2. Runs `npm run build` (with `prebuild` syncing screenshots first)
3. Uploads `site/dist/` as a Pages artifact
4. Publishes via `actions/deploy-pages@v4`

### One-time repo setup

1. **Settings → Pages → Source:** *GitHub Actions* (not "Deploy from a branch").
2. (Optional) **Settings → Pages → Custom domain:** point at your domain, then
   add a `CNAME` file under `site/public/` containing only the domain.
3. (Optional) **Settings → Secrets and variables → Variables:**
   - `ASTRO_SITE` — canonical origin (e.g. `https://ntilde.dev`).
     Default: `https://benyblack.github.io`.
   - `ASTRO_BASE` — path prefix Astro adds to every URL. Default:
     `/ntilde`. Set to empty for a custom-domain deployment.
   The workflow forwards both to the Astro build.

## Stack

- [Astro 4](https://astro.build/) — static-site framework, no client JS by
  default, ships only the bytes the page needs.
- [Tailwind CSS 3](https://tailwindcss.com/) — design tokens live in
  [`tailwind.config.mjs`](./tailwind.config.mjs); base styles in
  [`src/styles/global.css`](./src/styles/global.css).
- [`@astrojs/sitemap`](https://docs.astro.build/en/guides/integrations-guide/sitemap/)
  — emits `/sitemap-index.xml` so search engines can find the page.

## Blog

Posts live in [`src/content/posts/`](./src/content/posts/), one folder per post:
`index.md` plus anything it embeds, linked relatively (`![...](./chart.svg)`). The
folder name is the URL: `src/content/posts/my-post/` is `/blog/my-post/`. Each post
starts with this frontmatter (the `posts` schema in
[`src/content/config.ts`](./src/content/config.ts) checks it at build time):

```yaml
---
title: "How we cut ntilde's CI time by 60%"
description: "One or two sentences for the post list and link previews."
date: 2026-11-01
author: Behnam
draft: false   # optional; true keeps it out of the published site
---
```

The page renders the title, so the body starts below it. An image followed by a
paragraph that is only italic text shows that text as the image's caption. A draft
shows up in `npm run dev` but not in `npm run build`. In Markdown links, write other
pages of the site from the site root (`[SSH](/docs/ssh/)`, `[Install](/#install)`);
the build adds the base path. Raw HTML `<a>` tags are not rewritten.

## Editing content

- Page sections live in [`src/components/`](./src/components/). Each section
  is its own file so it's easy to reorder or hide without touching layout.
- The base layout (head, nav, footer) is in
  [`src/layouts/Base.astro`](./src/layouts/Base.astro).
- Brand colors and font stacks are defined in
  [`tailwind.config.mjs`](./tailwind.config.mjs) under
  `theme.extend.colors.{ink,ntilde}` and `theme.extend.fontFamily`.

## File map

```
site/
├── astro.config.mjs           # Astro config (site URL, integrations)
├── tailwind.config.mjs        # Design tokens
├── package.json               # Dependencies + scripts
├── public/                    # Served verbatim at the site root
│   ├── favicon.svg
│   ├── og.svg
│   ├── robots.txt
│   └── screenshots/           # Synced from docs/assets/screenshots/
├── qualification-source.json  # Pins the publication /qualification/ shows
├── scripts/
│   ├── sync-screenshots.mjs   # Copies docs/assets/screenshots -> public/
│   ├── qualification-data.mjs # Builds and validates the /qualification/ data
│   ├── sync-qualification.mjs # Writes src/data/ntilde-qualification.json (generated)
│   └── check-qualification-build.mjs # Checks the built /qualification/ page
├── src/
│   ├── components/            # One Astro file per page section
│   ├── layouts/
│   │   └── Base.astro
│   ├── content/
│   │   ├── config.ts          # Collection schemas (docs, posts)
│   │   └── posts/             # Blog posts, one folder each
│   ├── pages/
│   │   ├── index.astro        # Single-page entrypoint
│   │   ├── docs/              # The user guide
│   │   └── blog/              # The post list and one page per post
│   └── styles/
│       └── global.css         # Tailwind layers + design tokens
└── README.md
```

## License

Same as the project: [MIT](../LICENSE).
