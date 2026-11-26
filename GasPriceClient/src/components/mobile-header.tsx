import { Link } from 'expo-router';
import { Platform, StyleSheet, useWindowDimensions, View } from 'react-native';

import { FuelAdviceNotices } from '@/components/fuel-advice-notices';
import { ThemedText } from '@/components/themed-text';

// Render inside the screen's scroll content so branding and prices scroll together.
export function MobileHeader() {
  const { width } = useWindowDimensions();
  if (Platform.OS === 'web' && width >= 900) return null;

  return <View style={styles.header}>
    <Link href="/" accessibilityLabel="GasUp Philippines home" style={styles.brandLink}>
      <ThemedText type="smallBold" style={styles.brand}>GasUp · PHILIPPINES</ThemedText>
    </Link>
    <FuelAdviceNotices compact />
  </View>;
}

const styles = StyleSheet.create({
  header: { gap: 12 },
  brandLink: { alignSelf: 'flex-start', paddingVertical: 4 },
  brand: { color: '#76B4FF' },
});
