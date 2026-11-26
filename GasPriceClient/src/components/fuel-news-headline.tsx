import { Link } from 'expo-router';
import { useEffect, useState } from 'react';
import { AccessibilityInfo, Animated, Platform, Pressable, StyleSheet, View, useWindowDimensions } from 'react-native';
import { ThemedText } from '@/components/themed-text';
import { newsStatusLabel, type NewsItem } from '@/news/api';

export function FuelNewsHeadline({ items }: { items: NewsItem[] }) {
  const { width } = useWindowDimensions();
  const compact = width < 600;
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
  const headline = status === 'confirmed' ? title.replace(/^\s*confirmed\s*:\s*/i, '') || title : title;
  return <View style={[styles.strip, !compact && count > 1 && styles.roomForControls]}>
    <View style={styles.label}>
      <ThemedText type="smallBold" style={styles.labelText}>{newsStatusLabel(status)}</ThemedText>
    </View>
    <Link href={{ pathname: '/news-detail', params: { id: item.id } }} asChild>
      <Pressable accessibilityRole="link" accessibilityLabel={headline} style={styles.link}
        onHoverIn={() => setReading(true)} onHoverOut={() => setReading(false)}
        onFocus={() => setFocused(true)} onBlur={() => setFocused(false)}>
        <Animated.View style={{ transform: [{ translateX: slide }] }}>
          <ThemedText numberOfLines={compact ? 2 : 1} type="smallBold" style={styles.headline}>{headline}</ThemedText>
        </Animated.View>
      </Pressable>
    </Link>
    {count > 1 && <View style={[styles.controls, !compact && styles.desktopControls]}>
      <Pressable accessibilityRole="button" accessibilityLabel="Previous headline" onPress={() => setIndex(current => (current + count - 1) % count)}
        style={styles.control}><ThemedText style={styles.arrow}>‹</ThemedText></Pressable>
      <Pressable accessibilityRole="button" accessibilityLabel="Next headline" onPress={() => setIndex(current => (current + 1) % count)}
        style={styles.control}><ThemedText style={styles.arrow}>›</ThemedText></Pressable>
    </View>}
  </View>;
}

const styles = StyleSheet.create({
  headline: { color: '#F2F6FC' },
  strip: { alignSelf: 'stretch', flexDirection: 'row', justifyContent: 'center', alignItems: 'stretch', minHeight: 44, backgroundColor: '#182332' },
  label: { backgroundColor: '#203650', justifyContent: 'center', paddingHorizontal: 10 },
  labelText: { color: '#9BC7FF', fontSize: 11, letterSpacing: 1 },
  link: { flexShrink: 1, minWidth: 0, justifyContent: 'center', marginHorizontal: 10, overflow: 'hidden', paddingVertical: 2 },
  roomForControls: { paddingHorizontal: 72 },
  controls: { flexDirection: 'row' },
  desktopControls: { position: 'absolute', right: 0, top: 0, bottom: 0 },
  control: { width: 36, justifyContent: 'center', alignItems: 'center' },
  arrow: { fontSize: 24, color: '#9BC7FF' },
});
