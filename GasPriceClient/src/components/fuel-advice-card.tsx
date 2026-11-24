import { Link } from 'expo-router';
import { useState, type ReactNode } from 'react';
import { ActivityIndicator, Linking, Platform, Pressable, StyleSheet, View } from 'react-native';
import { ThemedText } from '@/components/themed-text';
import { AppIcon } from '@/components/app-icon';
import { useTheme } from '@/hooks/use-theme';
import { adviceCurrentDecrease, adviceCurrentDecreaseContext, adviceHeadline, adviceImpact, adviceTime, forecastLabel,
  summarizeWeeklyAdjustments, weeklyAdjustmentLabel, type AdviceFuel, type FuelAdvice } from '@/fuel/advice';
import type { FuelAdjustmentWeek } from '@/fuel/api';
import { changeLabel, newsDate, newsIdPattern } from '@/news/api';

function weekDate(date: string) {
  return new Date(`${date}T12:00:00+08:00`).toLocaleDateString('en-PH', {
    timeZone: 'Asia/Manila', month: 'short', day: 'numeric', year: 'numeric',
  });
}

function companyCount(count: number) {
  return `${count} ${count === 1 ? 'company' : 'companies'}`;
}

export function FuelAdviceCard({ advice, loading, error, reload, fuelType, onFuelChange, weeklyAdjustments,
  weeklyAdjustmentsLoading, weeklyAdjustmentsUnavailable, initiallyShowDetails = false, onNavigate, onCompare, priceDetails }: {
  advice: FuelAdvice | null; loading: boolean; error: boolean; reload: () => Promise<void>;
  fuelType: AdviceFuel; onFuelChange: (fuel: AdviceFuel) => void;
  weeklyAdjustments?: FuelAdjustmentWeek | null;
  weeklyAdjustmentsLoading?: boolean; weeklyAdjustmentsUnavailable?: boolean;
  initiallyShowDetails?: boolean;
  onNavigate?: () => void;
  onCompare?: () => void;
  priceDetails?: ReactNode;
}) {
  const theme = useTheme();
  const [showDetails, setShowDetails] = useState(initiallyShowDetails);
  const status = advice?.status ?? 'unknown';
  const forecast = advice?.forecast;
  const accent = forecast || status === 'wait' ? theme.adviceWait
    : status === 'gas_now' ? theme.advicePositive : theme.textSecondary;
  const impact = advice ? adviceImpact(advice) : null;
  const currentDecrease = advice ? adviceCurrentDecrease(advice) : null;
  const currentContext = advice ? adviceCurrentDecreaseContext(advice) : null;
  const showRecommendation = !forecast || status !== 'unknown' || advice?.reason === 'conflicting_adjustments';
  const headline = currentContext
    ? currentContext.companies.length ? 'Good time to refuel at reporting companies' : 'Good time to refuel'
    : showRecommendation ? adviceHeadline(advice, loading, error) : forecastLabel(forecast);
  const headlineColor = currentContext || status === 'gas_now' ? theme.advicePositive : accent;
  const weeklySummary = weeklyAdjustments && summarizeWeeklyAdjustments(weeklyAdjustments.adjustments, fuelType);
  const sourceEvidence = [...(advice?.evidence ?? []), ...(advice?.currentAdjustments ?? [])];
  const evidence = advice?.evidence[0];
  const sources = [...new Set(sourceEvidence.flatMap(e => e.sourceUrls))]
    .filter(url => { try { return new URL(url).protocol === 'https:'; } catch { return false; } });
  const newsId = sourceEvidence.find(e => e.newsId && newsIdPattern.test(e.newsId))?.newsId;
  const compareStyle = StyleSheet.flatten([styles.primaryAction, { backgroundColor: theme.backgroundSelected, borderColor: accent }]);
  const compareLabel = <ThemedText type="smallBold" style={{ color: accent }}>Compare fuel prices →</ThemedText>;
  return <View style={[styles.card, { backgroundColor: theme.backgroundElement, borderColor: accent }]}>
    <View style={styles.topline}>
      <View style={styles.label} accessibilityLiveRegion="polite">
        <AppIcon name="fuel" color={headlineColor} />
        <ThemedText accessibilityRole="header" style={[styles.headline, { color: headlineColor }]}>{headline}</ThemedText>
      </View>
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
    </View>

    <View style={styles.body}>
      <View style={styles.recommendation} accessibilityLiveRegion="polite">
        {currentContext ? <View style={[styles.currentContext, { borderColor: theme.advicePositive }]}>
          <ThemedText type="smallBold" style={{ color: theme.advicePositive }}>{currentContext.change}</ThemedText>
          <ThemedText type="small" themeColor="textSecondary">{currentContext.companies.length
            ? `Confirmed for ${companyCount(currentContext.companies.length)}.`
            : 'Based on the latest confirmed nationwide adjustment.'}</ThemedText>
          <ThemedText type="small" themeColor="textSecondary">Effective {weekDate(currentContext.evidence.effectiveDatePhilippines)}</ThemedText>
        </View> : showRecommendation && <>
          {currentDecrease && <ThemedText type="smallBold" style={{ color: theme.advicePositive }}>{currentDecrease}</ThemedText>}
          {!currentDecrease && <ThemedText>{advice?.message ?? (loading ? 'Looking up the latest fuel-price advice for your selection.'
            : error ? 'We couldn’t load fuel advice. Check your connection and try again.' : 'No current confirmed update is available.')}</ThemedText>}
          {evidence && advice?.reason !== 'conflicting_adjustments' && <ThemedText type="small" themeColor="textSecondary">
            Effective: {adviceTime(evidence)}{evidence.isEffectiveTimeDefaulted ? '\n6:00 AM PHT default used' : ''}
          </ThemedText>}
        </>}
        {priceDetails}
        {forecast && <>
          {(currentContext || showRecommendation) && <ThemedText accessibilityRole="header" type="smallBold"
            style={{ color: theme.adviceWait }}>{forecastLabel(forecast)}</ThemedText>}
          <ThemedText>The next fuel-price adjustment is forecast and may change.</ThemedText>
          <ThemedText type="small" style={{ color: theme.adviceWait }}>Next adjustment · Forecast: {changeLabel({
            fuel: fuelType, oilCompany: forecast.oilCompany, status: 'forecast',
            minChangePerLiter: forecast.minChangePerLiter, maxChangePerLiter: forecast.maxChangePerLiter,
          })}. Expected date: {adviceTime(forecast)}.</ThemedText>
        </>}
        {impact && <View style={[styles.impact, { backgroundColor: theme.background, borderColor: theme.backgroundSelected }]}>
          <ThemedText type="small" themeColor="textSecondary">{impact.label}</ThemedText>
          <ThemedText style={[styles.savings, { color: status === 'gas_now' ? theme.advicePositive : accent }]}>₱{impact.total.toFixed(2)}</ThemedText>
          <ThemedText type="small" themeColor="textSecondary">₱{impact.amount.toFixed(2)}/L {evidence!.minChangePerLiter < 0 ? 'decrease' : 'increase'} · Example fill</ThemedText>
        </View>}
      </View>
      {weeklyAdjustments !== undefined && <View style={[styles.companyList, { backgroundColor: theme.background, borderColor: theme.backgroundSelected }]}>
        <ThemedText accessibilityRole="header" type="smallBold">Weekly adjustment summary</ThemedText>
        {weeklyAdjustments?.adjustments.length && weeklySummary ? <>
          <ThemedText type="small" themeColor="textSecondary">{weekDate(weeklyAdjustments.weekStart)} – {weekDate(weeklyAdjustments.weekEnd)}</ThemedText>
          {weeklySummary.groups.map(({ min, max, count }) => <ThemedText key={min} type="smallBold"
            style={{ color: min < 0 ? theme.advicePositive : min > 0 ? theme.priceIncrease : theme.textSecondary }}>
            {weeklyAdjustmentLabel(min, max)} · {companyCount(count)} reported
          </ThemedText>)}
          {weeklySummary.missing > 0 && <ThemedText type="small" themeColor="textSecondary">
            {companyCount(weeklySummary.missing)} had no {fuelType} amount listed.
          </ThemedText>}
        </> : <ThemedText type="small" themeColor="textSecondary">{weeklyAdjustmentsLoading ? 'Loading weekly adjustments…'
          : weeklyAdjustmentsUnavailable ? 'Company adjustments unavailable' : 'No weekly adjustments to display yet'}</ThemedText>}
      </View>}
    </View>

    <View style={[styles.footer, { borderTopColor: theme.backgroundSelected }]}>
      <View style={styles.metadata}>
        <ThemedText type="small" themeColor="textSecondary">{advice ? `${currentContext ? 'Confirmed current decrease' : advice.evidence.length ? 'Confirmed adjustment' : advice.forecast ? 'Forecast only' : 'No confirmed update'} · Checked ${newsDate(advice.evaluatedAtUtc, true)} PHT` : 'Price-based advice'}</ThemedText>
        <ThemedText type="small" themeColor="textSecondary">Individual companies may differ. Keep enough fuel for your trip.</ThemedText>
      </View>
      <View style={styles.actions}>
        {onCompare ? <Pressable accessibilityRole="button" onPress={onCompare} style={compareStyle}>{compareLabel}</Pressable>
          : <Link href="/fuel-prices" onPress={onNavigate} asChild><Pressable accessibilityRole="link" style={compareStyle}>{compareLabel}</Pressable></Link>}
        {sourceEvidence.length > 0 && <Pressable accessibilityRole="button" accessibilityState={{ expanded: showDetails }}
          onPress={() => setShowDetails(value => !value)} style={styles.textButton}>
          <ThemedText type="smallBold">{showDetails ? 'Hide sources ↑' : status === 'wait' ? 'Why wait? ↓' : 'Why this advice? ↓'}</ThemedText>
        </Pressable>}
        {status === 'wait' && Platform.OS !== 'web' && <Link href="/news" onPress={onNavigate} style={styles.textButton}><ThemedText type="smallBold">Fuel news & alerts →</ThemedText></Link>}
      </View>
    </View>
    {showDetails && advice && <View style={[styles.details, { borderTopColor: theme.backgroundSelected }]}>
      <ThemedText type="smallBold">Source announcements</ThemedText>
      {newsId && <Link href={{ pathname: '/news-detail', params: { id: newsId } }} onPress={onNavigate} asChild><Pressable accessibilityRole="link" style={styles.textButton}>
        <ThemedText type="smallBold" style={{ color: accent }}>Read the full fuel update →</ThemedText>
      </Pressable></Link>}
      {sources.map((url, index) => <Pressable key={url} accessibilityRole="link" accessibilityLabel={`Open source ${index + 1}: ${new URL(url).hostname}`}
        onPress={() => void Linking.openURL(url).catch(() => {})} style={styles.textButton}>
        <ThemedText type="small" style={{ color: accent }}>Source {index + 1} · {new URL(url).hostname} ↗</ThemedText>
      </Pressable>)}
      <ThemedText type="small" themeColor="textSecondary">{sourceEvidence.some(e => e.isEffectiveTimeDefaulted)
        ? 'The effective time uses the app’s 6:00 AM Philippine-time default because the announcement supplied only a date.'
        : 'Effective dates and times come from the stored announcements.'}</ThemedText>
    </View>}
  </View>;
}

