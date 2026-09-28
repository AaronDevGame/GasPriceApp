import type { Coordinates } from './api';
import { LocationError } from './location-error';

// Use the browser prompt directly, including Safari where Permissions API support varies.
export function requestLocation(): Promise<Coordinates | null> {
  if (typeof navigator === 'undefined' || !navigator.geolocation) return Promise.reject(new LocationError('unsupported'));
  if (globalThis.isSecureContext === false) return Promise.reject(new LocationError('insecure'));
  return new Promise((resolve, reject) => {
    navigator.geolocation.getCurrentPosition(
      position => resolve({ latitude: position.coords.latitude, longitude: position.coords.longitude }),
      error => {
        if (error.code === error.PERMISSION_DENIED) resolve(null);
        else reject(new LocationError(error.code === error.TIMEOUT ? 'timeout' : 'unavailable'));
      },
      { enableHighAccuracy: false, timeout: 20000, maximumAge: 60000 },
    );
  });
}
