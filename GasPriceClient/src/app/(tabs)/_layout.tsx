import { Link, Tabs } from 'expo-router';
import { Platform, Pressable, StyleSheet, useWindowDimensions, View } from 'react-native';

import { FuelAdviceNotices } from '@/components/fuel-advice-notices';
import { SafeAreaView } from 'react-native-safe-area-context';

import { AppIcon } from '@/components/app-icon';
import { ThemedText } from '@/components/themed-text';
import { useTheme } from '@/hooks/use-theme';

const destinations = [
  { name: 'index', href: '/', title: 'Overview', icon: 'overview' },
  { name: 'fuel-prices', href: '/fuel-prices', title: 'Fuel Prices', icon: 'fuel' },
  { name: 'news', href: '/news', title: 'News', icon: 'news' },
  { name: 'weekly-changes', href: '/weekly-changes', title: 'Weekly Changes', icon: 'changes' },
] as const;

export default function TabLayout() {
  const theme = useTheme();
  const { width } = useWindowDimensions();
  const desktop = Platform.OS === 'web' && width >= 900;
  return <View style={{ flex: 1, backgroundColor: theme.background }}>
    {!desktop && <SafeAreaView edges={['top']}><View style={styles.mobileNotices}><FuelAdviceNotices compact /></View></SafeAreaView>}
    <Tabs screenOptions={{
    headerShown: false,
    tabBarPosition: desktop ? 'top' : 'bottom',
    tabBarActiveTintColor: '#76B4FF',
    tabBarInactiveTintColor: theme.textSecondary,
    tabBarStyle: { backgroundColor: theme.backgroundElement, borderTopColor: theme.backgroundSelected, height: Platform.OS === 'web' ? 76 : undefined },
    tabBarLabelStyle: { fontSize: width < 380 ? 10 : 11, fontWeight: '600', marginBottom: 6 },
    tabBarItemStyle: { paddingTop: 8 },
  }} tabBar={desktop ? ({ state, navigation }) => <View style={styles.nav}>
    <View style={styles.navInner}>
      <Link href="/" style={styles.brandLink} accessibilityLabel="GasPrice Philippines home">
        <View style={styles.brand}>
          <View style={styles.brandIcon}><AppIcon name="fuel" color="#76B4FF" /></View>
          <View><ThemedText style={styles.brandName}>GasPrice</ThemedText><ThemedText style={styles.country}>PHILIPPINES</ThemedText></View>
        </View>
      </Link>
      <FuelAdviceNotices />
      <View style={styles.links}>{destinations.map(item => {
        const route = state.routes.find(r => r.name === item.name);
        const selected = state.routes[state.index].name === item.name;
        return <Link key={item.name} href={item.href} asChild>
          <Pressable accessibilityRole="link" accessibilityState={{ selected }} onPress={event => {
            if (!route) return;
            const emitted = navigation.emit({ type: 'tabPress', target: route.key, canPreventDefault: true });
            if (emitted.defaultPrevented) event.preventDefault();
          }} style={({ hovered, pressed }) => [styles.navLink, (selected || hovered || pressed) && styles.navLinkActive]}>
            <AppIcon name={item.icon} color={selected ? '#76B4FF' : theme.textSecondary} />
            <ThemedText type="smallBold" style={[styles.navLabel, selected ? styles.activeText : styles.inactiveText]}>{item.title}</ThemedText>
          </Pressable>
        </Link>;
      })}</View>
    </View>
  </View> : undefined}>
    {destinations.map(item => <Tabs.Screen key={item.name} name={item.name} options={{
      title: item.title, tabBarAccessibilityLabel: item.title,
      tabBarIcon: ({ color }) => <AppIcon name={item.icon} color={color} />,
    }} />)}
  </Tabs>
  </View>;
}
const styles = StyleSheet.create({
  mobileNotices: { paddingHorizontal: 16, paddingVertical: 8 },
  nav: { backgroundColor: '#151A21', borderBottomWidth: 1, borderColor: '#2B3441', alignItems: 'center', paddingHorizontal: 28 },
  navInner: { width: '100%', maxWidth: 1160, minHeight: 84, flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: 16, flexWrap: 'wrap', paddingVertical: 10 },
  brandLink: { flexShrink: 0 }, brand: { flexDirection: 'row', alignItems: 'center', gap: 10 },
  brandIcon: { backgroundColor: '#21334B', borderRadius: 12, padding: 10 },
  brandName: { fontSize: 20, lineHeight: 25, fontWeight: '700' }, country: { fontSize: 9, lineHeight: 15, letterSpacing: 2, color: '#9DA9BA' },
  links: { flexDirection: 'row', gap: 4 }, navLink: { flexDirection: 'row', alignItems: 'center', gap: 6, paddingHorizontal: 8, paddingVertical: 12, borderRadius: 10 },
  navLabel: { fontSize: 11, lineHeight: 16 },
  navLinkActive: { backgroundColor: '#21334B' }, activeText: { color: '#76B4FF' }, inactiveText: { color: '#9DA9BA' },
});
