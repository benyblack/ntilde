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

export const collections = { docs };
