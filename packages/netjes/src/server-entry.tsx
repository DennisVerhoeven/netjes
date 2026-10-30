import { pages } from 'virtual:netjes/pages';
import { createServerRenderer } from './server-render.tsx';

export const { render, renderToStream } = createServerRenderer(pages);
