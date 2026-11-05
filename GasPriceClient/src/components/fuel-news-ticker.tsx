import { Link } from 'expo-router';
import { useEffect, useState } from 'react';
import { Animated, Easing, Pressable, StyleSheet, View } from 'react-native';
import { ThemedText } from '@/components/themed-text';
import { ThemedView } from '@/components/themed-view';
import type { NewsItem } from '@/news/api';

export function FuelNewsTicker({ item }: { item: NewsItem }) {
  const message = `${item.article.title}   •   ${item.article.summary}`;
  const [offset] = useState(() => new Animated.Value(0));
  const [viewport, setViewport] = useState(0);
  const [textWidth, setTextWidth] = useState(0);
  const [paused, setPaused] = useState(false);
  useEffect(() => {
    if (!viewport || !textWidth || paused) return;
    let active = true;
    let animation: Animated.CompositeAnimation | undefined;
    const run = (position: number) => {
      if (!active) return;
      const distance = textWidth + position;
      animation = Animated.timing(offset, {
        toValue: -textWidth, duration: Math.max(1, distance / 45 * 1000),
        easing: Easing.linear, useNativeDriver: true, isInteraction: false,
      });
      animation.start(({ finished }) => {
        if (finished && active) { offset.setValue(0); run(0); }
      });
    };
    offset.stopAnimation(position => run(Math.max(-textWidth, Math.min(0, position))));
    return () => { active = false; animation?.stop(); };
  }, [viewport, textWidth, message, offset, paused]);

  return <Link href={{ pathname: '/news-detail', params: { id: item.id } }} asChild>
    <Pressable accessibilityRole="link" accessibilityLabel={`Read fuel news: ${message}`}
      onHoverIn={() => setPaused(true)} onHoverOut={() => setPaused(false)}
      onFocus={() => setPaused(true)} onBlur={() => setPaused(false)}>
      <ThemedView type="backgroundElement" style={styles.header}>
        <View style={styles.viewport} onLayout={event => setViewport(event.nativeEvent.layout.width)}>
          <Animated.View style={[styles.track, { transform: [{ translateX: offset }] }]}>
            <ThemedText type="smallBold" numberOfLines={1} style={styles.headline}
              onLayout={event => setTextWidth(event.nativeEvent.layout.width)}>{message}   •   </ThemedText>
            {Array.from({ length: textWidth ? Math.max(1, Math.ceil(viewport / textWidth)) : 1 }, (_, index) =>
              <ThemedText key={index} type="smallBold" numberOfLines={1} style={styles.headline}
                accessible={false} aria-hidden>{message}   •   </ThemedText>)}
          </Animated.View>
        </View>
      </ThemedView>
    </Pressable>
  </Link>;
}
const styles = StyleSheet.create({
  header: { flexDirection: 'row', alignItems: 'center', borderRadius: 12, paddingHorizontal: 14, gap: 12, height: 48, overflow: 'hidden' },
  viewport: { flex: 1, height: 24, overflow: 'hidden', justifyContent: 'center' },
  track: { position: 'absolute', flexDirection: 'row', alignItems: 'center' },
  headline: { flexShrink: 0 },
});
