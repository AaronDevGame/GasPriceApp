import { Link } from 'expo-router';
import { StyleSheet, View } from 'react-native';

import { AppIcon } from '@/components/app-icon';
import { ThemedText } from '@/components/themed-text';

export function GasUpBrand() {
  return <Link href="/" style={styles.brandLink} accessibilityLabel="GasUp Philippines home">
    <View style={styles.brand}>
      <View style={styles.brandIcon}><AppIcon name="fuel" color="#76B4FF" /></View>
      <View>
        <ThemedText style={styles.brandName}>GasUp</ThemedText>
        <ThemedText style={styles.country}>PHILIPPINES</ThemedText>
      </View>
    </View>
  </Link>;
}

const styles = StyleSheet.create({
  brandLink: { flexShrink: 0, alignSelf: 'center' },
  brand: { flexDirection: 'row', alignItems: 'center', gap: 10 },
  brandIcon: { backgroundColor: '#21334B', borderRadius: 12, padding: 10 },
  brandName: { fontSize: 20, lineHeight: 25, fontWeight: '700' },
  country: { fontSize: 9, lineHeight: 15, letterSpacing: 2, color: '#9DA9BA' },
});
