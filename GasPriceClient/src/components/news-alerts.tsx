import { useEffect, useState } from 'react';
import { Button, Switch, View } from 'react-native';
import { useAuth } from '@/auth/auth-provider';
import { ApiError } from '@/auth/http';
import { subscribeToForeground } from '@/auth/foreground';
import { disableAlerts, enableAlerts, getSubscription, pushAvailable, type Subscription } from '@/news/push';
import { ThemedText } from './themed-text';
import { ThemedView } from './themed-view';

export function NewsAlerts() {
  const { session } = useAuth();
  const [subscription, setSubscription] = useState<Subscription | null>(null);
  const [forecasts, setForecasts] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  useEffect(() => {
    if (session?.state !== 'authenticated' || !pushAvailable()) return;
    let active = true;
    const load = () => void getSubscription().then(value => {
      if (active) { setSubscription(value); setForecasts(value.includeForecasts); setMessage(null); }
    }, () => { if (active) setMessage('Alert preferences could not be loaded.'); });
    load();
    const unsubscribe = subscribeToForeground(load);
    return () => { active = false; unsubscribe(); };
  }, [session?.state]);
  if (!pushAvailable() || session?.state !== 'authenticated') return null;
  const update = async (enabled: boolean) => {
    setBusy(true); setMessage(null);
    try {
      const next = enabled ? await enableAlerts(forecasts) : await disableAlerts();
      setSubscription(next); setMessage(next.enabled ? 'Fuel alerts are enabled.' : 'Fuel alerts are off.');
    } catch (error) {
      setMessage(error instanceof ApiError ? 'Alert preferences could not be saved. Please try again.'
        : error instanceof Error ? error.message : 'Notifications are temporarily unavailable.');
    } finally { setBusy(false); }
  };
  return <ThemedView type="backgroundElement" style={{ padding: 20, borderRadius: 20, gap: 12 }}>
    <ThemedText type="smallBold">Fuel alerts</ThemedText>
    <ThemedText type="small">Receive confirmed fuel adjustments. Forecast alerts are optional.</ThemedText>
    <View style={{ flexDirection: 'row', alignItems: 'center', gap: 12 }}>
      <Switch value={forecasts} onValueChange={setForecasts} disabled={busy} accessibilityLabel="Include forecast alerts" />
      <ThemedText type="small">Include forecasts</ThemedText>
    </View>
    {subscription?.available === false && <ThemedText type="small">Fuel notifications are coming soon.</ThemedText>}
    <Button title={subscription?.enabled ? 'Save alert preferences' : 'Enable fuel alerts'}
      onPress={() => void update(true)} disabled={busy || !subscription?.available} />
    {subscription?.enabled && <Button title="Turn off fuel alerts" onPress={() => void update(false)} disabled={busy} />}
    {message && <ThemedText type="small" accessibilityLiveRegion="polite">{message}</ThemedText>}
  </ThemedView>;
}
