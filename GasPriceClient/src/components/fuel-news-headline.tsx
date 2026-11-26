import { Link } from 'expo-router';
import { useEffect, useState } from 'react';
import { AccessibilityInfo, Animated, Platform, Pressable, StyleSheet, View, useWindowDimensions } from 'react-native';
import { AppIcon } from '@/components/app-icon';
import { ThemedText } from '@/components/themed-text';
import type { NewsItem } from '@/news/api';

export function FuelNewsHeadline({ items }: { items: NewsItem[] }) {
  const { width } = useWindowDimensions();
  const compact = width < 600;
  const [index, setIndex] = useState(0);
  const [paused, setPaused] = useState(false);
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
    if (count < 2 || paused || reading || focused || reduceMotion) return;
    const timer = setInterval(() => setIndex(current => (current + 1) % count), 7500);
    return () => clearInterval(timer);
  }, [count, paused, reading, focused, reduceMotion]);

  useEffect(() => {
    if (reduceMotion || count < 2) { slide.setValue(0); return; }
    slide.setValue(-20);
    const animation = Animated.timing(slide, { toValue: 0, duration: 280, useNativeDriver: Platform.OS !== 'web' });
    animation.start();
    return () => animation.stop();
  }, [item?.id, reduceMotion, count, slide]);

  if (!item) return null;
  return <View style={styles.strip}>
    <View style={styles.label}>
      {compact ? <AppIcon name="news" color="#9BC7FF" /> : <ThemedText type="smallBold" style={styles.labelText}>FUEL NEWS</ThemedText>}
    </View>
    <Link href={{ pathname: '/news-detail', params: { id: item.id } }} asChild>
      <Pressable accessibilityRole="link" accessibilityLabel={item.article.title} style={styles.link}
        onHoverIn={() => setReading(true)} onHoverOut={() => setReading(false)}
        onFocus={() => setFocused(true)} onBlur={() => setFocused(false)}>
        <Animated.View style={{ transform: [{ translateX: slide }] }}>
          <ThemedText numberOfLines={compact ? 2 : 1} type="smallBold">{item.article.title}</ThemedText>
        </Animated.View>
      </Pressable>
    </Link>
    {count > 1 && <>
      {!reduceMotion && <Pressable accessibilityRole="button" accessibilityLabel={paused ? 'Resume headlines' : 'Pause headlines'}
        onPress={() => setPaused(!paused)} style={styles.control}>
        <ThemedText type="smallBold">{paused ? '▶' : 'Ⅱ'}</ThemedText>
      </Pressable>}
      <Pressable accessibilityRole="button" accessibilityLabel="Next headline" onPress={() => {
        setIndex((index + 1) % count);
        setPaused(true);
      }} style={styles.control}><ThemedText style={styles.arrow}>›</ThemedText></Pressable>
    </>}
  </View>;
}

const styles = StyleSheet.create({
  strip: { flexDirection: 'row', alignItems: 'stretch', minHeight: 44, backgroundColor: '#182332' },
  label: { backgroundColor: '#203650', justifyContent: 'center', paddingHorizontal: 10 },
  labelText: { color: '#9BC7FF', fontSize: 11, letterSpacing: 1 },
  link: { flex: 1, minWidth: 0, justifyContent: 'center', marginHorizontal: 10, overflow: 'hidden', paddingVertical: 2 },
  control: { width: 36, justifyContent: 'center', alignItems: 'center' },
  arrow: { fontSize: 24, color: '#9BC7FF' },
});
