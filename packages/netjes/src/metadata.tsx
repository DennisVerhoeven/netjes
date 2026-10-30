// The Next.js Metadata API: `export const metadata` from pages and layouts, plus metadata sent by the C# loader
// (`.WithMetadata(...)`, the counterpart of generateMetadata). React 19 hoists the tags into <head>.

import { Fragment } from 'react';

/** A title, or a layout's `default` and `template` (`'%s · Site'`), or an `absolute` title that skips templates. */
export type MetadataTitle = string | { default?: string; template?: string; absolute?: string };

/** An image for a link preview: `og:image` with `og:image:width`, `og:image:height` and `og:image:alt` when given, or `twitter:image` with `twitter:image:alt`. */
export type MetadataImage = { url: string | URL; width?: number; height?: number; alt?: string };

/** One image or several, each a URL (a string or a `URL`) or a `MetadataImage`, as Next.js takes them. */
type MetadataImages = string | URL | MetadataImage | (string | URL | MetadataImage)[];

/** An image with its URL as a string, as the tags are written from it. */
type ResolvedImage = Omit<MetadataImage, 'url'> & { url: string };

/** The type of `export const metadata` in pages, layouts and special pages: the head tags a route sets, merged from the layouts down. */
export type Metadata = {
  title?: MetadataTitle;
  description?: string;
  keywords?: string[];
  authors?: { name: string; url?: string }[];
  robots?: string | { index?: boolean; follow?: boolean };
  alternates?: { canonical?: string; languages?: Record<string, string> };
  openGraph?: {
    title?: string;
    description?: string;
    url?: string;
    siteName?: string;
    type?: string;
    locale?: string;
    images?: MetadataImages;
  };
  twitter?: { card?: 'summary' | 'summary_large_image' | 'app' | 'player'; site?: string; creator?: string; title?: string; description?: string; images?: MetadataImages };
  icons?: { icon?: string; apple?: string };
  themeColor?: string;
  other?: Record<string, string>;
};

type OpenGraph = NonNullable<Metadata['openGraph']>;
type Twitter = NonNullable<Metadata['twitter']>;

/** The merged result: one title string, the images as a list, everything else merged field by field (later wins). */
export type ResolvedMetadata = Omit<Metadata, 'title' | 'openGraph' | 'twitter'> & {
  title?: string;
  openGraph?: Omit<OpenGraph, 'images'> & { images?: ResolvedImage[] };
  twitter?: Omit<Twitter, 'images'> & { images?: ResolvedImage[] };
};

function titleOf(title: MetadataTitle | undefined, template: string | undefined): string | undefined {
  if (title === undefined) return undefined;
  // A function replacer: a title such as "Save $& now" must not be read as a replacement pattern.
  if (typeof title === 'string') return template ? template.replace('%s', () => title) : title;
  if (title.absolute !== undefined) return title.absolute;
  return title.default;
}

/** The images as a list of descriptors with string URLs; like Next.js, a null image or one without a URL is left out. */
function imagesOf(images: MetadataImages): ResolvedImage[] {
  return (Array.isArray(images) ? images : [images])
    // The types allow no null, but an untyped page can pass one.
    .filter((image) => !!image)
    .map((image) => (typeof image === 'string' || image instanceof URL ? { url: String(image) } : { ...image, url: String(image.url ?? '') }))
    .filter((image) => image.url !== '');
}

function withImages<T extends { images?: MetadataImages }>(group: T): Omit<T, 'images'> & { images?: ResolvedImage[] } {
  const { images, ...rest } = group;
  return images == null ? rest : { ...rest, images: imagesOf(images) };
}

/**
 * Merges the layouts' metadata (outermost first), the page's, and the server's. A layout's `title.template` applies
 * to titles below it, `title.default` is used when nothing below sets one, `title.absolute` skips templates.
 */
export function resolveMetadata(layouts: readonly (Metadata | undefined)[], page: Metadata | undefined, server?: Metadata): ResolvedMetadata {
  let resolved: ResolvedMetadata = {};
  let template: string | undefined;
  for (const metadata of [...layouts, page, server]) {
    if (!metadata) continue;
    const { title, openGraph, twitter, alternates, icons, other, ...rest } = metadata;
    resolved = { ...resolved, ...rest };
    if (title !== undefined) {
      // A layout's own default is not put through its own template, only templates from above.
      const own = titleOf(title, typeof title === 'string' || title.default === undefined ? template : undefined);
      if (own !== undefined) resolved.title = own;
      if (typeof title === 'object' && title.template) template = title.template;
    }
    if (openGraph) resolved.openGraph = { ...resolved.openGraph, ...withImages(openGraph) };
    if (twitter) resolved.twitter = { ...resolved.twitter, ...withImages(twitter) };
    if (alternates) resolved.alternates = { ...resolved.alternates, ...alternates };
    if (icons) resolved.icons = { ...resolved.icons, ...icons };
    if (other) resolved.other = { ...resolved.other, ...other };
  }
  return fillTwitter(resolved);
}

