import { describe, expect, it } from 'vitest';
import { containerName, dbPort } from '../e2e/db-config.mjs';

describe('the end-to-end database settings', () => {
  it('defaults to port 8432 and the original container name', () => {
    expect(dbPort({})).toBe(8432);
    expect(containerName(8432)).toBe('taskboard-e2e-db');
  });

  it('reads E2E_DB_PORT and names the container after the port', () => {
    expect(dbPort({ E2E_DB_PORT: '21040' })).toBe(21040);
    expect(containerName(21040)).toBe('taskboard-e2e-db-21040');
  });

  it('rejects a port that is not a number', () => {
    expect(() => dbPort({ E2E_DB_PORT: 'abc' })).toThrow(/E2E_DB_PORT/);
  });
});
