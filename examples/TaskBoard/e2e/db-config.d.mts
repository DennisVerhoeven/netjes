export const DEFAULT_DB_PORT: number;
export function dbPort(env?: Record<string, string | undefined>): number;
export function containerName(port: number): string;
