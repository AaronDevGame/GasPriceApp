import { Platform } from 'react-native';

export class ApiError extends Error {
  constructor(public status: number, public code: string) {
    super('API request failed'); // Never include responses, request headers, or secrets.
  }
}

function apiBase() {
  if (Platform.OS === 'web') return ''; // Same-origin HTTPS reverse proxy.
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
  if (!/^\/(auth|player|fuel-prices|ai)(\/|$)/.test(path) || /[?#\\]/.test(path)) {
    throw new ApiError(0, 'invalid_api_path');
  }
  const url = `${apiBase()}${path}`;
  for (let attempt = 0; attempt < 2; attempt++) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 15000);
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
      const envelope = await response.json();
      if (!response.ok) throw new ApiError(response.status, envelope.error?.error ?? 'request_failed');
      return envelope.data as T;
    } catch (error) {
      if (error instanceof ApiError) throw error;
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
  if (error.code === 'api_not_configured') return 'The API address has not been configured for this app.';
  if (error.status === 429) return 'Too many attempts. Please wait a few minutes and try again.';
  if (error.status === 401) return 'Your guest identity could not be verified. Try restoring your session.';
  if (error.code === 'invalid_csrf_token') return 'Your browser session changed. Please retry.';
  if (error.status === 400) return 'Check your player name (up to 24 characters) and try again.';
  return 'Unable to connect. Check your connection and try again. Your saved guest has been kept.';
}
