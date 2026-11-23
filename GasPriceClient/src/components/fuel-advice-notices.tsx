import { Image } from 'expo-image';
import { Link } from 'expo-router';
import { useEffect, useState } from 'react';
import { Modal, Pressable, ScrollView, StyleSheet, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import { FuelAdviceCard } from '@/components/fuel-advice-card';
import { ThemedText } from '@/components/themed-text';
import { adviceSummary, type AdviceFuel } from '@/fuel/advice';
import { useFuelAdvice } from '@/fuel/use-fuel-advice';
import { getFuelAdjustments, type FuelAdjustmentWeek } from '@/fuel/api';
import { useAuth } from '@/auth/auth-provider';
import { useTheme } from '@/hooks/use-theme';

type HeaderFuel = Extract<AdviceFuel, 'gasoline' | 'diesel'>;

const fuelNozzles = {
  gasoline: require('@/assets/images/fuel-nozzles/gasoline-green.png'),
  diesel: require('@/assets/images/fuel-nozzles/diesel-yellow.png'),
};

function AdviceNotice({ fuel, onOpen, active }: { fuel: HeaderFuel; onOpen: () => void; active: boolean }) {
  const theme = useTheme();
  const { advice, loading, error } = useFuelAdvice(fuel, null, active);
  const [focused, setFocused] = useState(false);
  const label = fuel === 'gasoline' ? 'Gasoline' : 'Diesel';
  const status = advice?.status;
  const color = status === 'gas_now' ? theme.advicePositive
    : status === 'wait' || advice?.reason === 'forecast_only' ? theme.adviceWait : theme.textSecondary;
  const summary = adviceSummary(advice, loading, error);
  const arrow = summary.direction === 'down' ? '↓' : summary.direction === 'up' ? '↑' : summary.direction === 'flat' ? '↔' : '';
  const change = summary.direction !== 'none' ? `${arrow} ${summary.amount}`.trim() : null;
  const notice = !advice ? loading ? 'Checking…' : error ? 'Tap to retry' : 'View advice'
    : status === 'gas_now' ? 'Gas up now' : status === 'wait' ? 'Wait for rollback' : status === 'neutral' ? 'No rush to fill' : summary.action;
  return <Pressable accessibilityRole="button" accessibilityLabel={`${label}: ${change ?? ''} ${summary.timing ? `${summary.timing}. ` : ''}${notice}. Open refueling advice`}
    accessibilityHint="Opens Should I gas up with this fuel selected"
    onPress={onOpen} onFocus={() => setFocused(true)} onBlur={() => setFocused(false)}
    style={({ hovered, pressed }) => [styles.notice, { backgroundColor: theme.backgroundElement,
      borderColor: focused || hovered ? '#76B4FF' : theme.backgroundSelected }, pressed && { opacity: 0.75 }]}>
    <View style={styles.noticeRow}>
      <View style={styles.fuelLabel}>
        <Image source={fuelNozzles[fuel]} style={styles.nozzle} contentFit="contain"
          tintColor={fuel === 'gasoline' ? '#64D99B' : '#F2CB45'} accessible={false} />
        <ThemedText type="smallBold">{label}</ThemedText>
      </View>
      {change && <ThemedText type="smallBold" style={{ color }}>{change}</ThemedText>}
      <ThemedText themeColor="textSecondary" style={styles.chevron}>›</ThemedText>
    </View>
    <View style={styles.noticeRow}>
      {!!summary.timing && <ThemedText type="small" themeColor="textSecondary">{summary.timing}</ThemedText>}
      <ThemedText type="smallBold" style={{ color }}>{notice}</ThemedText>
    </View>
  </Pressable>;
}

function AdvicePanel({ initialFuel, onClose }: { initialFuel: HeaderFuel; onClose: () => void }) {
  const theme = useTheme();
  const [fuel, setFuel] = useState<AdviceFuel>(initialFuel);
  const advice = useFuelAdvice(fuel, null);
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
    <AdviceNotice active={active} fuel="gasoline" onOpen={() => setSelectedFuel('gasoline')} />
    <AdviceNotice active={active} fuel="diesel" onOpen={() => setSelectedFuel('diesel')} />
    <Modal visible={selectedFuel !== null} transparent animationType="fade" onRequestClose={close}>
      {selectedFuel && <AdvicePanel initialFuel={selectedFuel} onClose={close} />}
    </Modal>
  </View>;
}

const styles = StyleSheet.create({
  notices: { flexDirection: 'row', flexGrow: 1, flexShrink: 1, gap: 10 },
  notice: { flex: 1, minWidth: 0, borderWidth: 1, borderRadius: 13, paddingHorizontal: 10, paddingVertical: 8, gap: 3 },
  noticeRow: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', gap: 6 },
  fuelLabel: { flexDirection: 'row', alignItems: 'center', gap: 5 },
  nozzle: { width: 20, height: 20, flexShrink: 0 },
  chevron: { marginLeft: 'auto' },
  overlay: { flex: 1, justifyContent: 'center', alignItems: 'center', backgroundColor: 'rgba(0,0,0,0.65)', padding: 16 },
  panel: { width: '100%', maxWidth: 960, maxHeight: '100%', flexShrink: 1, borderRadius: 20, overflow: 'hidden' },
  panelHeader: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: 12, padding: 16 },
  title: { fontSize: 22, lineHeight: 30, fontWeight: '700', flexShrink: 1 },
  close: { minHeight: 44, paddingHorizontal: 12, justifyContent: 'center', borderRadius: 8 },
  panelContent: { padding: 12, paddingTop: 0, gap: 12 },
  weeklyLink: { color: '#76B4FF', fontSize: 14, fontWeight: '600', padding: 12 },
});
