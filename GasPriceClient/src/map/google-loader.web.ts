/// <reference types="google.maps" />
let loading: Promise<void> | null = null;
let attempt = 0;
export function loadGoogleMaps(key: string): Promise<void> {
  if (typeof google !== 'undefined' && google.maps?.Map) return Promise.resolve();
  if (loading) return loading;
  loading = new Promise<void>((resolve, reject) => {
    const name = `__gasPriceMapsReady${++attempt}`;
    const callbacks = window as unknown as Record<string, unknown>;
    const script = document.createElement('script');
    const done = (error?: Error) => {
      clearTimeout(timeout); delete callbacks[name]; script.onerror = null;
      if (error) { script.remove(); reject(error); } else resolve();
    };
    callbacks[name] = () => done();
    const timeout = setTimeout(() => done(new Error('Map loading timed out')), 25000);
    const query = new URLSearchParams({ key, v: 'quarterly', loading: 'async', callback: name, region: 'PH', language: 'en' });
    script.src = `https://maps.googleapis.com/maps/api/js?${query}`;
    script.async = true; script.onerror = () => done(new Error('Map unavailable'));
    document.head.appendChild(script);
  }).catch(error => { loading = null; throw error; });
  return loading;
}
