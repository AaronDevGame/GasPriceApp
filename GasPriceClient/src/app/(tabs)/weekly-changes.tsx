import { useCallback, useEffect, useState } from 'react';
import { ActivityIndicator, Button, Platform, RefreshControl, ScrollView, StyleSheet, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import { useAuth } from '@/auth/auth-provider';
import { errorMessage } from '@/auth/http';
import { ThemedText } from '@/components/themed-text';
import { ThemedView } from '@/components/themed-view';
import { getFuelAdjustments, type FuelAdjustment, type FuelAdjustmentFeed } from '@/fuel/api';
import { useTheme } from '@/hooks/use-theme';

function dateLabel(value: string) {
  return new Date(`${value}T00:00:00`).toLocaleDateString('en-PH', { month: 'short', day: 'numeric', year: 'numeric' });
}

function Change({ label, amount }: { label: string; amount: number | null }) {
  const color = amount === null ? undefined : amount > 0 ? '#BA3636' : amount < 0 ? '#087443' : undefined;
  const value = amount === null ? 'Not listed' : amount > 0
    ? `↑ Increase ₱${amount.toFixed(2)}` : amount < 0 ? `↓ Decrease ₱${Math.abs(amount).toFixed(2)}` : 'No change';
  const spoken = amount === null ? 'not listed' : amount > 0
    ? `increase of ₱${amount.toFixed(2)} per liter` : amount < 0
      ? `decrease of ₱${Math.abs(amount).toFixed(2)} per liter` : 'no change';
  return <View style={styles.change} accessible accessibilityLabel={`${label}: ${spoken}`}>
    <ThemedText type="small" themeColor="textSecondary">{label}</ThemedText>
    <ThemedText type="smallBold" style={color ? { color } : undefined}>{value}</ThemedText>
  </View>;
}

function AdjustmentCard({ item }: { item: FuelAdjustment }) {
  return <ThemedView type="backgroundElement" style={styles.card}>
    <View style={styles.cardHeader}>
      <ThemedText style={styles.company}>{item.oilCompany}</ThemedText>
      <ThemedText type="small" themeColor="textSecondary">Effective {dateLabel(item.effectiveDatePhilippines)}</ThemedText>
    </View>
    <View style={styles.changes}>
      <Change label="Gasoline" amount={item.gasolineChangePerLiter} />
      <Change label="Diesel" amount={item.dieselChangePerLiter} />
      <Change label="Kerosene" amount={item.keroseneChangePerLiter} />
    </View>
  </ThemedView>;
}

export default function WeeklyChangesScreen() {
  const { session, loading: authLoading, error: authError } = useAuth();
  const theme = useTheme();
  const [feed, setFeed] = useState<FuelAdjustmentFeed | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const authenticated = session?.state === 'authenticated';

  const load = useCallback(async (refresh = false) => {
    if (!authenticated) return;
    if (refresh) setRefreshing(true);
    else setLoading(true);
    setError(null);
    try { setFeed(await getFuelAdjustments()); }
    catch (failure) { setError(errorMessage(failure)); }
    finally { setLoading(false); setRefreshing(false); }
  }, [authenticated]);

  useEffect(() => {
    if (!authenticated) return;
    let active = true;
    void getFuelAdjustments().then(
      result => { if (active) setFeed(result); },
      failure => { if (active) setError(errorMessage(failure)); },
    ).finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [authenticated]);

  const weeks = new Map<string, FuelAdjustment[]>();
  for (const item of feed?.items ?? []) {
    const key = `${item.weekStart}|${item.weekEnd}`;
    weeks.set(key, [...(weeks.get(key) ?? []), item]);
  }

  return <ThemedView style={styles.page}>
    <SafeAreaView style={styles.safe}>
      <ScrollView contentContainerStyle={styles.scroll}
        refreshControl={Platform.OS === 'web' ? undefined : <RefreshControl refreshing={refreshing}
          onRefresh={() => void load(true)} enabled={authenticated && !loading}
          tintColor={theme.text} colors={[theme.text]} progressBackgroundColor={theme.backgroundElement} />}
      >
        <View style={styles.content}>
          <View style={styles.header}>
            <ThemedText type="smallBold" style={styles.accent}>GASPRICE · PHILIPPINES</ThemedText>
            <ThemedText type="title" style={styles.title}>Weekly Changes</ThemedText>
            <ThemedText themeColor="textSecondary">Announced fuel price increases and decreases by oil company, in pesos per liter.</ThemedText>
          </View>
          {Platform.OS === 'web' && <Button title="Refresh changes" disabled={!authenticated || loading || refreshing} onPress={() => void load(true)} />}
          {authLoading || (authenticated && loading) ? <View style={styles.loading}><ActivityIndicator /><ThemedText type="small">Loading weekly changes…</ThemedText></View> : null}
          {!authenticated && !authLoading && <ThemedText accessibilityRole="alert">{authError ?? 'You are signed out. Reopen the Gas Price tab to reconnect.'}</ThemedText>}
          {error && <ThemedText accessibilityRole="alert">{error}</ThemedText>}
          {authenticated && !loading && !error && feed?.items.length === 0 && <ThemedView type="backgroundElement" style={styles.card}>
            <ThemedText style={styles.company}>No weekly changes yet</ThemedText>
            <ThemedText>Changes will appear here when a DOE notice has been imported.</ThemedText>
          </ThemedView>}
          {[...weeks].map(([key, items]) => {
            const [start, end] = key.split('|');
            return <View key={key} style={styles.week}>
              <ThemedText style={styles.weekTitle}>{dateLabel(start)} – {dateLabel(end)}</ThemedText>
              {items.map(item => <AdjustmentCard key={item.id} item={item} />)}
            </View>;
          })}
          <ThemedText type="small" themeColor="textSecondary">Based on DOE notices. These are changes per liter, not current pump prices.</ThemedText>
        </View>
      </ScrollView>
    </SafeAreaView>
  </ThemedView>;
}

const styles = StyleSheet.create({
  page: { flex: 1 }, safe: { flex: 1 }, scroll: { padding: 24, alignItems: 'center' },
  content: { width: '100%', maxWidth: 900, gap: 20, paddingVertical: 16 },
  header: { gap: 8, marginBottom: 12 }, accent: { color: '#208AEF' },
  title: { fontSize: 38, lineHeight: 44 }, loading: { flexDirection: 'row', gap: 12, alignItems: 'center' },
  week: { gap: 12 }, weekTitle: { fontSize: 20, lineHeight: 28, fontWeight: '700' },
  card: { padding: 20, borderRadius: 20, gap: 16 }, cardHeader: { gap: 4 },
  company: { fontSize: 21, lineHeight: 28, fontWeight: '600' },
  changes: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
  change: { flexGrow: 1, flexBasis: 100, gap: 4 },
});
