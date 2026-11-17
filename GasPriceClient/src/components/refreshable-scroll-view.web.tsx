import { useEffect, useRef, useState } from 'react';
import { ActivityIndicator, ScrollView, StyleSheet, View } from 'react-native';

import type { RefreshableScrollViewProps } from './refreshable-scroll-view';
import { ThemedText } from '@/components/themed-text';
import { useTheme } from '@/hooks/use-theme';
import { attachPullToRefresh } from '@/scroll/pull-to-refresh';

export function RefreshableScrollView({ refreshing, onRefresh, refreshEnabled = true, children, ...props }: RefreshableScrollViewProps) {
  const scroll = useRef<ScrollView>(null);
  const latest = useRef({ refreshing, onRefresh, refreshEnabled });
  const [distance, setDistance] = useState(0);
  const [busy, setBusy] = useState(false);
  const theme = useTheme();
  useEffect(() => { latest.current = { refreshing, onRefresh, refreshEnabled }; }, [refreshing, onRefresh, refreshEnabled]);
  useEffect(() => {
    const node = scroll.current?.getScrollableNode();
    if (!(node instanceof HTMLElement)) return;
    return attachPullToRefresh(node, {
      canRefresh: () => latest.current.refreshEnabled && !latest.current.refreshing,
      onRefresh: () => latest.current.onRefresh(),
      onPull: setDistance,
      onBusy: setBusy,
    });
  }, []);
  const active = busy || refreshing;
  return <View style={styles.container}>
    <ScrollView {...props} ref={scroll}>{children}</ScrollView>
    {(distance > 0 || busy) && <View pointerEvents="none" style={[styles.indicator, { backgroundColor: theme.backgroundElement }]}>
      {active && <ActivityIndicator size="small" color={theme.text} />}
      <ThemedText accessibilityLiveRegion="polite" type="small">
        {active ? 'Refreshing…' : distance >= 64 ? 'Release to refresh' : 'Pull down to refresh'}
      </ThemedText>
    </View>}
  </View>;
}

const styles = StyleSheet.create({
  container: { flex: 1, minHeight: 0 },
  indicator: { position: 'absolute', top: 8, alignSelf: 'center', borderRadius: 24, paddingHorizontal: 16,
    paddingVertical: 10, flexDirection: 'row', alignItems: 'center', gap: 8, boxShadow: '0 2px 10px #0004' },
});
