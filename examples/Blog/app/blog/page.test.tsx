import { render, screen, within } from '@testing-library/react';
import { createTestApp } from 'netjes/testing';
import { describe, expect, it } from 'vitest';
import BlogIndex from './page.tsx';

const tags = ['react', 'routing'];

function currentFilters() {
  const filters = within(screen.getByRole('navigation', { name: 'Filter by tag' }));
  return filters.queryAllByRole('link', { current: 'page' }).map((link) => link.textContent);
}

describe('the blog index', () => {
  it('marks the tag it shows as the current filter, and not All', () => {
    render(<BlogIndex tag="routing" tags={tags} posts={[]} />, { wrapper: createTestApp({ url: '/blog?tag=routing' }).Wrapper });

    expect(currentFilters()).toEqual(['routing']);
  });

  it('marks All as the current filter without a tag', () => {
    render(<BlogIndex tag={null} tags={tags} posts={[]} />, { wrapper: createTestApp({ url: '/blog' }).Wrapper });

    expect(currentFilters()).toEqual(['All']);
  });
});
