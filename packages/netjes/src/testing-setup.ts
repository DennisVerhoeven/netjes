// Registered by netjes() as a Vitest setup file: unmount what each test rendered (Vitest has no globals, so
// Testing Library cannot register this itself).
import { cleanup } from '@testing-library/react';
import { afterEach } from 'vitest';

afterEach(() => {
  cleanup();
});
