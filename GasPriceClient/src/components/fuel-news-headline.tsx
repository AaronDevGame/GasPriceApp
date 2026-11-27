import { Link } from 'expo-router';
import { useEffect, useState } from 'react';
import { AccessibilityInfo, Animated, Platform, Pressable, StyleSheet, View, useWindowDimensions } from 'react-native';
import { ThemedText } from '@/components/themed-text';
import { newsDate, newsStatusLabel, type NewsItem } from '@/news/api';

export function FuelNewsHeadline({ items }: { items: NewsItem[] }) {
  const { width, fontScale } = useWindowDimensions();
  const desktop = Platform.OS === 'web' && width >= 900;
  // Match the desktop navigation's side padding and content width.
  const headerInset = desktop ? Math.max(28, (width - 1160) / 2) : 10;
  const labelWidth = headerInset + 14 + Math.ceil(86 * Math.max(1, fontScale));
  const [index, setIndex] = useState(0);
  const [reading, setReading] = useState(false);
  const [focused, setFocused] = useState(false);
  const [reduceMotion, setReduceMotion] = useState(true);
  const [slide] = useState(() => new Animated.Value(0));
  const count = items.length;
  const item = items[index % count];

  useEffect(() => {
    let active = true;
    void AccessibilityInfo.isReduceMotionEnabled().then(value => { if (active) setReduceMotion(value); });
    const subscription = AccessibilityInfo.addEventListener('reduceMotionChanged', setReduceMotion);
    return () => { active = false; subscription.remove(); };
  }, []);

  useEffect(() => {
    if (count < 2 || reading || focused || reduceMotion) return;
    const timer = setInterval(() => setIndex(current => (current + 1) % count), 7500);
    return () => clearInterval(timer);
  }, [count, reading, focused, reduceMotion]);

  useEffect(() => {
    if (reduceMotion || count < 2) { slide.setValue(0); return; }
    slide.setValue(-20);
    const animation = Animated.timing(slide, { toValue: 0, duration: 280, useNativeDriver: Platform.OS !== 'web' });
    animation.start();
    return () => animation.stop();
  }, [item?.id, reduceMotion, count, slide]);

  if (!item) return null;
  const { status, title } = item.article;
  const publicationDate = newsDate(item.publishedAtUtc);
  const headline = status === 'confirmed' ? title.replace(/^\s*confirmed\s*:\s*/i, '') || title : title;
  return <View style={[styles.strip, { paddingRight: headerInset }]}>
    <View style={[styles.label, { width: labelWidth, paddingLeft: headerInset }]}>
      <ThemedText type="smallBold" style={styles.labelText}>{newsStatusLabel(status)}</ThemedText>
    </View>
    <Link href={{ pathname: '/news-detail', params: { id: item.id } }} asChild>
      <Pressable accessibilityRole="link" accessibilityLabel={`${headline}. Published ${publicationDate}`} style={styles.link}
        onHoverIn={() => setReading(true)} onHoverOut={() => setReading(false)}
        onFocus={() => setFocused(true)} onBlur={() => setFocused(false)}>
        <Animated.View style={[styles.headlineRow, { transform: [{ translateX: slide }] }]}>
          <ThemedText numberOfLines={1} type="smallBold" style={styles.headline}>{headline}</ThemedText>
          <ThemedText numberOfLines={1} type="small" style={styles.date}>{publicationDate}</ThemedText>
        </Animated.View>
      </Pressable>
    </Link>
    {count > 1 && <View style={styles.controls}>
      <Pressable accessibilityRole="button" accessibilityLabel="Previous headline" onPress={() => setIndex(current => (current + count - 1) % count)}
        style={styles.control}><ThemedText style={styles.arrow}>‹</ThemedText></Pressable>
      <Pressable accessibilityRole="button" accessibilityLabel="Next headline" onPress={() => setIndex(current => (current + 1) % count)}
        style={styles.control}><ThemedText style={styles.arrow}>›</ThemedText></Pressable>
    </View>}
  </View>;
}

const styles = StyleSheet.create({
  headlineRow: { flexDirection: 'row', alignItems: 'baseline', gap: 10 },
  headline: { color: '#F2F6FC', flexShrink: 1, minWidth: 0 },
  date: { color: '#8B8B8B', fontSize: 11 },
  strip: { alignSelf: 'stretch', flexDirection: 'row', alignItems: 'stretch', minHeight: 44, backgroundColor: '#182332' },
  label: { backgroundColor: '#203650', justifyContent: 'center', paddingRight: 14 },
  labelText: { color: '#9BC7FF', fontSize: 11, letterSpacing: 1 },
  link: { flex: 1, minWidth: 0, justifyContent: 'center', marginHorizontal: 10, overflow: 'hidden', paddingVertical: 2 },
  controls: { flexDirection: 'row' },
  control: { width: 36, justifyContent: 'center', alignItems: 'center' },
  arrow: { fontSize: 24, color: '#9BC7FF' },
});
