import { RefreshControl, ScrollView, type ScrollViewProps } from 'react-native';

import { useTheme } from '@/hooks/use-theme';

export type RefreshableScrollViewProps = Omit<ScrollViewProps, 'refreshControl'> & {
  refreshing: boolean;
  onRefresh(): Promise<unknown> | void;
  refreshEnabled?: boolean;
};

export function RefreshableScrollView({ refreshing, onRefresh, refreshEnabled = true, ...props }: RefreshableScrollViewProps) {
  const theme = useTheme();
  return <ScrollView {...props} refreshControl={<RefreshControl refreshing={refreshing}
    onRefresh={() => void onRefresh()} enabled={refreshEnabled}
    tintColor={theme.text} colors={[theme.text]} progressBackgroundColor={theme.backgroundElement} />} />;
}
