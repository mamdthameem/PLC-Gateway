import { API_BASE } from './apiBase';

/**
 * Every signed-in dashboard API call goes through here.
 *
 * - It asks the current sign-in for a token on EVERY call. For an email sign-in that is Firebase's
 *   getIdToken(), which hands back a fresh token whenever the old one is close to expiring, so a
 *   long-open dashboard never sends an expired one. For the shared or local admin login it is the
 *   token the gateway issued.
 * - A 401 or 403 ends the session: the dashboard returns to the login page and shows the gateway's
 *   own reason (e.g. "This account is not registered for this site").
 *
 * AuthContext plugs both in when it starts; until then calls go out without a token.
 */

type TokenSource = () => Promise<string | null>;

let tokenSource: TokenSource = async () => null;
let sessionEnded: (message: string) => void = () => {};

export function setTokenSource(source: TokenSource): void {
  tokenSource = source;
}

export function setSessionEndedHandler(handler: (message: string) => void): void {
  sessionEnded = handler;
}

/** fetch() for a dashboard API path such as "/api/lifetime", signed in. */
export async function apiFetch(path: string, init: RequestInit = {}): Promise<Response> {
  const headers = new Headers(init.headers);
  const token = await tokenSource();
  if (token) headers.set('Authorization', `Bearer ${token}`);
  if (init.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json');

  const res = await fetch(`${API_BASE}${path}`, { ...init, headers });
  if (res.status === 401 || res.status === 403) {
    sessionEnded(await reasonOf(res));
  }
  return res;
}

/** The gateway's own sentence for a refused request, or a plain fallback. */
export async function reasonOf(res: Response): Promise<string> {
  const body = await res.clone().json().catch(() => null) as { message?: string; error?: string } | null;
  return body?.message || body?.error
    || (res.status === 403 ? 'You do not have access to this dashboard.' : 'Please sign in again.');
}
