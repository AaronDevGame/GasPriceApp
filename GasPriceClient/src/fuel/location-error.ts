export class LocationError extends Error {
  constructor(public reason: 'unsupported' | 'insecure' | 'unavailable' | 'timeout') {
    super(`Location ${reason}`);
  }
}

export function locationErrorMessage(error: unknown): string {
  if (error instanceof LocationError) {
    switch (error.reason) {
      case 'unsupported': return 'This browser cannot provide a location. Choose a city or province below.';
      case 'insecure': return 'Location access requires a secure connection. Open the app over HTTPS.';
      case 'timeout': return 'Getting your location timed out. Check device Location Services and Wi-Fi, then try Use my location again.';
      case 'unavailable': return 'Your device could not determine its location. Check device Location Services and Wi-Fi, then try Use my location again.';
    }
  }
  return 'Your location is unavailable. You can choose a city or province below.';
}
