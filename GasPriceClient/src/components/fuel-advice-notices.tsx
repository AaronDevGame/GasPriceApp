import { Link } from 'expo-router';
import { useEffect, useState } from 'react';
import { Modal, Pressable, ScrollView, StyleSheet, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import { FuelAdviceCard } from '@/components/fuel-advice-card';
import { ThemedText } from '@/components/themed-text';
import { adviceImpact, type AdviceFuel } from '@/fuel/advice';
import { useFuelAdvice } from '@/fuel/use-fuel-advice';
import { getDoePriceBrowse } from '@/fuel/api';
import { useAuth } from '@/auth/auth-provider';
import { useTheme } from '@/hooks/use-theme';
import { useLatestNews } from '@/news/use-latest-news';

type HeaderFuel = Extract<AdviceFuel, 'gasoline' | 'diesel'>;

function AdviceNotice({ fuel, onOpen }: { fuel: HeaderFuel; onOpen: () => void }) {
  const theme = useTheme();
  const { advice, loading, error } = useFuelAdvice(fuel, null);
  const [focused, setFocused] = useState(false);
  const label = fuel === 'gasoline' ? 'Gasoline' : 'Diesel';
  const status = advice?.status;
  const color = status === 'gas_now' ? theme.advicePositive : status === 'wait' ? theme.adviceWait : theme.textSecondary;
  const impact = advice ? adviceImpact(advice) : null;
  const change = impact ? `${advice!.evidence[0].minChangePerLiter < 0 ? '↓' : '↑'} ₱${impact.amount.toFixed(2)}/L` : null;
  const notice = !advice ? loading ? 'Checking…' : error ? 'Tap to retry' : 'View advice'
    : status === 'gas_now' ? 'Gas up now' : status === 'wait' ? 'Wait for rollback' : status === 'neutral' ? 'No rush to fill' : 'View update';
  const date = advice?.evidence[0]?.effectiveDatePhilippines;
  const dateLabel = date ? new Date(`${date}T12:00:00+08:00`).toLocaleDateString('en-PH', {
    month: 'short', day: 'numeric', timeZone: 'Asia/Manila',
  }) : null;
  return <Pressable accessibilityRole="button" accessibilityLabel={`${label}: ${change ?? ''} ${notice}. Open refueling advice`}
    accessibilityHint="Opens Should I gas up with this fuel selected"
    onPress={onOpen} onFocus={() => setFocused(true)} onBlur={() => setFocused(false)}
    style={({ hovered, pressed }) => [styles.notice, { backgroundColor: theme.backgroundElement,
      borderColor: focused || hovered ? '#76B4FF' : theme.backgroundSelected }, pressed && { opacity: 0.75 }]}>
    <View style={styles.noticeRow}>
      <ThemedText type="smallBold">{label}</ThemedText>
      {change && <ThemedText type="smallBold" style={{ color }}>{change}</ThemedText>}
      <ThemedText themeColor="textSecondary" style={styles.chevron}>›</ThemedText>
    </View>
    <View style={styles.noticeRow}>
      {dateLabel && <ThemedText type="small" themeColor="textSecondary">{dateLabel}</ThemedText>}
      <ThemedText type="smallBold" style={{ color }}>{notice}</ThemedText>
    </View>
  </Pressable>;
}

function AdvicePanel({ initialFuel, onClose }: { initialFuel: HeaderFuel; onClose: () => void }) {
  const theme = useTheme();
  const [fuel, setFuel] = useState<AdviceFuel>(initialFuel);
  const [company, setCompany] = useState<string | null>(null);
  const advice = useFuelAdvice(fuel, company);
  const { session } = useAuth();
  const authenticated = session?.state === 'authenticated';
  const [doeCompanies, setDoeCompanies] = useState<string[]>([]);
  useEffect(() => {
    if (!authenticated) return;
    let active = true;
    void getDoePriceBrowse().then(feed => {
      if (active) setDoeCompanies(feed.selected?.prices.map(price => price.oilCompany) ?? []);
    }).catch(() => {});
    return () => { active = false; };
  }, [authenticated]);
  const news = useLatestNews();
  const companies = [...new Set([
    ...doeCompanies,
    ...(news.item?.article.adjustments.flatMap(change => change.oilCompany ? [change.oilCompany] : []) ?? []),
  ])];
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
        <FuelAdviceCard {...advice} fuelType={fuel} onFuelChange={setFuel} oilCompany={company}
          onCompanyChange={setCompany} companies={companies} onNavigate={onClose} />
        <Link href="/weekly-changes" onPress={onClose} style={styles.weeklyLink}>Weekly adjustments ↗</Link>
      </ScrollView>
    </View>
  </SafeAreaView>;
}

export function FuelAdviceNotices({ compact = false }: { compact?: boolean }) {
  const [selectedFuel, setSelectedFuel] = useState<HeaderFuel | null>(null);
  const close = () => setSelectedFuel(null);
  return <View style={[styles.notices, !compact && { flexBasis: 340 }]}>
    <AdviceNotice fuel="gasoline" onOpen={() => setSelectedFuel('gasoline')} />
    <AdviceNotice fuel="diesel" onOpen={() => setSelectedFuel('diesel')} />
    <Modal visible={selectedFuel !== null} transparent animationType="fade" onRequestClose={close}>
      {selectedFuel && <AdvicePanel initialFuel={selectedFuel} onClose={close} />}
    </Modal>
  </View>;
}

const styles = StyleSheet.create({
  notices: { flexDirection: 'row', flexGrow: 1, flexShrink: 1, gap: 10 },
  notice: { flex: 1, minWidth: 0, borderWidth: 1, borderRadius: 13, paddingHorizontal: 10, paddingVertical: 8, gap: 3 },
  noticeRow: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', gap: 6 },
  chevron: { marginLeft: 'auto' },
  overlay: { flex: 1, justifyContent: 'center', alignItems: 'center', backgroundColor: 'rgba(0,0,0,0.65)', padding: 16 },
  panel: { width: '100%', maxWidth: 960, maxHeight: '100%', flexShrink: 1, borderRadius: 20, overflow: 'hidden' },
  panelHeader: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: 12, padding: 16 },
  title: { fontSize: 22, lineHeight: 30, fontWeight: '700', flexShrink: 1 },
  close: { minHeight: 44, paddingHorizontal: 12, justifyContent: 'center', borderRadius: 8 },
  panelContent: { padding: 12, paddingTop: 0, gap: 12 },
  weeklyLink: { color: '#76B4FF', fontSize: 14, fontWeight: '600', padding: 12 },
});
