import { ApiError, send, serialQueue } from './http';
import type { AuthClient, Session } from './types';

let csrfToken: string | undefined;
const queue = serialQueue();

async function mutation<T>(path: string, init: RequestInit): Promise<T> {
  for (let attempt = 0; attempt < 2; attempt++) {
    csrfToken ??= (await send<{ csrfToken: string }>('/auth/browser/csrf')).csrfToken;
    try {
      return await send<T>(path, { ...init, headers: { ...init.headers, 'X-CSRF-Token': csrfToken } });
    } catch (error) {
      if (!(error instanceof ApiError) || error.code !== 'invalid_csrf_token' || attempt > 0) throw error;
      csrfToken = undefined;
    }
  }
  throw new ApiError(400, 'invalid_csrf_token');
}

function login() {
  return mutation<Session>('/auth/browser/guest/login', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({}),
  });
}

async function status() {
  return send<Omit<Session, 'state'> & { state: Session['state'] | 'resumable' }>('/auth/browser/status');
}

async function restore(): Promise<Session> {
  const result = await status();
  if (result.state !== 'resumable') return result as Session;
  try { return await login(); } catch (error) {
    if (error instanceof ApiError && error.status === 401) return { state: 'unavailable' };
    throw error;
  }
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const perform = () => !init.method || ['GET', 'HEAD'].includes(init.method.toUpperCase())
    ? send<T>(path, init) : mutation<T>(path, init);
  try { return await perform(); } catch (error) {
    if (!(error instanceof ApiError) || error.status !== 401) throw error;
    // Check persisted logout state before renewing, including logout in another tab.
    const current = await status();
    if (current.state !== 'resumable') throw error;
    await login();
    return perform();
  }
}

export const authClient: AuthClient = {
  logout: () => queue(async () => {
    await mutation('/auth/browser/logout', { method: 'POST' });
    return { state: 'signedOut' };
  }),
  restore: () => queue(restore),
  login: () => queue(login),
  request: <T>(path: string, init?: RequestInit) => queue(() => request<T>(path, init)),
};
