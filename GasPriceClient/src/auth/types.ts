export type SessionState = 'new' | 'authenticated' | 'signedOut' | 'unavailable';
export type Session = { state: SessionState; playerName?: string; playerId?: number };
export type AuthClient = {
  logout(): Promise<Session>;
  restore(): Promise<Session>;
  login(): Promise<Session>;
  // Check/renew an existing session without creating a guest or undoing logout.
  resume(): Promise<Session | null>;
  request<T>(path: string, init?: RequestInit): Promise<T>;
};
