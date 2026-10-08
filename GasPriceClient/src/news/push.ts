import Constants from 'expo-constants';
import * as Notifications from 'expo-notifications';
import { Platform } from 'react-native';
import { authClient } from '@/auth/session';

export type Subscription = { enabled: boolean; includeForecasts: boolean; available: boolean };
const projectId = () => Constants.easConfig?.projectId ?? Constants.expoConfig?.extra?.eas?.projectId;
export const pushAvailable = () => Constants.appOwnership !== 'expo' && !!projectId();
export const getSubscription = () => authClient.request<Subscription>('/fuel-news/subscription');

export async function enableAlerts(includeForecasts: boolean) {
  if (!pushAvailable()) throw new Error('Notifications are unavailable in this app build.');
  if (Platform.OS === 'android') await Notifications.setNotificationChannelAsync('fuel-news', {
    name: 'Fuel price news', importance: Notifications.AndroidImportance.DEFAULT,
  });
  let permissions = await Notifications.getPermissionsAsync();
  if (permissions.status !== 'granted') permissions = await Notifications.requestPermissionsAsync();
  if (permissions.status !== 'granted') throw new Error('Allow notifications in your device settings to receive fuel alerts.');
  const token = (await Notifications.getExpoPushTokenAsync({ projectId: projectId() })).data;
  return authClient.request<Subscription>('/fuel-news/subscription', {
    method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ pushToken: token, includeForecasts }),
  });
}
export const disableAlerts = () => authClient.request<Subscription>('/fuel-news/subscription', { method: 'DELETE' });

export function listenForNewsNotifications(open: (id: string) => void) {
  if (!pushAvailable()) return () => {};
  Notifications.setNotificationHandler({ handleNotification: async () => ({
    shouldShowBanner: true, shouldShowList: true, shouldPlaySound: false, shouldSetBadge: false,
  }) });
  let lastIdentifier: string | undefined;
  const handle = (response: Notifications.NotificationResponse | null) => {
    if (!response || lastIdentifier === response.notification.request.identifier) return;
    lastIdentifier = response.notification.request.identifier;
    const id = response.notification.request.content.data?.newsId;
    if (typeof id === 'string') open(id);
  };
  let active = true;
  void Notifications.getLastNotificationResponseAsync().then(response => {
    if (active) { handle(response); void Notifications.clearLastNotificationResponseAsync(); }
  }).catch(() => {});
  const subscription = Notifications.addNotificationResponseReceivedListener(handle);
  return () => { active = false; subscription.remove(); };
}
