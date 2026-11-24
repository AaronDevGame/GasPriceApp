import { Image } from 'expo-image';
import { Link, usePathname, useRouter } from 'expo-router';
import { useEffect, useState } from 'react';
import { Modal, Pressable, ScrollView, StyleSheet, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import { FuelAdviceCard } from '@/components/fuel-advice-card';
import { ThemedText } from '@/components/themed-text';
import { adviceSummary, type AdviceFuel } from '@/fuel/advice';
import { useFuelAdvice } from '@/fuel/use-fuel-advice';
import { formatPrice, getFuelAdjustments, type FuelAdjustmentWeek } from '@/fuel/api';
import { headerPrice, useHeaderComparison, useHeaderPrices, type HeaderFuel } from '@/fuel/header-prices';
import { useAuth } from '@/auth/auth-provider';
import { useTheme } from '@/hooks/use-theme';
import { reportDate } from '@/map/labels';

const fuelNozzles = {
  gasoline: require('@/assets/images/fuel-nozzles/gasoline-green.png'),
  diesel: require('@/assets/images/fuel-nozzles/diesel-yellow.png'),
};

function AdviceNotice({ fuel, onOpen, active, compact }: { fuel: HeaderFuel; onOpen: () => void; active: boolean; compact: boolean }) {
  const theme = useTheme();
  const { advice, loading, error } = useFuelAdvice(fuel, null, active);
  const prices = useHeaderPrices();
  const price = headerPrice(prices, fuel);
  const amount = price ? `${formatPrice(price.range)}/L` : null;
  const priceLabel = price ? price.estimated ? `${price.area} estimate`
    : `${price.area} · DOE${price.baselineDate ? ` ${reportDate(price.baselineDate)}` : ''}${price.outdated ? ' · Outdated' : ''}`
    : prices.loading ? 'Checking prices…' : 'Price unavailable';
  const [focused, setFocused] = useState(false);
  const label = fuel === 'gasoline' ? 'Gasoline 91' : 'Diesel';
  const status = advice?.status;
  const color = status === 'gas_now' ? theme.advicePositive
    : status === 'wait' || advice?.reason === 'forecast_only' ? theme.adviceWait : theme.textSecondary;
  const summary = adviceSummary(advice, loading, error);
  const changeColor = advice?.reason === 'forecast_only' ? theme.adviceWait
    : summary.direction === 'down' ? theme.advicePositive : summary.direction === 'up' ? theme.priceIncrease : theme.textSecondary;
  const arrow = summary.direction === 'down' ? '↓' : summary.direction === 'up' ? '↑' : summary.direction === 'flat' ? '↔' : '';
  const change = summary.direction !== 'none' ? `${arrow} ${summary.amount}`.trim() : null;
  const notice = !advice ? loading ? 'Checking…' : error ? 'Tap to retry' : 'View advice'
    : advice.reason === 'rollback_effective' ? 'Rollback in effect'
    : status === 'gas_now' ? 'Gas up now' : status === 'wait' ? 'Wait for rollback' : status === 'neutral' ? 'No rush to fill' : summary.action;
  const priceView = <View style={styles.price}>
    {amount && <ThemedText type="smallBold" style={styles.amount}>{amount}</ThemedText>}
    <ThemedText type="small" themeColor="textSecondary" style={styles.priceLabel}>{priceLabel}</ThemedText>
  </View>;
  return <Pressable accessibilityRole="button"
    accessibilityLabel={[fuel === 'gasoline' ? 'Gasoline RON 91' : label, amount, priceLabel, change, summary.timing, notice].filter(Boolean).join('. ')}
    accessibilityHint="Opens Should I gas up with this fuel selected"
    onPress={onOpen} onFocus={() => setFocused(true)} onBlur={() => setFocused(false)}
    style={({ hovered, pressed }) => [styles.notice, { backgroundColor: theme.backgroundElement,
      borderColor: focused || hovered ? '#76B4FF' : theme.backgroundSelected }, pressed && { opacity: 0.75 }]}>
    <View style={styles.noticeTop}>
      <View style={[styles.noticeRow, styles.noticeMain]}>
        <View style={styles.fuelLabel}>
          <Image source={fuelNozzles[fuel]} style={styles.nozzle} contentFit="contain"
            tintColor={fuel === 'gasoline' ? '#64D99B' : '#F2CB45'} accessible={false} />
          <ThemedText type="smallBold" style={styles.headingText}>{label}</ThemedText>
        </View>
        {change && <ThemedText type="smallBold" style={[styles.headingText, { color: changeColor }]}>{change}</ThemedText>}
        {!compact && priceView}
      </View>
      <ThemedText themeColor="textSecondary">›</ThemedText>
    </View>
    {compact && priceView}
    <View style={styles.noticeRow}>
      {!!summary.timing && <ThemedText type="small" themeColor="textSecondary">{summary.timing}</ThemedText>}
      <ThemedText type="smallBold" style={{ color }}>{notice}</ThemedText>
    </View>
  </Pressable>;
}

function AdvicePanel({ initialFuel, onClose }: { initialFuel: HeaderFuel; onClose: () => void }) {
  const theme = useTheme();
  const pathname = usePathname();
  const router = useRouter();
  const { setComparison } = useHeaderComparison();
  const [fuel, setFuel] = useState<AdviceFuel>(initialFuel);
  const advice = useFuelAdvice(fuel, null);
  const prices = useHeaderPrices();
  const price = fuel === 'kerosene' ? null : headerPrice(prices, fuel);
  const { session } = useAuth();
  const authenticated = session?.state === 'authenticated';
  const [week, setWeek] = useState<FuelAdjustmentWeek | null>();
  const [weekUnavailable, setWeekUnavailable] = useState(false);
  const [refreshCount, setRefreshCount] = useState(0);
  useEffect(() => {
    if (!authenticated) return;
    let active = true;
    void getFuelAdjustments(1).then(feed => {
      if (active) {
        setWeek(feed.groups[0] ?? null);
        setWeekUnavailable(false);
      }
    }, () => { if (active) setWeekUnavailable(true); });
    return () => { active = false; };
  }, [authenticated, refreshCount]);
  const reload = () => {
    setRefreshCount(count => count + 1);
    return advice.reload();
  };
  const compare = () => {
    if (fuel !== 'kerosene') setComparison({ fuel, location: price?.location });
    onClose();
    if (pathname !== '/') router.navigate('/');
  };
  return <SafeAreaView style={styles.overlay}>
    <Pressable accessibilityRole="button" accessibilityLabel="Close refueling advice" onPress={onClose} style={StyleSheet.absoluteFill} />
    <View accessibilityViewIsModal style={[styles.panel, { backgroundColor: theme.background }]}>
      <View style={styles.panelHeader}>
        <ThemedText accessibilityRole="header" style={styles.title}>Should I gas up?</ThemedText>
        <Pressable accessibilityRole="button" accessibilityLabel="Close refueling advice" onPress={onClose}
          style={({ hovered, pressed }) => [styles.close, (hovered || pressed) && { backgroundColor: theme.backgroundSelected }]}>
          <ThemedText type="smallBold">Close ×</ThemedText>
        </Pressable>
      </View>
      <ScrollView contentContainerStyle={styles.panelContent} keyboardShouldPersistTaps="handled">
        {price && <View style={[styles.priceDetails, { backgroundColor: theme.backgroundElement }]}>
          <ThemedText type="smallBold">{price.area}{fuel === 'gasoline' ? ' · Gasoline RON 91' : ' · Diesel'} · {formatPrice(price.range)}/L</ThemedText>
          <ThemedText type="small" themeColor="textSecondary">
            {price.estimated ? 'Estimated using confirmed adjustments' : 'DOE reported range'}
            {price.baselineDate ? ` · DOE data through ${reportDate(price.baselineDate)}` : ''}
            {price.outdated ? ' · Outdated' : ''}
          </ThemedText>
          <ThemedText type="small" themeColor="textSecondary">Actual prices vary by station.</ThemedText>
        </View>}
        <FuelAdviceCard {...advice} reload={reload} fuelType={fuel} onFuelChange={setFuel}
          weeklyAdjustments={authenticated ? week ?? null : null}
          weeklyAdjustmentsLoading={authenticated && week === undefined && !weekUnavailable}
          weeklyAdjustmentsUnavailable={!authenticated || weekUnavailable}
          onCompare={fuel === 'kerosene' ? undefined : compare}
          onNavigate={onClose} />
        <Link href="/weekly-changes" onPress={onClose} style={styles.weeklyLink}>Weekly adjustments ↗</Link>
      </ScrollView>
    </View>
  </SafeAreaView>;
}

export function FuelAdviceNotices({ compact = false, active = true }: { compact?: boolean; active?: boolean }) {
  const [selectedFuel, setSelectedFuel] = useState<HeaderFuel | null>(null);
  const close = () => setSelectedFuel(null);
  return <View style={[styles.notices, !compact && { flexBasis: 340 }]}>
    <AdviceNotice active={active} compact={compact} fuel="gasoline" onOpen={() => setSelectedFuel('gasoline')} />
    <AdviceNotice active={active} compact={compact} fuel="diesel" onOpen={() => setSelectedFuel('diesel')} />
    <Modal visible={selectedFuel !== null} transparent animationType="fade" onRequestClose={close}>
      {selectedFuel && <AdvicePanel initialFuel={selectedFuel} onClose={close} />}
    </Modal>
  </View>;
}

const styles = StyleSheet.create({
  notices: { flexDirection: 'row', flexGrow: 1, flexShrink: 1, gap: 10 },
  notice: { flex: 1, minWidth: 0, borderWidth: 1, borderRadius: 13, paddingHorizontal: 10, paddingVertical: 8, gap: 3 },
  noticeRow: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', gap: 6 },
  noticeTop: { flexDirection: 'row', alignItems: 'flex-start', gap: 5 },
  noticeMain: { flex: 1, minWidth: 0, gap: 4 },
  fuelLabel: { flexDirection: 'row', alignItems: 'center', gap: 4 },
  nozzle: { width: 18, height: 18, flexShrink: 0 },
  headingText: { fontSize: 13 },
  price: { flexShrink: 1, maxWidth: '100%' },
  amount: { fontSize: 13, fontVariant: ['tabular-nums'] },
  priceLabel: { fontSize: 11, lineHeight: 15 },
  priceDetails: { padding: 14, borderRadius: 12, gap: 4 },
  overlay: { flex: 1, justifyContent: 'center', alignItems: 'center', backgroundColor: 'rgba(0,0,0,0.65)', padding: 16 },
  panel: { width: '100%', maxWidth: 960, maxHeight: '100%', flexShrink: 1, borderRadius: 20, overflow: 'hidden' },
  panelHeader: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: 12, padding: 16 },
  title: { fontSize: 22, lineHeight: 30, fontWeight: '700', flexShrink: 1 },
  close: { minHeight: 44, paddingHorizontal: 12, justifyContent: 'center', borderRadius: 8 },
  panelContent: { padding: 12, paddingTop: 0, gap: 12 },
  weeklyLink: { color: '#76B4FF', fontSize: 14, fontWeight: '600', padding: 12 },
});
