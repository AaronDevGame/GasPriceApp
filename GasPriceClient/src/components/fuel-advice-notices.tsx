import { Image } from 'expo-image';
import { Link, usePathname, useRouter } from 'expo-router';
import { useEffect, useState } from 'react';
import { Modal, Pressable, ScrollView, StyleSheet, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import { AppIcon } from '@/components/app-icon';
import { FuelAdviceCard } from '@/components/fuel-advice-card';
import { ThemedText } from '@/components/themed-text';
import { adviceSummary, type AdviceFuel } from '@/fuel/advice';
import { useFuelAdvice } from '@/fuel/use-fuel-advice';
import { formatPrice, getFuelAdjustments, type FuelAdjustmentWeek } from '@/fuel/api';
import { headerArea, headerPrice, useHeaderComparison, useHeaderPrices, type HeaderFuel } from '@/fuel/header-prices';
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
  const area = price?.area ?? headerArea(prices.area);
  const amount = price ? `${formatPrice(price.range)}/L` : null;
  const priceLabel = price ? price.estimated ? `${price.area} estimate`
    : `${price.area} · DOE${price.baselineDate ? ` ${reportDate(price.baselineDate)}` : ''}${price.outdated ? ' · Outdated' : ''}`
    : [area, prices.loading ? 'Checking prices…' : 'Price unavailable'].filter(Boolean).join(' · ');
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
  const notice = advice ? summary.updateStatus : loading ? 'Checking…' : error ? 'Tap to retry' : 'View advice';
  const priceView = <View style={!compact && styles.desktopPrice}>
    {amount && <ThemedText type="smallBold" style={styles.amount}>{amount}</ThemedText>}
    <View style={styles.priceCaption}>
      {area && <AppIcon name="map-pin" color={theme.textSecondary} />}
      <ThemedText type="small" themeColor="textSecondary" style={styles.priceLabel}>{priceLabel}</ThemedText>
    </View>
  </View>;
  return <Pressable accessibilityRole="button"
    accessibilityLabel={[fuel === 'gasoline' ? 'Gasoline RON 91' : label, amount, priceLabel, change, notice].filter(Boolean).join('. ')}
    accessibilityHint="Opens Should I gas up with this fuel selected"
    onPress={onOpen} onFocus={() => setFocused(true)} onBlur={() => setFocused(false)}
    style={({ hovered, pressed }) => [styles.notice, compact && styles.compactNotice, { backgroundColor: theme.backgroundElement,
      borderColor: focused || hovered ? '#76B4FF' : theme.backgroundSelected }, pressed && { opacity: 0.75 }]}>
    <View style={styles.noticeTop}>
      <View style={styles.noticeMain}>
        <View style={[styles.fuelHeading, compact ? styles.compactFuelHeading : styles.desktopFuelHeading]}>
          <Image source={fuelNozzles[fuel]} style={styles.nozzle} contentFit="contain"
            tintColor={fuel === 'gasoline' ? '#64D99B' : '#F2CB45'} accessible={false} />
          <View style={[styles.fuelText, compact && styles.compactFuelText]}>
            <ThemedText type="smallBold" style={styles.headingText}>{label}</ThemedText>
            {change && <ThemedText type="smallBold" style={[styles.headingText, { color: changeColor }]}>{change}</ThemedText>}
          </View>
        </View>
        {!compact && priceView}
      </View>
      <ThemedText themeColor="textSecondary">›</ThemedText>
    </View>
    {compact && priceView}
    <View style={styles.noticeRow}>
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
        <FuelAdviceCard {...advice} reload={reload} fuelType={fuel} onFuelChange={setFuel}
          weeklyAdjustments={authenticated ? week ?? null : null}
          weeklyAdjustmentsLoading={authenticated && week === undefined && !weekUnavailable}
          weeklyAdjustmentsUnavailable={!authenticated || weekUnavailable}
          onCompare={fuel === 'kerosene' ? undefined : compare}
          onNavigate={onClose}
          priceDetails={price && <View style={[styles.priceDetails, {
            backgroundColor: theme.background, borderColor: theme.backgroundSelected,
          }]}>
            <ThemedText type="smallBold">{price.area}{fuel === 'gasoline' ? ' · Gasoline RON 91' : ' · Diesel'} · {formatPrice(price.range)}/L</ThemedText>
            <ThemedText type="small" themeColor="textSecondary">
              {price.localEstimate ? 'Local estimate' : price.estimated ? 'Estimated using confirmed adjustments' : 'DOE reported range'}
              {price.baselineDate ? ` · DOE data through ${reportDate(price.baselineDate)}` : ''}
              {price.outdated ? ' · Outdated' : ''}
            </ThemedText>
            <ThemedText type="small" themeColor="textSecondary">Actual prices vary by station.</ThemedText>
          </View>} />
        <Link href="/weekly-changes" onPress={onClose} style={styles.weeklyLink}>Weekly adjustments ↗</Link>
      </ScrollView>
    </View>
  </SafeAreaView>;
}

