import { useCallback, useEffect, useRef, useState } from 'react';
import { ActivityIndicator, Animated, Easing, Image, Platform, Pressable, RefreshControl, ScrollView, StyleSheet, View } from 'react-native';
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

function weekLabel(start: string, end: string) {
  const startDate = new Date(`${start}T00:00:00`);
  const endDate = new Date(`${end}T00:00:00`);
  const startOptions: Intl.DateTimeFormatOptions = { month: 'short', day: 'numeric' };
  if (startDate.getFullYear() !== endDate.getFullYear()) startOptions.year = 'numeric';
  return `${startDate.toLocaleDateString('en-PH', startOptions)} – ${dateLabel(end)}`;
}

function stationBrand(value: string) {
  const key = value.trim().toLowerCase().replace(/[^a-z0-9]/g, '');
  switch (key) {
    case 'caltex': return { name: 'Caltex', logo: 'caltex' } as const;
    case 'cleanfuel': return { name: 'Cleanfuel', logo: 'cleanfuel' } as const;
    case 'ecooil': return { name: 'EcoOil', logo: 'ecooil' } as const;
    case 'cityoil': return { name: 'City Oil', logo: null } as const;
    default: return { name: value.trim(), logo: null } as const;
  }
}

function StationLogo({ name, logo }: { name: string; logo: 'caltex' | 'cleanfuel' | 'ecooil' | null }) {
  if (logo === 'caltex') return <Image source={require('@/assets/images/stations/caltex.png')} style={styles.logo} resizeMode="contain" accessibilityLabel="Caltex logo" />;
  if (logo === 'cleanfuel') return <View style={styles.logoCrop}>
    <Image source={require('@/assets/images/stations/cleanfuel.png')} style={styles.cleanfuelLogo} resizeMode="stretch" accessibilityLabel="Cleanfuel logo" />
  </View>;
  if (logo === 'ecooil') return <View style={styles.logoCrop}>
    <Image source={require('@/assets/images/stations/ecooil.png')} style={styles.ecooilLogo} resizeMode="stretch" accessibilityLabel="EcoOil logo" />
  </View>;
  return <View style={styles.logoFallback} accessible={false}>
    <ThemedText type="smallBold" style={styles.logoInitials}>{name.split(/\s+/).map(word => word[0]).slice(0, 2).join('').toUpperCase()}</ThemedText>
  </View>;
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
  const brand = stationBrand(item.oilCompany);
  return <ThemedView type="backgroundElement" style={styles.card}>
    <View style={styles.cardHeader}>
      <View style={styles.brand}>
        <StationLogo name={brand.name} logo={brand.logo} />
        <ThemedText style={styles.company}>{brand.name}</ThemedText>
      </View>
      <ThemedText type="small" themeColor="textSecondary" style={styles.effectiveDate}>Effective {dateLabel(item.effectiveDatePhilippines)}</ThemedText>
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
  const [selectedWeek, setSelectedWeek] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const weekScroll = useRef<ScrollView>(null);
  const weekViewportWidth = useRef(0);
  const weekTabLayouts = useRef<Record<string, { x: number; width: number }>>({});
  const suppressWeekPress = useRef(false);
  const [cardEntrance] = useState(() => new Animated.Value(1));
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

  const availableWeeks = feed?.groups ?? [];
  const activeWeek = availableWeeks.find(week => week.weekStart === selectedWeek) ?? availableWeeks[0];

  useEffect(() => {
    if (Platform.OS !== 'web' || availableWeeks.length === 0) return;
    const scrollNode = weekScroll.current?.getScrollableNode() as HTMLElement | null;
    if (!scrollNode) return;

    let pointerId: number | null = null;
    let startX = 0;
    let startScrollLeft = 0;
    let dragged = false;
    let releasePressTimeout: number | undefined;

    const onPointerDown = (event: PointerEvent) => {
      if (event.pointerType !== 'mouse' || event.button !== 0) return;
      if (event.clientY >= scrollNode.getBoundingClientRect().bottom - 12) return;
      window.clearTimeout(releasePressTimeout);
      suppressWeekPress.current = false;
      pointerId = event.pointerId;
      startX = event.clientX;
      startScrollLeft = scrollNode.scrollLeft;
      dragged = false;
    };
    const onPointerMove = (event: PointerEvent) => {
      if (event.pointerId !== pointerId) return;
      const distance = event.clientX - startX;
      if (!dragged && Math.abs(distance) < 5) return;
      if (!dragged) scrollNode.setPointerCapture(event.pointerId);
      dragged = true;
      suppressWeekPress.current = true;
      scrollNode.classList.add('is-dragging');
      scrollNode.scrollLeft = startScrollLeft - distance;
      event.preventDefault();
    };
    const onPointerEnd = (event: PointerEvent) => {
      if (event.pointerId !== pointerId) return;
      pointerId = null;
      scrollNode.classList.remove('is-dragging');
      if (dragged) releasePressTimeout = window.setTimeout(() => { suppressWeekPress.current = false; }, 0);
    };

    scrollNode.addEventListener('pointerdown', onPointerDown);
    window.addEventListener('pointermove', onPointerMove);
    window.addEventListener('pointerup', onPointerEnd);
    window.addEventListener('pointercancel', onPointerEnd);
    return () => {
      window.clearTimeout(releasePressTimeout);
      scrollNode.classList.remove('is-dragging');
      scrollNode.removeEventListener('pointerdown', onPointerDown);
      window.removeEventListener('pointermove', onPointerMove);
      window.removeEventListener('pointerup', onPointerEnd);
      window.removeEventListener('pointercancel', onPointerEnd);
    };
  }, [availableWeeks.length]);

  const selectWeek = (weekStart: string) => {
    const tab = weekTabLayouts.current[weekStart];
    if (tab) {
      weekScroll.current?.scrollTo({ x: Math.max(0, tab.x + tab.width / 2 - weekViewportWidth.current / 2), animated: true });
    }
    if (weekStart === activeWeek.weekStart) return;
    cardEntrance.stopAnimation();
    cardEntrance.setValue(0);
    setSelectedWeek(weekStart);
    Animated.timing(cardEntrance, {
      toValue: 1,
      duration: 240,
      easing: Easing.out(Easing.cubic),
      useNativeDriver: Platform.OS !== 'web',
    }).start();
  };

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
          {authLoading || (authenticated && loading) ? <View style={styles.loading}><ActivityIndicator /><ThemedText type="small">Loading weekly changes…</ThemedText></View> : null}
          {!authenticated && !authLoading && <ThemedText accessibilityRole="alert">{authError ?? 'You are signed out. Reopen the Gas Price tab to reconnect.'}</ThemedText>}
          {error && <ThemedText accessibilityRole="alert">{error} {Platform.OS === 'web' ? 'Reload this page to try again.' : 'Pull down to try again.'}</ThemedText>}
          {authenticated && !loading && !error && feed?.groups.length === 0 && <ThemedView type="backgroundElement" style={styles.card}>
            <ThemedText style={styles.company}>No weekly changes yet</ThemedText>
            <ThemedText>Changes will appear here when a DOE notice has been imported.</ThemedText>
          </ThemedView>}
          {availableWeeks.length > 0 && <View style={styles.week}>
            <ScrollView ref={weekScroll} id="adjustment-week-tabs" horizontal showsHorizontalScrollIndicator
              onLayout={event => { weekViewportWidth.current = event.nativeEvent.layout.width; }}
              contentContainerStyle={styles.weekTabs} accessibilityLabel="Adjustment weeks">
              {availableWeeks.map(week => {
                const selected = week.weekStart === activeWeek.weekStart;
                return <Pressable key={week.weekStart} accessibilityRole="tab" accessibilityState={{ selected }}
                  accessibilityLabel={weekLabel(week.weekStart, week.weekEnd)} onPress={() => {
                    if (!suppressWeekPress.current) selectWeek(week.weekStart);
                  }}
                  onLayout={event => { weekTabLayouts.current[week.weekStart] = event.nativeEvent.layout; }}
                  style={[styles.weekTab, { backgroundColor: selected ? theme.backgroundSelected : theme.backgroundElement },
                    selected && styles.selectedWeekTab]}>
                  <ThemedText type="smallBold" style={selected ? styles.selectedWeekTabText : undefined}>
                    {weekLabel(week.weekStart, week.weekEnd)}
                  </ThemedText>
                </Pressable>;
              })}
            </ScrollView>
            <Animated.View style={[styles.week, { opacity: cardEntrance,
              transform: [{ translateY: cardEntrance.interpolate({ inputRange: [0, 1], outputRange: [8, 0] }) }] }]}>
              {activeWeek.adjustments.map(item => <AdjustmentCard key={item.id} item={item} />)}
            </Animated.View>
          </View>}
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
  week: { gap: 12 },
  weekTabs: { gap: 8, paddingBottom: 8 },
  weekTab: { paddingHorizontal: 16, paddingVertical: 12, borderRadius: 12, borderWidth: 2, borderColor: 'transparent' },
  selectedWeekTab: { borderColor: '#208AEF' },
  selectedWeekTabText: { color: '#208AEF' },
  card: { padding: 20, borderRadius: 20, gap: 16 },
  cardHeader: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: 12 },
  brand: { flex: 1, minWidth: 0, flexDirection: 'row', alignItems: 'center', gap: 10 },
  logo: { width: 44, height: 44 }, logoCrop: { width: 44, height: 44, overflow: 'hidden' },
  cleanfuelLogo: { width: 301, height: 44, left: -2 },
  ecooilLogo: { width: 110, height: 44, left: -4 },
  logoFallback: { width: 44, height: 44, borderRadius: 12, backgroundColor: '#34526A', alignItems: 'center', justifyContent: 'center' },
  logoInitials: { color: '#FFFFFF' },
  company: { fontSize: 21, lineHeight: 28, fontWeight: '600', flexShrink: 1 },
  effectiveDate: { textAlign: 'right', flexShrink: 1 },
  changes: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
  change: { flexGrow: 1, flexBasis: 100, gap: 4 },
});
