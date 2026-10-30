import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { type StandardSchemaV1, definePage } from '../src/define-page.tsx';

// A hand-rolled Standard Schema, so the test needs no validation library.
const postProps: StandardSchemaV1<unknown, { title: string }> = {
  '~standard': {
    version: 1,
    vendor: 'test',
    validate(value) {
      if (typeof value === 'object' && value !== null && typeof (value as { title?: unknown }).title === 'string') {
        return { value: { title: (value as { title: string }).title.trim() } };
      }
      return { issues: [{ message: 'Expected a string', path: ['title'] }] };
    },
  },
};

const Post = definePage(postProps, ({ title }) => <h1>{title}</h1>);

describe('definePage', () => {
  it('renders the page with the validated (and transformed) props', () => {
    render(<Post title="  Hello  " />);

    expect(screen.getByRole('heading').textContent).toBe('Hello');
  });

  it('throws a readable error naming the invalid prop', () => {
    expect(() => render(<Post />)).toThrow('Invalid props: title: Expected a string');
  });

  it('names the wrapped component for devtools', () => {
    function PostPage() {
      return null;
    }
    expect(definePage(postProps, PostPage).displayName).toBe('definePage(PostPage)');
  });
});
