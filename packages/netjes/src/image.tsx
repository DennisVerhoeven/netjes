import { type ImgHTMLAttributes, createContext, useContext } from 'react';
import { preload } from 'react-dom';

/**
 * Where the optimizer lives, and the widths and qualities it serves: Netjes:Images on the server, sent with the first
 * document. Without `qualities`, any quality is requested as given.
 */
export type ImageConfig = { path: string; widths: readonly number[]; qualities?: readonly number[]; basePath?: string };

/** Next.js's device and image sizes; the server's Netjes:Images:Widths default to the same. */
const DEFAULT_CONFIG: ImageConfig = {
  path: '/_netjes/image',
  widths: [16, 32, 48, 64, 96, 128, 256, 384, 640, 750, 828, 1080, 1200, 1920, 2048, 3840],
};

const ImageConfigContext = createContext<ImageConfig>(DEFAULT_CONFIG);

export const ImageConfigProvider = ImageConfigContext.Provider;

/** The props of `<Image>`: an `<img>`'s own, with a required size and `alt`, and the optimizer's options. */
export type ImageProps = Omit<ImgHTMLAttributes<HTMLImageElement>, 'src' | 'srcSet' | 'width' | 'height' | 'loading' | 'alt'> & {
  /** A path in `public/` (`/img/hero.png`) or a URL on a host in Netjes:Images:RemoteHosts. */
  src: string;
  /** Required, as for any image; `""` for decorative ones. */
  alt: string;
  /** The intrinsic size in CSS pixels; reserves space so the page does not shift as it loads. */
  width: number;
  height: number;
  /** For responsive layouts, e.g. `(max-width: 768px) 100vw, 50vw`: the browser then picks from every width. */
  sizes?: string;
  /** 1–100. Default 75. Rounded to the nearest of Netjes:Images:Qualities. */
  quality?: number;
  /** Above the fold: load eagerly with high priority, and preload it from the document head. */
  priority?: boolean;
  /** Serve `src` as is. SVGs and data URLs always are. */
  unoptimized?: boolean;
};

function optimizerUrl(config: ImageConfig, src: string, width: number, quality: number) {
  return `${config.path}?url=${encodeURIComponent(src)}&w=${width}&q=${quality}`;
}

/** The accepted quality nearest to the one asked for (the higher one on a tie). */
function qualityFor(qualities: readonly number[] | undefined, wanted: number) {
  if (!qualities?.length) return wanted;
  return qualities.reduce((best, q) => (Math.abs(q - wanted) < Math.abs(best - wanted) || (Math.abs(q - wanted) === Math.abs(best - wanted) && q > best) ? q : best));
}

/** The smallest configured width that covers `target`, or the largest there is. */
function widthFor(widths: readonly number[], target: number) {
  return widths.find((w) => w >= target) ?? widths[widths.length - 1] ?? target;
}

function underBasePath(src: string, basePath: string | undefined) {
  return basePath && src.startsWith('/') && !src.startsWith('//') && !src.startsWith(`${basePath}/`) ? basePath + src : src;
}

function isUnoptimizable(src: string) {
  return src.startsWith('data:') || src.startsWith('blob:') || /\.svg($|[?#])/i.test(src);
}

/**
 * Like `next/image`: an `<img>` with width and height (no layout shift), lazy loading, and a `srcset` of resized
 * copies from the `/_netjes/image` endpoint, so each screen downloads only what it needs.
 */
export function Image({ src, alt, width, height, sizes, quality = 75, priority = false, unoptimized = false, ...rest }: ImageProps) {
  const config = useContext(ImageConfigContext);
  const common = {
    ...rest,
    alt,
    width,
    height,
    decoding: 'async' as const,
    loading: priority ? ('eager' as const) : ('lazy' as const),
    ...(priority ? { fetchPriority: 'high' as const } : {}),
  };
  // Served as is, from public/: under the app's base path. (The optimizer's `url` stays a public/ path.)
  if (unoptimized || isUnoptimizable(src)) return <img {...common} src={underBasePath(src, config.basePath)} />;

  const widths = [...config.widths].sort((a, b) => a - b);
  const q = qualityFor(config.qualities, quality);
  // Responsive: every width, the browser picks by `sizes`. Fixed: 1x and 2x, each once.
  const chosen = sizes ? widths : [...new Set([widthFor(widths, width), widthFor(widths, width * 2)])];
  const srcSet = chosen
    .map((w, i) => `${optimizerUrl(config, src, w, q)} ${sizes ? `${w}w` : `${i + 1}x`}`)
    .join(', ');
  const fallback = optimizerUrl(config, src, chosen[chosen.length - 1] ?? width, q);
  if (priority) preload(fallback, { as: 'image', imageSrcSet: srcSet, ...(sizes ? { imageSizes: sizes } : {}), fetchPriority: 'high' });

  return <img {...common} sizes={sizes} srcSet={srcSet} src={fallback} />;
}
