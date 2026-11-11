import { useState } from 'react';
import { Modal, Pressable, ScrollView, StyleSheet, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import { FuelAdviceCard } from '@/components/fuel-advice-card';
import { ThemedText } from '@/components/themed-text';
import { adviceSummary, type AdviceFuel } from '@/fuel/advice';
import { useFuelAdvice } from '@/fuel/use-fuel-advice';
import { useTheme } from '@/hooks/use-theme';

function AdviceDetails({ fuel, onFuelChange, onClose }: {
  fuel: AdviceFuel; onFuelChange: (fuel: AdviceFuel) => void; onClose: () => void;
}) {
  const advice = useFuelAdvice(fuel, null);
  return <FuelAdviceCard key={fuel} {...advice} fuelType={fuel} onFuelChange={onFuelChange}
    oilCompany={null} onCompanyChange={() => {}} companies={[]} initiallyShowDetails onNavigate={onClose} />;
}

export function FuelAdviceSummary({ inHeader = false }: { inHeader?: boolean }) {
  const theme = useTheme();
  const gasoline = useFuelAdvice('gasoline', null);
  const diesel = useFuelAdvice('diesel', null);
  const [selected, setSelected] = useState<AdviceFuel | null>(null);
  const results = { gasoline, diesel };
  return <View style={[styles.summary, inHeader && styles.header]} accessibilityLabel="Nationwide fuel adjustments">
    {(['gasoline', 'diesel'] as const).map(fuel => {
      const result = results[fuel];
      const summary = adviceSummary(result.advice, result.loading, result.error);
      const accent = result.advice?.status === 'gas_now' ? theme.advicePositive
        : result.advice?.status === 'wait' ? theme.adviceWait : theme.textSecondary;
      const directionColor = summary.direction === 'up' ? theme.priceIncrease
        : summary.direction === 'down' ? theme.advicePositive : theme.textSecondary;
      const arrow = summary.direction === 'up' ? '↑' : summary.direction === 'down' ? '↓' : summary.direction === 'flat' ? '↔' : '';
      const name = fuel === 'gasoline' ? 'Gasoline' : 'Diesel';
      return <Pressable key={fuel} accessibilityRole="button" accessibilityLabel={`${name}: ${summary.direction === 'up' ? 'increase' : summary.direction === 'down' ? 'decrease' : ''} ${summary.amount}. ${summary.action}. ${summary.timing}. Open advice and sources.`}
        accessibilityHint="Opens detailed nationwide fuel advice and source announcements"
        onPress={() => setSelected(fuel)}
        style={({ hovered, pressed }) => [styles.fuel, { backgroundColor: theme.backgroundElement, borderColor: theme.backgroundSelected },
          inHeader && styles.headerFuel,
          (hovered || pressed) && { borderColor: accent, backgroundColor: theme.backgroundSelected }]}>
        <View style={styles.row}>
          <ThemedText style={styles.name}>{name}</ThemedText>
          <ThemedText style={[styles.amount, { color: directionColor }]}>{arrow} {summary.amount}</ThemedText>
          <ThemedText themeColor="textSecondary" style={styles.chevron}>›</ThemedText>
        </View>
        <View style={[styles.badge, { backgroundColor: `${accent}18` }]}>
          <ThemedText style={[styles.action, { color: accent }]}>{summary.action}</ThemedText>
        </View>
        <ThemedText style={styles.timing} themeColor="textSecondary">{summary.timing}</ThemedText>
      </Pressable>;
    })}
    <Modal visible={selected !== null} transparent animationType="fade" onRequestClose={() => setSelected(null)}>
      <SafeAreaView style={styles.overlay}>
        <View style={[styles.dialog, { backgroundColor: theme.background }]} accessibilityViewIsModal>
          <View style={styles.dialogHeader}>
            <ThemedText accessibilityRole="header" type="smallBold">Nationwide fuel advice</ThemedText>
            <Pressable accessibilityRole="button" accessibilityLabel="Close fuel advice" onPress={() => setSelected(null)}
              style={styles.close}><ThemedText type="smallBold">Close ✕</ThemedText></Pressable>
          </View>
          <ScrollView contentContainerStyle={styles.details}>
            {selected && <AdviceDetails fuel={selected} onFuelChange={setSelected} onClose={() => setSelected(null)} />}
          </ScrollView>
        </View>
      </SafeAreaView>
    </Modal>
  </View>;
}

const styles = StyleSheet.create({
  summary: { flexDirection: 'row', flexWrap: 'wrap', gap: 10 },
  header: { flex: 1, maxWidth: 520, minWidth: 300 },
  headerFuel: { flexBasis: 140, padding: 10 },
  fuel: { flexGrow: 1, flexShrink: 1, flexBasis: 240, minWidth: 0, borderWidth: 1, borderRadius: 12, padding: 12, gap: 6 },
  row: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', gap: 8 },
  name: { fontSize: 13, lineHeight: 18, fontWeight: '700' },
  amount: { fontSize: 14, lineHeight: 20, fontWeight: '700', fontVariant: ['tabular-nums'] },
  chevron: { marginLeft: 'auto', fontSize: 20, lineHeight: 20 },
  badge: { alignSelf: 'flex-start', paddingHorizontal: 7, paddingVertical: 2, borderRadius: 5 },
  action: { fontSize: 12, lineHeight: 18, fontWeight: '700' },
  timing: { fontSize: 11, lineHeight: 16 },
  overlay: { flex: 1, backgroundColor: '#000000AA', justifyContent: 'center', alignItems: 'center', padding: 12 },
  dialog: { width: '100%', maxWidth: 840, maxHeight: '95%', borderRadius: 18, overflow: 'hidden' },
  dialogHeader: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', paddingHorizontal: 20, gap: 12 },
  close: { minHeight: 48, justifyContent: 'center', paddingHorizontal: 8 },
  details: { padding: 12 },
});
