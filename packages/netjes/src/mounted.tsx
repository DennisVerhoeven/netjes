import { type ReactNode, useMemo } from 'react';
import { Form, type FormAction, type FormProps, SubmitButton, type SubmitButtonProps } from './form.tsx';
import { type Href, isHrefShape } from './href.ts';
import { Link, type LinkProps } from './link.tsx';
import { isDotSegment } from './payload.ts';
import { type Router, useHref, usePagePayload, usePathname, useRouter } from './router.tsx';

/** The props of a page package's `<Link>`: those of the core `<Link>`, with an href below the package's mount. */
export type MountedLinkProps<THref extends string = string> = Omit<LinkProps, 'href'> & { href: THref };

/** The props of a page package's `<Form>`: those of the core `<Form>`, with a URL `action` below the package's mount. */
export type MountedFormProps<THref extends string = string> = Omit<FormProps, 'action'> & { action?: THref | FormAction };

/** The props of a page package's `<SubmitButton>`, with a URL `formAction` below the package's mount. */
export type MountedSubmitButtonProps<THref extends string = string> = Omit<SubmitButtonProps, 'formAction'> & {
  formAction?: THref | Exclude<SubmitButtonProps['formAction'], string>;
};

/** The client router, as a page package's `useRouter()` returns it: `push`, `replace` and `prefetch` go below its mount. */
export type MountedRouter<THref extends string = string> = Omit<Router, 'push' | 'replace' | 'prefetch'> & {
  push(href: THref, options?: { scroll?: boolean }): Promise<void>;
  replace(href: THref, options?: { scroll?: boolean }): Promise<void>;
  prefetch(href: THref): void;
};

/** What {@link mounted} returns: the links, forms and router hooks of one page package. */
export type Mounted<THref extends string = string> = {
  /** A `<Link>` whose href is below the mount: `/articles` goes to `/help/articles` for a package mounted at `/help`. */
  Link: (props: MountedLinkProps<THref>) => ReactNode;
  /** A `<Form>` whose URL `action` is below the mount. Without one it posts to the page on screen, as the core `<Form>`. */
  Form: (props: MountedFormProps<THref>) => ReactNode;
  /** A `<SubmitButton>` whose URL `formAction` is below the mount. */
  SubmitButton: (props: MountedSubmitButtonProps<THref>) => ReactNode;
  /** The client router, whose `push`, `replace` and `prefetch` take hrefs below the mount. */
  useRouter: () => MountedRouter<THref>;
  /**
   * The path of the page on screen below the mount, without the base path: `/articles/x` for `/help/articles/x`, and
   * `/` for the mount itself. A page outside the mount gets its whole path, as the core `usePathname` gives it.
   */
  usePathname: () => string;
  /**
   * An href below the mount, under the base path, for a plain `<a>`, another attribute or a `fetch`: a page's, or an
   * endpoint's from the package's `endpointUrl()`. Untyped, as the core `useHref`.
   */
  useHref: (href: string) => string;
};

/** The package's URL for the page on screen, from the payload: null when the page has no value for the prefix. */
function useMountOrNull(name: string): string | null {
  const mounts = usePagePayload().mounts;
  const mount = mounts && Object.hasOwn(mounts, name) ? mounts[name] : undefined;
  if (mount === undefined) {
    throw new Error(`netjes: the page package '${name}' is not mounted: map it with app.MapPagePackage(...) before app.MapPages()`);
  }
  return mount;
}

/** The package's URL for the page on screen; throws when the page has none. */
function useMount(name: string): string {
  const mount = useMountOrNull(name);
  if (mount === null) throw new Error(`netjes: '${name}' is mounted at a prefix with a value, and this page has none for it`);
  return mount;
}

/**
 * A segment as routing compares it: decoded where it can be, as the server decodes a path, which leaves `%2F` as it is
 * (so `a%2Fb` and `a%252Fb` hold the same value), in any case.
 */
