import { Tabs } from 'expo-router';

import { useTheme } from '@/hooks/use-theme';

export default function TabLayout() {
  const theme = useTheme();
  return <Tabs screenOptions={{
    headerShown: false,
    tabBarActiveTintColor: '#208AEF',
    tabBarInactiveTintColor: theme.textSecondary,
    tabBarStyle: { backgroundColor: theme.background, borderTopColor: theme.backgroundSelected },
    tabBarLabelStyle: { fontSize: 13, fontWeight: '600' },
  }}>
    <Tabs.Screen name="index" options={{ title: 'Gas Price', tabBarAccessibilityLabel: 'Gas Price' }} />
    <Tabs.Screen name="weekly-changes" options={{ title: 'Weekly Changes', tabBarAccessibilityLabel: 'Weekly Changes' }} />
  </Tabs>;
}
