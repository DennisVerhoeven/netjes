import netjes from 'netjes/vite';
import { defineConfig } from 'vite';

export default defineConfig({
  // The help center page package, which Program.cs mounts at /(marketing)/help.
  plugins: [netjes({ packages: ['@netjes-examples/help-center'] })],
});