const styles = StyleSheet.create({
  card: { borderWidth: 1, borderRadius: 20, padding: 20, gap: 20 },
  topline: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', justifyContent: 'space-between', gap: 8 },
  label: { flexDirection: 'row', alignItems: 'center', flexShrink: 1, gap: 10 },
  controls: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', justifyContent: 'space-between', gap: 12 },
  fuels: { flexDirection: 'row', flexWrap: 'wrap', borderRadius: 12, padding: 4, gap: 4 },
  fuel: { minHeight: 44, paddingVertical: 12, paddingHorizontal: 12, borderRadius: 9, justifyContent: 'center' },
  companyList: { flexGrow: 1, flexShrink: 1, flexBasis: 270, minWidth: 0, padding: 14, gap: 7, borderWidth: 1, borderRadius: 12 },
  body: { flexDirection: 'row', flexWrap: 'wrap', gap: 24, alignItems: 'flex-start' },
  recommendation: { flexGrow: 1, flexShrink: 1, flexBasis: 360, gap: 12, minWidth: 0 },
  currentContext: { borderWidth: 1, borderRadius: 12, padding: 16, gap: 8 },
  headline: { fontSize: 28, lineHeight: 36, fontWeight: '700', letterSpacing: -0.5, flexShrink: 1 },
  impact: { flexBasis: 230, flexGrow: 1, flexShrink: 1, padding: 18, borderRadius: 14, borderWidth: 1, gap: 8, minWidth: 0 },
  savings: { fontSize: 26, lineHeight: 34, fontWeight: '700', fontVariant: ['tabular-nums'] },
  footer: { borderTopWidth: 1, paddingTop: 16, gap: 16 }, metadata: { gap: 6 },
  actions: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', gap: 12 },
  primaryAction: { minHeight: 44, paddingVertical: 12, paddingHorizontal: 16, borderWidth: 1, borderRadius: 10, justifyContent: 'center' },
  textButton: { minHeight: 44, paddingVertical: 10, justifyContent: 'center', alignSelf: 'flex-start' },
  details: { borderTopWidth: 1, paddingTop: 16, gap: 8 }, dim: { opacity: 0.65 },
});
