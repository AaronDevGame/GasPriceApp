import type { Coordinates } from './api';

// Use the browser prompt directly, including Safari where Permissions API support varies.
export function requestLocation(): Promise<Coordinates | null> {
  if (!navigator.geolocation) return Promise.reject(new Error('Geolocation unavailable'));
  return new Promise((resolve, reject) => {
    navigator.geolocation.getCurrentPosition(
      position => resolve({ latitude: position.coords.latitude, longitude: position.coords.longitude }),
      error => error.code === error.PERMISSION_DENIED ? resolve(null) : reject(new Error('Location unavailable')),
      { enableHighAccuracy: false, timeout: 20000, maximumAge: 60000 },
    );
  });
}
