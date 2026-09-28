import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react';

import { ApiError, errorMessage } from './http';
import { authClient } from './session';
import type { Session } from './types';

type AuthContextValue = {
  session: Session | null;
  loading: boolean;
  error: string | null;
  logout(): Promise<void>;
  restore(): Promise<void>;
};
const AuthContext = createContext<AuthContextValue | null>(null);

export async function ensureSession(): Promise<Session> {
  const next = await authClient.restore();
  if (next.state === 'unavailable') throw new ApiError(401, 'identity_unavailable');
  return next.state === 'authenticated' ? next : authClient.login();
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<Session | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const busy = useRef(false);
  const startup = useRef<Promise<Session> | null>(null);
  const run = useCallback(async (operation: () => Promise<Session>) => {
    if (busy.current) return;
    busy.current = true;
    setLoading(true);
    setError(null);
    try {
      setSession(await operation());
    } catch (failure) {
      setError(errorMessage(failure));
    } finally {
      busy.current = false;
      setLoading(false);
    }
  }, []);
  const logout = useCallback(() => run(() => authClient.logout()), [run]);
  const restore = useCallback(() => run(ensureSession), [run]);
  useEffect(() => {
    let active = true;
    // Reuse startup work through Strict Mode's effect replay; do not rotate twice.
    startup.current ??= ensureSession();
    void startup.current.then(
      next => { if (active) setSession(next); },
      failure => { if (active) setError(errorMessage(failure)); },
    ).finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, []);

  return <AuthContext.Provider value={{ session, loading, error, restore, logout }}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const value = useContext(AuthContext);
  if (!value) throw new Error('AuthProvider is required');
  return value;
}
