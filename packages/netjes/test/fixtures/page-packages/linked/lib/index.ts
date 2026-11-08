export { Badge } from './badge.tsx';

/** One object per module instance: a page that renders its id shows which instance it got. */
export const marker = { id: `linked-${Math.random().toString(36).slice(2)}` };