export function FuelAdviceNotices({ compact = false, active = true }: { compact?: boolean; active?: boolean }) {
  const [selectedFuel, setSelectedFuel] = useState<HeaderFuel | null>(null);
  const close = () => setSelectedFuel(null);
  return <View style={[styles.notices, compact ? styles.compactNotices : styles.desktopNotices]}>
    <AdviceNotice active={active} compact={compact} fuel="gasoline" onOpen={() => setSelectedFuel('gasoline')} />
    <AdviceNotice active={active} compact={compact} fuel="diesel" onOpen={() => setSelectedFuel('diesel')} />
    <Modal visible={selectedFuel !== null} transparent animationType="fade" onRequestClose={close}>
      {selectedFuel && <AdvicePanel initialFuel={selectedFuel} onClose={close} />}
    </Modal>
  </View>;
}

const styles = StyleSheet.create({
  notices: { flexDirection: 'row', flexGrow: 1, flexShrink: 1, gap: 10 },
  compactNotices: { flexWrap: 'wrap' },
  desktopNotices: { flexBasis: 340 },
  notice: { flex: 1, minWidth: 0, borderWidth: 1, borderRadius: 13, paddingHorizontal: 10, paddingVertical: 8, gap: 3 },
  compactNotice: { flexBasis: 205 },
  noticeRow: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', gap: 6 },
  noticeTop: { flexDirection: 'row', alignItems: 'flex-start', gap: 5 },
  noticeMain: { flex: 1, minWidth: 0, flexDirection: 'row', alignItems: 'flex-start', gap: 8 },
  fuelHeading: { flexDirection: 'row', gap: 4, minWidth: 0 },
  desktopFuelHeading: { flex: 1, alignItems: 'flex-start' },
  compactFuelHeading: { flexShrink: 1, alignItems: 'center' },
  fuelText: { flexShrink: 1, alignItems: 'flex-start' },
  compactFuelText: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', columnGap: 6 },
  nozzle: { width: 18, height: 18, flexShrink: 0 },
  headingText: { fontSize: 13 },
  desktopPrice: { flex: 1, minWidth: 0 },
  amount: { fontSize: 13, fontVariant: ['tabular-nums'] },
  priceCaption: { flexDirection: 'row', alignItems: 'flex-start', gap: 3 },
  priceLabel: { flexShrink: 1, fontSize: 11, lineHeight: 15 },
  priceDetails: { padding: 14, borderRadius: 12, borderWidth: 1, gap: 4 },
  overlay: { flex: 1, justifyContent: 'center', alignItems: 'center', backgroundColor: 'rgba(0,0,0,0.65)', padding: 16 },
  panel: { width: '100%', maxWidth: 960, maxHeight: '100%', flexShrink: 1, borderRadius: 20, overflow: 'hidden' },
  panelHeader: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: 12, padding: 16 },
  title: { fontSize: 22, lineHeight: 30, fontWeight: '700', flexShrink: 1 },
  close: { minHeight: 44, paddingHorizontal: 12, justifyContent: 'center', borderRadius: 8 },
  panelContent: { padding: 12, paddingTop: 0, gap: 12 },
  weeklyLink: { color: '#76B4FF', fontSize: 14, fontWeight: '600', padding: 12 },
});
