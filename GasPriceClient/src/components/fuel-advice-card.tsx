import { Link } from 'expo-router';
import { useState } from 'react';
import { ActivityIndicator, Linking, Platform, Pressable, StyleSheet, View } from 'react-native';
import { ThemedText } from '@/components/themed-text';
import { AppIcon } from '@/components/app-icon';
import { useTheme } from '@/hooks/use-theme';
import { adviceHeadline, adviceImpact, adviceTime, type AdviceFuel, type FuelAdvice } from '@/fuel/advice';
import { changeLabel, newsDate, newsIdPattern } from '@/news/api';

export function FuelAdviceCard({ advice, loading, error, reload, fuelType, onFuelChange, oilCompany,
  onCompanyChange, companies }: {
  advice: FuelAdvice | null; loading: boolean; error: boolean; reload: () => Promise<void>;
  fuelType: AdviceFuel; onFuelChange: (fuel: AdviceFuel) => void; oilCompany: string | null;
  onCompanyChange: (company: string | null) => void; companies: string[];
}) {
  const theme = useTheme();
  const [showCompanies, setShowCompanies] = useState(false);
  const [showDetails, setShowDetails] = useState(false);
  const status = advice?.status ?? 'unknown';
  const accent = status === 'gas_now' ? theme.advicePositive : status === 'wait' ? theme.adviceWait : theme.textSecondary;
  const impact = advice ? adviceImpact(advice) : null;
  const evidence = advice?.evidence[0];
  const options = [...new Set([...companies, ...(oilCompany ? [oilCompany] : [])])].sort((a, b) => a.localeCompare(b));
  const sources = [...new Set((advice?.evidence ?? []).flatMap(e => e.sourceUrls))]
    .filter(url => { try { return new URL(url).protocol === 'https:'; } catch { return false; } });
  const newsId = advice?.evidence.find(e => e.newsId && newsIdPattern.test(e.newsId))?.newsId;
  return <View style={[styles.card, { backgroundColor: theme.backgroundElement, borderColor: accent }]}>
    <View style={styles.topline}>
      <View style={styles.label}><AppIcon name="fuel" color={accent} /><ThemedText type="smallBold">SHOULD I GAS UP?</ThemedText></View>
      <Pressable accessibilityRole="button" accessibilityLabel="Refresh fuel advice" disabled={loading}
        accessibilityState={{ disabled: loading }} onPress={() => void reload()}
        style={({ pressed, hovered }) => [styles.textButton, (pressed || hovered || loading) && styles.dim]}>
        {loading ? <ActivityIndicator size="small" color={theme.textSecondary} /> : <ThemedText type="smallBold" themeColor="textSecondary">Refresh ↻</ThemedText>}
      </Pressable>
    </View>

    <View style={styles.controls}>
      <View style={[styles.fuels, { backgroundColor: theme.background }]} accessibilityLabel="Fuel type">
        {(['gasoline', 'diesel', 'kerosene'] as const).map(fuel => <Pressable key={fuel} accessibilityRole="button"
          accessibilityLabel={fuel[0].toUpperCase() + fuel.slice(1)} accessibilityState={{ selected: fuelType === fuel }}
          onPress={() => onFuelChange(fuel)} style={({ pressed, hovered }) => [styles.fuel,
            fuelType === fuel && { backgroundColor: theme.backgroundSelected }, (pressed || hovered) && styles.dim]}>
          <ThemedText type="smallBold" themeColor={fuelType === fuel ? 'text' : 'textSecondary'}>{fuel[0].toUpperCase() + fuel.slice(1)}</ThemedText>
        </Pressable>)}
      </View>
      {options.length > 0 ? <Pressable accessibilityRole="button" accessibilityLabel="Choose company for fuel advice"
        accessibilityState={{ expanded: showCompanies }} onPress={() => setShowCompanies(value => !value)}
        style={[styles.scope, { borderColor: theme.backgroundSelected }]}>
        <ThemedText type="smallBold">{oilCompany ?? 'Nationwide'} {showCompanies ? '▴' : '▾'}</ThemedText>
      </Pressable> : <ThemedText type="small" themeColor="textSecondary">Nationwide advice</ThemedText>}
    </View>
    {showCompanies && <View style={styles.companyOptions}>
      {[null, ...options].map(company => <Pressable key={company ?? 'nationwide'} accessibilityRole="button"
        accessibilityState={{ selected: oilCompany === company }} onPress={() => { onCompanyChange(company); setShowCompanies(false); }}
        style={[styles.scope, { backgroundColor: oilCompany === company ? theme.backgroundSelected : theme.background }]}>
        <ThemedText type="small">{company ?? 'Nationwide'}</ThemedText>
      </Pressable>)}
    </View>}

    <View style={styles.body}>
      <View style={styles.recommendation} accessibilityLiveRegion="polite">
        <ThemedText accessibilityRole="header" style={[styles.headline, { color: accent }]}>{adviceHeadline(advice, loading, error)}</ThemedText>
        <ThemedText>{advice?.message ?? (loading ? 'Looking up the latest fuel-price advice for your selection.'
          : error ? 'We couldn’t load fuel advice. Check your connection and try again.' : 'No current confirmed update is available.')}</ThemedText>
        {evidence && advice?.reason !== 'conflicting_adjustments' && <ThemedText type="small" themeColor="textSecondary">
          Effective: {adviceTime(evidence)}{evidence.isEffectiveTimeDefaulted ? '\n6:00 AM PHT default used' : ''}
        </ThemedText>}
        {advice?.forecast && <ThemedText type="small" themeColor="textSecondary">Forecast: {changeLabel({
          fuel: fuelType, oilCompany: advice.forecast.oilCompany, status: 'forecast',
          minChangePerLiter: advice.forecast.minChangePerLiter, maxChangePerLiter: advice.forecast.maxChangePerLiter,
        })}. Subject to change. Expected date: {adviceTime(advice.forecast)}. A forecast does not confirm when to refuel.</ThemedText>}
      </View>
      {impact && <View style={[styles.impact, { backgroundColor: theme.background, borderColor: theme.backgroundSelected }]}>
        <ThemedText type="small" themeColor="textSecondary">{impact.label}</ThemedText>
        <ThemedText style={[styles.savings, { color: accent }]}>₱{impact.total.toFixed(2)}</ThemedText>
        <ThemedText type="small" themeColor="textSecondary">₱{impact.amount.toFixed(2)}/L {evidence!.minChangePerLiter < 0 ? 'decrease' : 'increase'} · Example fill</ThemedText>
      </View>}
    </View>

    <View style={[styles.footer, { borderTopColor: theme.backgroundSelected }]}>
      <View style={styles.metadata}>
        <ThemedText type="small" themeColor="textSecondary">{advice ? `${advice.evidence.length ? 'Confirmed adjustment' : advice.forecast ? 'Forecast only' : 'No confirmed update'} · Checked ${newsDate(advice.evaluatedAtUtc, true)} PHT` : 'Price-based advice'}</ThemedText>
        <ThemedText type="small" themeColor="textSecondary">{oilCompany ? `${oilCompany} announcements and nationwide updates.` : 'Nationwide announcements. Individual companies may differ.'} Keep enough fuel for your trip.</ThemedText>
      </View>
      <View style={styles.actions}>
        <Link href="/fuel-prices" asChild><Pressable accessibilityRole="link" style={StyleSheet.flatten([styles.primaryAction, { backgroundColor: theme.backgroundSelected, borderColor: accent }])}>
          <ThemedText type="smallBold" style={{ color: accent }}>Compare fuel prices →</ThemedText>
        </Pressable></Link>
        {(advice?.evidence.length ?? 0) > 0 && <Pressable accessibilityRole="button" accessibilityState={{ expanded: showDetails }}
          onPress={() => setShowDetails(value => !value)} style={styles.textButton}>
          <ThemedText type="smallBold">{showDetails ? 'Hide sources ↑' : status === 'wait' ? 'Why wait? ↓' : 'Why this advice? ↓'}</ThemedText>
        </Pressable>}
        {status === 'wait' && Platform.OS !== 'web' && <Link href="/news" style={styles.textButton}><ThemedText type="smallBold">Fuel news & alerts →</ThemedText></Link>}
      </View>
    </View>
    {showDetails && advice && <View style={[styles.details, { borderTopColor: theme.backgroundSelected }]}>
      <ThemedText type="smallBold">Source announcements</ThemedText>
      {newsId && <Link href={{ pathname: '/news-detail', params: { id: newsId } }} asChild><Pressable accessibilityRole="link" style={styles.textButton}>
        <ThemedText type="smallBold" style={{ color: accent }}>Read the full fuel update →</ThemedText>
      </Pressable></Link>}
      {sources.map((url, index) => <Pressable key={url} accessibilityRole="link" accessibilityLabel={`Open source ${index + 1}: ${new URL(url).hostname}`}
        onPress={() => void Linking.openURL(url).catch(() => {})} style={styles.textButton}>
        <ThemedText type="small" style={{ color: accent }}>Source {index + 1} · {new URL(url).hostname} ↗</ThemedText>
      </Pressable>)}
      <ThemedText type="small" themeColor="textSecondary">{advice.evidence.some(e => e.isEffectiveTimeDefaulted)
        ? 'The effective time uses the app’s 6:00 AM Philippine-time default because the announcement supplied only a date.'
        : 'Effective dates and times come from the stored announcements.'}</ThemedText>
    </View>}
  </View>;
}

