import { render, screen } from '@testing-library/react';
import { renderToString } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { Image, ImageConfigProvider } from '../src/image.tsx';

const optimized = (src: string, w: number, q = 75) => `/_netjes/image?url=${encodeURIComponent(src)}&w=${w}&q=${q}`;

describe('Image', () => {
  it('serves a fixed-size image at 1x and 2x through the optimizer, lazily', () => {
    render(<Image src="/img/hero.png" width={400} height={200} alt="Hero" />);

    const img = screen.getByRole('img', { name: 'Hero' });
    expect(img.getAttribute('srcset')).toBe(`${optimized('/img/hero.png', 640)} 1x, ${optimized('/img/hero.png', 828)} 2x`);
    expect(img.getAttribute('src')).toBe(optimized('/img/hero.png', 828));
    expect(img.getAttribute('width')).toBe('400');
    expect(img.getAttribute('height')).toBe('200');
    expect(img.getAttribute('loading')).toBe('lazy');
    expect(img.getAttribute('decoding')).toBe('async');
  });

  it('offers every configured width when the layout is responsive (sizes)', () => {
    render(
      <ImageConfigProvider value={{ path: '/_netjes/image', widths: [320, 640, 1280] }}>
        <Image src="/a.jpg" width={1280} height={720} sizes="(max-width: 768px) 100vw, 50vw" quality={60} alt="A" />
      </ImageConfigProvider>,
    );

    const img = screen.getByRole('img', { name: 'A' });
    expect(img.getAttribute('srcset')).toBe(
      [320, 640, 1280].map((w) => `${optimized('/a.jpg', w, 60)} ${w}w`).join(', '),
    );
    expect(img.getAttribute('sizes')).toBe('(max-width: 768px) 100vw, 50vw');
  });

  it('rounds the quality to the nearest one the server accepts', () => {
    render(
      <ImageConfigProvider value={{ path: '/_netjes/image', widths: [640], qualities: [50, 75, 90] }}>
        <Image src="/a.jpg" width={640} height={360} quality={60} alt="Sixty" />
        <Image src="/a.jpg" width={640} height={360} quality={85} alt="EightyFive" />
      </ImageConfigProvider>,
    );

    expect(screen.getByRole('img', { name: 'Sixty' }).getAttribute('src')).toBe(optimized('/a.jpg', 640, 50));
    expect(screen.getByRole('img', { name: 'EightyFive' }).getAttribute('src')).toBe(optimized('/a.jpg', 640, 90));
  });

  it('puts files it does not optimize under the base path, and leaves the optimizer source as a public path', () => {
    render(
      <ImageConfigProvider value={{ path: '/base/_netjes/image', widths: [640], basePath: '/base' }}>
        <Image src="/logo.svg" width={64} height={64} alt="Logo" />
        <Image src="/img/a.png" width={64} height={64} alt="Raw" unoptimized />
        <Image src="https://cdn.example.com/b.png" width={64} height={64} alt="Remote" unoptimized />
        <Image src="/img/hero.png" width={640} height={320} alt="Hero" />
      </ImageConfigProvider>,
    );

    expect(screen.getByRole('img', { name: 'Logo' }).getAttribute('src')).toBe('/base/logo.svg');
    expect(screen.getByRole('img', { name: 'Raw' }).getAttribute('src')).toBe('/base/img/a.png');
    expect(screen.getByRole('img', { name: 'Remote' }).getAttribute('src')).toBe('https://cdn.example.com/b.png');
    expect(screen.getByRole('img', { name: 'Hero' }).getAttribute('src')).toBe('/base/_netjes/image?url=%2Fimg%2Fhero.png&w=640&q=75');
  });

  it('loads priority images eagerly, with high fetch priority', () => {
    render(<Image src="/img/hero.png" width={400} height={200} alt="Hero" priority />);

    const img = screen.getByRole('img', { name: 'Hero' });
    expect(img.getAttribute('loading')).toBe('eager');
    expect(img.getAttribute('fetchpriority')).toBe('high');
  });

  it('preloads priority images from the document head when server-rendered', () => {
    const html = renderToString(
      <html lang="en">
        <head />
        <body>
          <Image src="/img/hero.png" width={400} height={200} alt="Hero" priority />
        </body>
      </html>,
    );

    const head = html.slice(html.indexOf('<head>'), html.indexOf('</head>'));
    expect(head).toContain('rel="preload"');
    expect(head).toContain('as="image"');
    expect(head.toLowerCase()).toContain(`imagesrcset="${optimized('/img/hero.png', 640).replaceAll('&', '&amp;')} 1x`.toLowerCase());
  });

  it.each([
    ['an SVG', '/logo.svg', {}],
    ['a data URL', 'data:image/png;base64,AAAA', {}],
    ['unoptimized', '/img/hero.png', { unoptimized: true }],
  ])('uses the source as is for %s', (_, src, extra) => {
    render(<Image src={src} width={40} height={40} alt="Plain" {...extra} />);

    const img = screen.getByRole('img', { name: 'Plain' });
    expect(img.getAttribute('src')).toBe(src);
    expect(img.getAttribute('srcset')).toBeNull();
  });

  it('passes other img attributes through', () => {
    render(<Image src="/img/hero.png" width={400} height={200} alt="Hero" className="rounded" style={{ objectFit: 'cover' }} />);

    const img = screen.getByRole('img', { name: 'Hero' });
    expect(img.className).toBe('rounded');
    expect(img.style.objectFit).toBe('cover');
  });
});
