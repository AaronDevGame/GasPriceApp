import { Tabs } from 'expo-router';
import { Platform } from 'react-native';

import { useTheme } from '@/hooks/use-theme';

export default function TabLayout() {
  const theme = useTheme();
  return <Tabs screenOptions={{
    headerShown: false,
    tabBarActiveTintColor: '#208AEF',
    tabBarInactiveTintColor: theme.textSecondary,
    tabBarStyle: { backgroundColor: theme.background, borderTopColor: theme.backgroundSelected },
    tabBarLabelStyle: { fontSize: Platform.OS === 'web' ? 13 : 11, fontWeight: '600' },
  }}>
    <Tabs.Screen name="index" options={{ title: 'Overview', tabBarAccessibilityLabel: 'Overview' }} />
    <Tabs.Screen name="fuel-prices" options={{ title: 'Fuel Prices', tabBarAccessibilityLabel: 'Fuel Prices' }} />
    <Tabs.Screen name="news" options={{ title: 'News', tabBarAccessibilityLabel: 'News' }} />
    <Tabs.Screen name="weekly-changes" options={{ title: 'Weekly Changes', tabBarAccessibilityLabel: 'Weekly Changes' }} />
  </Tabs>;
}
