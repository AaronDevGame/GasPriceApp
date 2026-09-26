export type SessionState = 'new' | 'authenticated' | 'signedOut' | 'unavailable';
export type Session = { state: SessionState; playerName?: string; playerId?: number };
export type PlayerData = {
  playerId: number;
  health: number;
  money: number;
  position: Record<string, unknown>;
  inventory: unknown[];
  extraData: Record<string, unknown>;
};
export type AuthClient = {
  logout(): Promise<Session>;
  restore(): Promise<Session>;
  login(playerName?: string, startNewGuest?: boolean): Promise<Session>;
  request<T>(path: string, init?: RequestInit): Promise<T>;
};
