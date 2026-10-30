import { describe, expect, it, vi } from 'vitest';
import { MissingModuleError, ModuleLoadError, createModuleLoader } from '../src/modules.ts';

const Page = () => null;
const Layout = () => null;

describe('createModuleLoader', () => {
  it('loads the page and segment files by root-relative file name', async () => {
    const loader = createModuleLoader({
      '/app/page.tsx': async () => ({ default: Page }),
      '/app/layout.tsx': async () => ({ default: Layout }),
    });

    const modules = await loader.loadRoute({ page: 'app/page.tsx', segments: [{ id: 'app', layout: 'app/layout.tsx' }] });

    expect(modules.page).toBe(Page);
    expect(modules.segments).toEqual([{ id: 'app', Layout }]);
  });

  it('imports each module once', async () => {
    const importer = vi.fn(async () => ({ default: Page }));
    const loader = createModuleLoader({ '/app/page.tsx': importer });

    await loader.load('app/page.tsx');
    await loader.load('app/page.tsx');

    expect(importer).toHaveBeenCalledTimes(1);
  });

  it('reports files that are not in the build', async () => {
    const loader = createModuleLoader({});

    await expect(loader.load('app/new/page.tsx')).rejects.toBeInstanceOf(MissingModuleError);
  });

  it('names the file and says why, in the browser, when a module fails to load', async () => {
    const thrown = new ReferenceError('missingThing is not defined');
    const loader = createModuleLoader(
      {
        '/app/page.tsx': async () => {
          throw thrown;
        },
      },
      { nameFailedImports: true },
    );

    const error = await loader.load('app/page.tsx').catch((reason: unknown) => reason);

    expect(error).toBeInstanceOf(ModuleLoadError);
    expect(error).toMatchObject({ file: 'app/page.tsx', cause: thrown });
    expect((error as Error).message).toBe('netjes: app/page.tsx could not be loaded (missingThing is not defined)');
  });

  it('rejects modules without a default component export', async () => {
    const loader = createModuleLoader({ '/app/page.tsx': async () => ({ Page }) });

    await expect(loader.load('app/page.tsx')).rejects.toThrow(/default export/);
  });

  it('allows a route without a page module', async () => {
    const loader = createModuleLoader({});

    expect(await loader.loadRoute({ page: null, segments: [] })).toEqual({ page: null, segments: [] });
  });
});
