import { Platform, StyleSheet, useWindowDimensions, View, type StyleProp, type ViewStyle } from 'react-native';

import { FuelAdviceNotices } from '@/components/fuel-advice-notices';
import { GasUpBrand } from '@/components/gasup-brand';

// Render inside the screen's scroll content so branding and prices scroll together.
export function MobileHeader({ style }: { style?: StyleProp<ViewStyle> }) {
  const { width } = useWindowDimensions();
  if (Platform.OS === 'web' && width >= 900) return null;

  return <View style={[styles.header, style]}>
    <View style={styles.brand}><GasUpBrand /></View>
    <FuelAdviceNotices compact />
  </View>;
}

const styles = StyleSheet.create({
  header: { gap: 12 },
  brand: { alignSelf: 'flex-start' },
});
