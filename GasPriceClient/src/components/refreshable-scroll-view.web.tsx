import { useEffect, useRef, useState } from 'react';
import { ActivityIndicator, ScrollView } from 'react-native';

import type { RefreshableScrollViewProps } from './refreshable-scroll-view';
import { ThemedText } from '@/components/themed-text';
import { useTheme } from '@/hooks/use-theme';
import { attachPullToRefresh, pullRefreshThreshold, refreshHoldDistance } from '@/scroll/pull-to-refresh';
import classes from './refreshable-scroll-view.module.css';

export function RefreshableScrollView({ refreshing, onRefresh, refreshEnabled = true, children, ...props }: RefreshableScrollViewProps) {
  const scroll = useRef<ScrollView>(null);
  const latest = useRef({ refreshing, onRefresh, refreshEnabled });
  const [pull, setPull] = useState({ distance: 0, pulling: false });
  const [busy, setBusy] = useState(false);
  const theme = useTheme();
  useEffect(() => { latest.current = { refreshing, onRefresh, refreshEnabled }; }, [refreshing, onRefresh, refreshEnabled]);
  useEffect(() => {
    const node = scroll.current?.getScrollableNode();
    if (!(node instanceof HTMLElement)) return;
    return attachPullToRefresh(node, {
      canRefresh: () => latest.current.refreshEnabled && !latest.current.refreshing,
      onRefresh: () => latest.current.onRefresh(),
      onPull: (distance, pulling) => setPull({ distance, pulling }),
      onBusy: setBusy,
    });
  }, []);
  // Only a gesture-initiated refresh opens the gap; background updates stay put.
  const offset = busy ? refreshHoldDistance : pull.distance;
  const ready = pull.distance >= pullRefreshThreshold;
  const visible = offset > 0;
  return <div className={classes.container} style={{ backgroundColor: theme.background }}>
    <div className={classes.indicator} data-pulling={pull.pulling} aria-hidden={!visible}
      style={{ transform: `translate3d(0, ${offset - refreshHoldDistance}px, 0)`, color: theme.text }}>
      {busy ? <ActivityIndicator size="small" color={theme.text} /> :
        <span className={classes.arrow} aria-hidden="true" style={{ transform: `rotate(${ready ? 180 : 0}deg)` }}>↓</span>}
      <ThemedText accessibilityLiveRegion="polite" type="small">
        {busy ? 'Refreshing…' : ready ? 'Release to refresh' : 'Pull down to refresh'}
      </ThemedText>
    </div>
    <div className={classes.content} data-pulling={pull.pulling}
      style={{ transform: offset > 0 ? `translate3d(0, ${offset}px, 0)` : 'translate3d(0, 0, 0)' }}>
      <ScrollView {...props} ref={scroll}>{children}</ScrollView>
    </div>
  </div>;
}
