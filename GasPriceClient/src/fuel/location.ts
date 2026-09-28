import * as Location from 'expo-location';
import type { Coordinates } from './api';

export async function requestLocation(): Promise<Coordinates | null> {
  const permission = await Location.requestForegroundPermissionsAsync();
  if (!permission.granted) return null;
  let timer: ReturnType<typeof setTimeout> | undefined;
  const position = await Promise.race([
    Location.getCurrentPositionAsync({ accuracy: Location.Accuracy.Balanced }),
    new Promise<never>((_, reject) => { timer = setTimeout(() => reject(new Error('Location timed out')), 20000); }),
  ]).finally(() => clearTimeout(timer));
  return { latitude: position.coords.latitude, longitude: position.coords.longitude };
}