function comparable(segment: string): string {
  try {
    return decodeURIComponent(segment.replace(/%2F/gi, '%252F')).toLowerCase();
  } catch {
    return segment.toLowerCase();
  }
}

/** `pathname` without the mount in front, `/` for the mount itself; the whole path when it is not below the mount. */
function belowMount(pathname: string, mount: string | null): string {
  if (mount === null || mount === '/') return pathname;
  const prefix = mount.split('/').slice(1);
  const segments = pathname.split('/').slice(1);
  if (segments.length < prefix.length || prefix.some((segment, i) => comparable(segment) !== comparable(segments[i] ?? ''))) return pathname;
  const rest = segments.slice(prefix.length).join('/');
  return rest === '' ? '/' : `/${rest}`;
}

/**
 * `href` below `mount`: a path gets the mount in front, and a link within the page on screen (`?…`, `#…`) passes
 * through. Anything else throws, as does a path with a dot segment, which would climb out of the mount.
 */
function underMount(href: string, mount: string, name: string): string {
  const withinThePage = href.startsWith('?') || href.startsWith('#');
  const path = href.split(/[?#]/, 1)[0] ?? '';
  // Browsers read a backslash in a path as a slash, and %2e as a dot.
  if (!isHrefShape(href) || (!withinThePage && (!href.startsWith('/') || path.split(/[/\\]/).some(isDotSegment)))) {
    throw new Error(
      `netjes: '${href}' is no link below the mount of '${name}': start it with '/' (but not '//' or '/\\'), with no '.' or ` +
        "'..' segment, or with '?' or '#'. Link to another site, or elsewhere in the app, with the Link from 'netjes'.",
    );
  }
  if (withinThePage || mount === '/') return href;
  return href === '/' || href[1] === '?' || href[1] === '#' ? mount + href.slice(1) : mount + href;
}

/**
 * The links, forms and router hooks of the page package `name`, its npm name, bound to where the app mounted it. Each
 * puts the package's URL for the page on screen in front of its hrefs, and the base path in front of that, so a
 * package links to its own pages as if it were mounted at the root. A package's generated file calls it once:
 * `export const { Link, Form, SubmitButton, useRouter, usePathname, useHref } = mounted<MountHref>('@acme/help-center')`.
 */
export function mounted<THref extends string = string>(name: string): Mounted<THref> {
  function MountedLink({ href, ...rest }: MountedLinkProps<THref>) {
    const mount = useMount(name);
    return <Link href={underMount(href, mount, name) as Href} {...rest} />;
  }

  function MountedForm({ action, ...rest }: MountedFormProps<THref>) {
    const mount = useMount(name);
    const url = typeof action === 'string' ? underMount(action, mount, name) : action;
    return <Form {...(url === undefined ? {} : { action: url })} {...rest} />;
  }

  function MountedSubmitButton({ formAction, ...rest }: MountedSubmitButtonProps<THref>) {
    const mount = useMount(name);
    return <SubmitButton formAction={typeof formAction === 'string' ? underMount(formAction, mount, name) : formAction} {...rest} />;
  }

  function useMountedRouter(): MountedRouter<THref> {
    const mount = useMount(name);
    const router = useRouter();
    return useMemo(
      () => ({
        push: (href, options) => router.push(underMount(href, mount, name) as Href, options),
        replace: (href, options) => router.replace(underMount(href, mount, name) as Href, options),
        prefetch: (href) => router.prefetch(underMount(href, mount, name) as Href),
        refresh: () => router.refresh(),
        back: () => router.back(),
        forward: () => router.forward(),
      }),
      [router, mount],
    );
  }

  function useMountedPathname(): string {
    return belowMount(usePathname(), useMountOrNull(name));
  }

  function useMountedHref(href: string): string {
    return useHref(underMount(href, useMount(name), name));
  }

  return {
    Link: MountedLink,
    Form: MountedForm,
    SubmitButton: MountedSubmitButton,
    useRouter: useMountedRouter,
    usePathname: useMountedPathname,
    useHref: useMountedHref,
  };
}
