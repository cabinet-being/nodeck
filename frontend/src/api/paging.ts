/** One page of a list plus the number of all matching items. */
export type Page<T> = { items: T[]; total: number };

/**
 * Lists answer with a plain array; with `include_total=true` the server adds the number of
 * all matching items in the `X-Total-Count` header.
 */
export function totalFrom(response: Response, fallback: number) {
  const total = Number(response.headers.get('X-Total-Count'));
  return Number.isSafeInteger(total) && total >= 0 ? total : fallback;
}

export function pageParams(page: number, pageSize: number) {
  return { limit: String(pageSize), offset: String((Math.max(1, page) - 1) * pageSize), include_total: 'true' };
}
