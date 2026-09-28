import { Platform } from 'react-native';

export class ApiError extends Error {
  constructor(public status: number, public code: string) {
    super('API request failed'); // Never include responses, request headers, or secrets.
  }
}

function apiBase() {
  if (Platform.OS === 'web') {
    if (typeof window !== 'undefined' &&
        ['localhost', '127.0.0.1'].includes(window.location.hostname) && window.location.port === '8081') {
      throw new ApiError(0, 'web_proxy_required');
    }
    return ''; // Same-origin HTTPS reverse proxy.
  }
  const value = process.env.EXPO_PUBLIC_API_URL;
  if (!value) throw new ApiError(0, 'api_not_configured');
  const url = new URL(value);
  if (url.username || url.password || url.search || url.hash ||
      (url.protocol !== 'https:' && !(typeof __DEV__ !== 'undefined' && __DEV__ && url.protocol === 'http:'))) {
    throw new ApiError(0, 'api_not_configured');
  }
  return value.replace(/\/$/, '');
}

export async function send<T>(path: string, init: RequestInit = {}): Promise<T> {
  // Restrict callers to API paths: credentials must never follow a caller-supplied URL.
  const pathname = path.split('?')[0];
  if (!/^\/(auth|fuel-prices|ai)(\/|$)/.test(pathname) || /[#\\]/.test(path) ||
      /[\u0000-\u0020]/.test(path) || pathname.includes('..') || pathname.includes('%')) {
    throw new ApiError(0, 'invalid_api_path');
  }
  const url = `${apiBase()}${path}`;
  for (let attempt = 0; attempt < 2; attempt++) {
    const controller = new AbortController();
    // The first status request can be slow while the hosted API starts up.
    const timeoutMs = pathname === '/ai/fuel-prices' ? 120000
      : ['/auth/status', '/auth/browser/status'].includes(pathname) ? 60000 : 15000;
    const timeout = setTimeout(() => controller.abort(), timeoutMs);
    try {
      const response = await fetch(url, {
        ...init, signal: controller.signal, redirect: 'error',
        credentials: Platform.OS === 'web' ? 'same-origin' : 'omit',
        cache: 'no-store',
        headers: { Accept: 'application/json', ...init.headers },
      });
      if (response.status === 429 && attempt === 0) {
        const delay = Number(response.headers.get('Retry-After'));
        if (Number.isFinite(delay) && delay > 0 && delay <= 5) {
          await new Promise((resolve) => setTimeout(resolve, delay * 1000));
          continue;
        }
      }
      let envelope;
      try {
        envelope = await response.json();
      } catch {
        if (controller.signal.aborted) throw new ApiError(0, 'request_timeout');
        throw new ApiError(response.status, 'invalid_api_response');
      }
      if (!response.ok) throw new ApiError(response.status, envelope?.error?.error ?? 'request_failed');
      if (!envelope || typeof envelope !== 'object' || !Object.hasOwn(envelope, 'data')) {
        throw new ApiError(response.status, 'invalid_api_response');
      }
      return envelope.data as T;
    } catch (error) {
      if (error instanceof ApiError) throw error;
      if (controller.signal.aborted) throw new ApiError(0, 'request_timeout');
      throw new ApiError(0, 'network_error');
    } finally {
      clearTimeout(timeout);
    }
  }
  throw new ApiError(429, 'too_many_requests');
}

// Serialize session operations so startup, refresh and logout cannot race a token rotation.
export function serialQueue() {
  let tail: Promise<unknown> = Promise.resolve();
  return <T>(operation: () => Promise<T>): Promise<T> => {
    const result = tail.then(operation, operation);
    tail = result.catch(() => undefined);
    return result;
  };
}

export function errorMessage(error: unknown): string {
  if (!(error instanceof ApiError)) return 'Secure session storage is unavailable. Please try again.';
  if (error.code === 'web_proxy_required') return 'Open https://localhost:8443 to connect to the API. Port 8081 only serves the app.';
  if (error.code === 'api_not_configured') return 'The API address has not been configured for this app.';
  if (error.code === 'request_timeout') return 'The server took too long to respond. It may still be starting. Please refresh prices to try again.';
  if (error.status === 429) return 'Too many attempts. Please wait a few minutes and try again.';
  if (error.status >= 500) return 'The server is temporarily unavailable. Please refresh prices in a moment.';
  if (error.code === 'invalid_api_response') return `The API returned an unexpected response (HTTP ${error.status}). Check that the HTTPS proxy is running, then refresh prices.`;
  if (error.status === 401) return 'Your guest identity could not be verified. Try restoring your session.';
  if (error.code === 'invalid_csrf_token') return 'Your browser session changed. Please retry.';
  if (error.status === 400) return 'The selected location could not be used. Choose a Philippine city or province and try again.';
  return 'Unable to connect. Check your connection and try again. Your saved guest has been kept.';
}