/**
 * Next.js's last step, once everything is merged: with Open Graph or Twitter metadata, Twitter takes the title,
 * description and images it does not set from Open Graph, or the page's title (as its templates made it) and
 * description where there is no Open Graph one. Without a card it gets `summary_large_image` when its own images have
 * one, or with no twitter metadata at all the images it took, and `summary` otherwise.
 */
function fillTwitter(resolved: ResolvedMetadata): ResolvedMetadata {
  const { openGraph: og, twitter } = resolved;
  if (!og && !twitter) return resolved;
  const title = twitter?.title || og?.title || resolved.title;
  const description = twitter?.description || og?.description || resolved.description;
  const images = twitter?.images ?? og?.images;
  const card = twitter?.card ?? ((twitter ? twitter.images : images)?.length ? 'summary_large_image' : 'summary');
  return {
    ...resolved,
    twitter: {
      ...twitter,
      card,
      ...(title ? { title } : {}),
      ...(description ? { description } : {}),
      ...(images ? { images } : {}),
    },
  };
}

function robotsContent(robots: Metadata['robots']): string | undefined {
  if (robots === undefined || typeof robots === 'string') return robots;
  return `${robots.index === false ? 'noindex' : 'index'}, ${robots.follow === false ? 'nofollow' : 'follow'}`;
}

/** The tags for resolved metadata; rendered anywhere, React puts them in <head>. */
export function MetadataTags({ metadata }: { metadata: ResolvedMetadata }) {
  const og = metadata.openGraph;
  const twitter = metadata.twitter;
  const robots = robotsContent(metadata.robots);
  // As in Next.js, an empty Open Graph title or description counts as not set, and an empty tag is not written.
  const ogTitle = og ? og.title || metadata.title : undefined;
  const ogDescription = og ? og.description || metadata.description : undefined;
  return (
    <>
      {metadata.title !== undefined && <title>{metadata.title}</title>}
      {metadata.description !== undefined && <meta name="description" content={metadata.description} />}
      {metadata.keywords && <meta name="keywords" content={metadata.keywords.join(', ')} />}
      {metadata.authors?.map((author) => <meta key={author.name} name="author" content={author.name} />)}
      {robots && <meta name="robots" content={robots} />}
      {metadata.themeColor && <meta name="theme-color" content={metadata.themeColor} />}
      {metadata.alternates?.canonical && <link rel="canonical" href={metadata.alternates.canonical} />}
      {Object.entries(metadata.alternates?.languages ?? {}).map(([lang, href]) => (
        <link key={lang} rel="alternate" hrefLang={lang} href={href} />
      ))}
      {metadata.icons?.icon && <link rel="icon" href={metadata.icons.icon} />}
      {metadata.icons?.apple && <link rel="apple-touch-icon" href={metadata.icons.apple} />}
      {ogTitle && <meta property="og:title" content={ogTitle} />}
      {ogDescription && <meta property="og:description" content={ogDescription} />}
      {og?.url && <meta property="og:url" content={og.url} />}
      {og?.siteName && <meta property="og:site_name" content={og.siteName} />}
      {og?.type && <meta property="og:type" content={og.type} />}
      {og?.locale && <meta property="og:locale" content={og.locale} />}
      {og?.images?.map((image, index) => (
        <Fragment key={`${index}:${image.url}`}>
          <meta property="og:image" content={image.url} />
          {!!image.width && <meta property="og:image:width" content={String(image.width)} />}
          {!!image.height && <meta property="og:image:height" content={String(image.height)} />}
          {!!image.alt && <meta property="og:image:alt" content={image.alt} />}
        </Fragment>
      ))}
      {twitter?.card && <meta name="twitter:card" content={twitter.card} />}
      {twitter?.site && <meta name="twitter:site" content={twitter.site} />}
      {twitter?.creator && <meta name="twitter:creator" content={twitter.creator} />}
      {twitter?.title && <meta name="twitter:title" content={twitter.title} />}
      {twitter?.description && <meta name="twitter:description" content={twitter.description} />}
      {twitter?.images?.map((image, index) => (
        <Fragment key={`${index}:${image.url}`}>
          <meta name="twitter:image" content={image.url} />
          {!!image.alt && <meta name="twitter:image:alt" content={image.alt} />}
        </Fragment>
      ))}
      {Object.entries(metadata.other ?? {}).map(([name, content]) => (
        <meta key={name} name={name} content={content} />
      ))}
    </>
  );
}
