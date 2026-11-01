import { defineCollection, z } from 'astro:content';

// The user guide, copied from docs/user/ by scripts/sync-docs.mjs, which writes this frontmatter.
const docs = defineCollection({
  type: 'content',
  schema: z.object({
    title: z.string(),
    navTitle: z.string(),
    description: z.string(),
    group: z.string(),
    order: z.number(),
    /** The file's name in docs/user/, for the "Edit on GitHub" link. */
    source: z.string(),
  }),
});

// Blog posts. Unlike the user guide, these are written for the site, so they live here
// (src/content/posts/) instead of being copied in from docs/. A post is a folder holding
// index.md and anything it embeds, which the post links relatively (./chart.svg).
const posts = defineCollection({
  type: 'content',
  schema: z.object({
    title: z.string(),
    description: z.string(),
    date: z.coerce.date(),
    author: z.string(),
    /** Drafts build locally but are left out of the list and of the published site. */
    draft: z.boolean().default(false),
  }),
});

export const collections = { docs, posts };
