/** The Standard Schema interface (https://standardschema.dev), implemented by zod, valibot, arktype and others. */
export interface StandardSchemaV1<Input = unknown, Output = Input> {
  readonly '~standard': {
    readonly version: 1;
    readonly vendor: string;
    readonly validate: (value: unknown) => StandardSchemaV1.Result<Output> | Promise<StandardSchemaV1.Result<Output>>;
    readonly types?: { readonly input: Input; readonly output: Output } | undefined;
  };
}

export declare namespace StandardSchemaV1 {
  type Result<Output> = { readonly value: Output; readonly issues?: undefined } | { readonly issues: ReadonlyArray<Issue> };
  type Issue = { readonly message: string; readonly path?: ReadonlyArray<PropertyKey | { readonly key: PropertyKey }> | undefined };
  type InferOutput<Schema extends StandardSchemaV1> = NonNullable<Schema['~standard']['types']>['output'];
}

function describeIssue(issue: StandardSchemaV1.Issue): string {
  const path = issue.path?.map((segment) => String(typeof segment === 'object' ? segment.key : segment)).join('.');
  return path ? `${path}: ${issue.message}` : issue.message;
}

function validateSync(schema: StandardSchemaV1, value: unknown, what: string) {
  const result = schema['~standard'].validate(value);
  if (result instanceof Promise) throw new Error(`netjes: async schemas are not supported for ${what}`);
  return result;
}

function topLevelKey(issue: StandardSchemaV1.Issue): string | undefined {
  const first = issue.path?.[0];
  const key = typeof first === 'object' ? first.key : first;
  return typeof key === 'string' ? key : undefined;
}

/**
 * Like {@link parseWith}, for input visitors control: values the schema rejects are dropped (so defaults and
 * optional fields apply) before giving up. Only a value that stays invalid without them throws.
 */
export function parseLenient<Schema extends StandardSchemaV1>(
  schema: Schema,
  value: Record<string, unknown>,
  what: string,
): StandardSchemaV1.InferOutput<Schema> {
  const input = { ...value };
  // Each pass drops what the issues name; a schema that names one key at a time must not cost a pass per key.
  for (let pass = 0; ; pass++) {
    const result = validateSync(schema, input, what);
    if (!result.issues) return result.value as StandardSchemaV1.InferOutput<Schema>;
    if (pass >= MAX_PASSES) throw new Error(`Invalid ${what}: ${result.issues.map(describeIssue).join('; ')}`);
    const rejected = result.issues.map(topLevelKey).filter((key): key is string => key !== undefined && key in input);
    // Issues without a path, such as a strict object's unrecognized keys: the keys the issue lists (zod's do), else
    // (for a handful of keys) one whose absence leaves fewer such issues.
    const listed = rejected.length === 0 ? listedKeys(result.issues, input) : [];
    const culprit = rejected.length === 0 && listed.length === 0 ? keyBehindPathlessIssues(schema, input, result.issues, what) : undefined;
    const drop = rejected.length > 0 ? rejected : listed.length > 0 ? listed : culprit === undefined ? [] : [culprit];
    if (drop.length === 0) throw new Error(`Invalid ${what}: ${result.issues.map(describeIssue).join('; ')}`);
    for (const key of drop) delete input[key];
  }
}

/** Keys a pathless issue names itself, like zod's `unrecognized_keys` (`issue.keys`). */
function listedKeys(issues: readonly StandardSchemaV1.Issue[], input: Record<string, unknown>): string[] {
  return issues.flatMap((issue) => {
    const keys = topLevelKey(issue) === undefined ? (issue as { keys?: unknown }).keys : undefined;
    return Array.isArray(keys) ? keys.filter((key): key is string => typeof key === 'string' && key in input) : [];
  });
}

/** Above these, the search costs too much for input a visitor controls; the parse fails instead. */
const MAX_SEARCHED_KEYS = 32;
const MAX_PASSES = 64;

function keyBehindPathlessIssues(
  schema: StandardSchemaV1,
  input: Record<string, unknown>,
  issues: readonly StandardSchemaV1.Issue[],
  what: string,
): string | undefined {
  const keys = Object.keys(input);
  if (keys.length > MAX_SEARCHED_KEYS) return undefined;
  const pathless = (list: readonly StandardSchemaV1.Issue[]) =>
    new Set(list.filter((issue) => topLevelKey(issue) === undefined).map((issue) => issue.message));
  const before = pathless(issues);
  return keys.find((key) => {
    const { [key]: _, ...rest } = input;
    const result = validateSync(schema, rest, what);
    if (!result.issues) return true;
    // Only a removal that leaves a strict subset: dropping a key a refinement needs adds an issue instead.
    const after = pathless(result.issues);
    return after.size < before.size && [...after].every((message) => before.has(message));
  });
}

/** Validates synchronously (rendering cannot wait) and throws `Invalid <what>: <issues>` when the value is rejected. */
export function parseWith<Schema extends StandardSchemaV1>(schema: Schema, value: unknown, what: string): StandardSchemaV1.InferOutput<Schema> {
  const result = validateSync(schema, value, what);
  if (result.issues) throw new Error(`Invalid ${what}: ${result.issues.map(describeIssue).join('; ')}`);
  return result.value as StandardSchemaV1.InferOutput<Schema>;
}