const styles = StyleSheet.create({
  card: { borderWidth: 1, borderRadius: 20, padding: 20, gap: 20 },
  topline: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', justifyContent: 'space-between', gap: 8 },
  label: { flexDirection: 'row', alignItems: 'center', gap: 10 },
  controls: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', justifyContent: 'space-between', gap: 12 },
  fuels: { flexDirection: 'row', flexWrap: 'wrap', borderRadius: 12, padding: 4, gap: 4 },
  fuel: { minHeight: 44, paddingVertical: 12, paddingHorizontal: 12, borderRadius: 9, justifyContent: 'center' },
  scope: { minHeight: 44, borderWidth: 1, borderRadius: 10, paddingVertical: 11, paddingHorizontal: 14, justifyContent: 'center' },
  companyOptions: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
  body: { flexDirection: 'row', flexWrap: 'wrap', gap: 24, alignItems: 'flex-start' },
  recommendation: { flexGrow: 1, flexShrink: 1, flexBasis: 360, gap: 12, minWidth: 0 },
  headline: { fontSize: 28, lineHeight: 36, fontWeight: '700', letterSpacing: -0.5 },
  impact: { flexBasis: 230, flexGrow: 1, flexShrink: 1, padding: 18, borderRadius: 14, borderWidth: 1, gap: 8, minWidth: 0 },
  savings: { fontSize: 26, lineHeight: 34, fontWeight: '700', fontVariant: ['tabular-nums'] },
  footer: { borderTopWidth: 1, paddingTop: 16, gap: 16 }, metadata: { gap: 6 },
  actions: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', gap: 12 },
  primaryAction: { minHeight: 44, paddingVertical: 12, paddingHorizontal: 16, borderWidth: 1, borderRadius: 10, justifyContent: 'center' },
  textButton: { minHeight: 44, paddingVertical: 10, justifyContent: 'center', alignSelf: 'flex-start' },
  details: { borderTopWidth: 1, paddingTop: 16, gap: 8 }, dim: { opacity: 0.65 },
});
