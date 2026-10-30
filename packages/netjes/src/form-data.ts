/** A form's fields as a request body: URL-encoded, or multipart when there are files or the form asks for it. */
export type EncodedForm = { body: string; contentType: string } | { body: FormData; contentType: null };

export function encodeForm(data: FormData, multipart = false): EncodedForm {
  const entries = [...data.entries()];
  if (multipart || entries.some(([, value]) => typeof value !== 'string')) return { body: data, contentType: null };
  const params = new URLSearchParams();
  for (const [name, value] of entries) params.append(name, String(value));
  return { body: params.toString(), contentType: 'application/x-www-form-urlencoded;charset=UTF-8' };
}
