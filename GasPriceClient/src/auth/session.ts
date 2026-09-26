import * as Crypto from 'expo-crypto';
import * as SecureStore from 'expo-secure-store';

import { ApiError, send, serialQueue } from './http';
import type { AuthClient, Session } from './types';

const storageKey = 'gasprice.guest-session.v1';
const storageOptions = { keychainAccessible: SecureStore.WHEN_UNLOCKED_THIS_DEVICE_ONLY };
type SavedSession = {
  appInstanceId: string;
  accessToken?: string;
  accessTokenExpiresAt?: string;
  guestCredential?: string;
  signedOut?: boolean;
};
type LoginResult = SavedSession & { playerName: string; playerId: number };
let saved: SavedSession | null | undefined;
let pendingSave = false;
const queue = serialQueue();

async function read() {
  if (saved === undefined) {
    const value = await SecureStore.getItemAsync(storageKey, storageOptions);
    saved = value ? JSON.parse(value) as SavedSession : null;
    if (saved && (typeof saved.appInstanceId !== 'string' ||
      !/^[\da-f]{8}-[\da-f]{4}-4[\da-f]{3}-[89ab][\da-f]{3}-[\da-f]{12}$/i.test(saved.appInstanceId))) {
      throw new ApiError(0, 'storage_error');
    }
  }
  if (pendingSave && saved) await persist(saved);
  return saved;
}

async function persist(value: SavedSession) {
  // Keep the one-time credential in memory if the OS temporarily rejects a write;
  // subsequent operations retry persistence before sending more requests.
  saved = value;
  pendingSave = true;
  await SecureStore.setItemAsync(storageKey, JSON.stringify(value), storageOptions);
  pendingSave = false;
}

function headers(value: SavedSession): Record<string, string> {
  return {
    'X-App-Instance-Id': value.appInstanceId,
    ...(value.accessToken ? { Authorization: `Bearer ${value.accessToken}` } : {}),
  };
}

async function login(playerName?: string, startNewGuest = false): Promise<Session> {
  let value = await read();
  if (!value || startNewGuest) {
    value = { appInstanceId: Crypto.randomUUID() };
    await persist(value);
  }
  const result = await send<LoginResult>('/auth/guest/login', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json', 'X-App-Instance-Id': value.appInstanceId,
      ...(value.guestCredential ? { 'X-Guest-Credential': value.guestCredential } : {}),
    },
    body: JSON.stringify({ playerName }),
  });
  await persist({
    appInstanceId: result.appInstanceId, accessToken: result.accessToken,
    accessTokenExpiresAt: result.accessTokenExpiresAt,
    guestCredential: result.guestCredential ?? value.guestCredential, signedOut: false,
  });
  return { state: 'authenticated', playerName: result.playerName, playerId: result.playerId };
}

async function restore(): Promise<Session> {
  const value = await read();
  if (!value) return { state: 'new' };
  if (value.signedOut) return { state: 'signedOut' };
  if (value.accessToken) {
    try {
      const status = await send<{ playerName: string; playerId: number }>('/auth/status', { headers: headers(value) });
      return { state: 'authenticated', playerName: status.playerName, playerId: status.playerId };
    } catch (error) {
      if (!(error instanceof ApiError) || error.status !== 401) throw error;
      if (error.code === 'player_not_logged_in') return { state: 'signedOut' };
    }
  }
  if (!value.guestCredential) return { state: 'unavailable' };
  try {
    return await login();
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) return { state: 'unavailable' };
    throw error;
  }
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  let value = await read();
  if (!value || value.signedOut) throw new ApiError(401, 'player_not_logged_in');
  try {
    return await send<T>(path, { ...init, headers: { ...init.headers, ...headers(value) } });
  } catch (error) {
    if (!(error instanceof ApiError) || error.status !== 401 ||
        error.code === 'player_not_logged_in' || !value.guestCredential) throw error;
    await login();
    value = (await read())!;
    return send<T>(path, { ...init, headers: { ...init.headers, ...headers(value) } });
  }
}

async function logout(): Promise<Session> {
  let value = await read();
  if (!value) return { state: 'new' };
  try {
    await send('/auth/logout', { method: 'POST', headers: headers(value) });
  } catch (error) {
    if (!(error instanceof ApiError) || error.status !== 401) throw error;
    if (value.guestCredential) {
      // The existing native endpoint requires a valid access token. Renew only
      // for this explicit logout operation, then revoke it immediately.
      await login();
      value = (await read())!;
      await send('/auth/logout', { method: 'POST', headers: headers(value) });
    }
  }
  await persist({ appInstanceId: value.appInstanceId, guestCredential: value.guestCredential, signedOut: true });
  return { state: 'signedOut' };
}

export const authClient: AuthClient = {
  logout: () => queue(logout),
  restore: () => queue(restore),
  login: (name, fresh) => queue(() => login(name, fresh)),
  request: <T>(path: string, init?: RequestInit) => queue(() => request<T>(path, init)),
};
