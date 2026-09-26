import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react';

import { ApiError, errorMessage } from './http';
import { authClient } from './session';
import type { PlayerData, Session } from './types';

type AuthContextValue = {
  session: Session | null;
  player: PlayerData | null;
  loading: boolean;
  error: string | null;
  logout(): Promise<void>;
  restore(): Promise<void>;
  login(name?: string, fresh?: boolean): Promise<void>;
};
const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<Session | null>(null);
  const [player, setPlayer] = useState<PlayerData | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const busy = useRef(false);
  const run = useCallback(async (operation: () => Promise<Session>) => {
    if (busy.current) return;
    busy.current = true;
    setLoading(true);
    setError(null);
    setPlayer(null);
    try {
      const next = await operation();
      setSession(next);
      if (next.state === 'authenticated') {
        setPlayer(await authClient.request<PlayerData>('/player/data'));
      }
    } catch (failure) {
      if (failure instanceof ApiError && failure.status === 401) setSession({ state: 'unavailable' });
      setError(errorMessage(failure));
    } finally {
      busy.current = false;
      setLoading(false);
    }
  }, []);
  const logout = useCallback(() => run(() => authClient.logout()), [run]);
  const restore = useCallback(() => run(() => authClient.restore()), [run]);
  const login = useCallback((name?: string, fresh?: boolean) => run(() => authClient.login(name, fresh)), [run]);
  useEffect(() => {
    let active = true;
    async function load() {
      try {
        const next = await authClient.restore();
        const data = next.state === 'authenticated'
          ? await authClient.request<PlayerData>('/player/data') : null;
        if (active) { setSession(next); setPlayer(data); }
      } catch (failure) {
        if (active) setError(errorMessage(failure));
      } finally {
        if (active) setLoading(false);
      }
    }
    void load();
    return () => { active = false; };
  }, []);

  return <AuthContext.Provider value={{ session, player, loading, error, restore, login, logout }}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const value = useContext(AuthContext);
  if (!value) throw new Error('AuthProvider is required');
  return value;
}
