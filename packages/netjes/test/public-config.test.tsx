import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { z } from 'zod';
import { PublicConfigProvider, usePublicConfig } from '../src/public-config.tsx';
import { createTestApp } from '../src/testing.tsx';

const schema = z.object({ apiUrl: z.url(), features: z.object({ search: z.stringbool() }) });

function Settings() {
  const config = usePublicConfig(schema);
  return <p>{`${config.apiUrl} search ${config.features.search ? 'on' : 'off'}`}</p>;
}

describe('usePublicConfig', () => {
  it('parses the configuration the server sent', () => {
    render(
      <PublicConfigProvider value={{ apiUrl: 'https://api.example.com', features: { search: 'true' } }}>
        <Settings />
      </PublicConfigProvider>,
    );

    expect(screen.getByText('https://api.example.com search on')).toBeDefined();
  });

  it('is unknown without a schema, and an empty object when there is no config', () => {
    function Raw() {
      const config: unknown = usePublicConfig();
      return <p>{JSON.stringify(config)}</p>;
    }

    render(<Raw />);

    expect(screen.getByText('{}')).toBeDefined();
  });

  it('throws to the error boundary when the config does not match the schema', () => {
    expect(() =>
      render(
        <PublicConfigProvider value={{ apiUrl: 'not a url' }}>
          <Settings />
        </PublicConfigProvider>,
      ),
    ).toThrow(/Invalid public config: apiUrl/);
  });

  it('can be set in tests through createTestApp', () => {
    const app = createTestApp({ publicConfig: { apiUrl: 'https://test.example.com', features: { search: 'false' } } });

    render(<Settings />, { wrapper: app.Wrapper });

    expect(screen.getByText('https://test.example.com search off')).toBeDefined();
  });
});
