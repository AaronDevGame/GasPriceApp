import { Image, StyleSheet, View } from 'react-native';
import { ThemedText } from '@/components/themed-text';

export function stationBrand(value: string) {
  const key = value.trim().toLowerCase().replace(/[^a-z0-9]/g, '');
  switch (key) {
    case 'caltex': return { name: 'Caltex', logo: 'caltex' } as const;
    case 'cleanfuel': return { name: 'Cleanfuel', logo: 'cleanfuel' } as const;
    case 'ecooil': return { name: 'EcoOil', logo: 'ecooil' } as const;
    case 'cityoil': return { name: 'City Oil', logo: null } as const;
    default: return { name: value.trim(), logo: null } as const;
  }
}

export function StationLogo({ name, logo, size = 44 }: { name: string; logo: 'caltex' | 'cleanfuel' | 'ecooil' | null; size?: number }) {
  const dimensions = { width: size, height: size };
  const scale = size / 44;
  if (logo === 'caltex') return <Image source={require('@/assets/images/stations/caltex.png')} style={dimensions} resizeMode="contain" accessibilityLabel="Caltex logo" />;
  if (logo === 'cleanfuel') return <View style={[styles.logoCrop, dimensions]}>
    <Image source={require('@/assets/images/stations/cleanfuel.png')} style={{ width: 301 * scale, height: size, left: -2 * scale }} resizeMode="stretch" accessibilityLabel="Cleanfuel logo" />
  </View>;
  if (logo === 'ecooil') return <View style={[styles.logoCrop, dimensions]}>
    <Image source={require('@/assets/images/stations/ecooil.png')} style={{ width: 110 * scale, height: size, left: -4 * scale }} resizeMode="stretch" accessibilityLabel="EcoOil logo" />
  </View>;
  return <View style={[styles.logoFallback, dimensions]} accessible={false}>
    <ThemedText type="smallBold" style={styles.logoInitials}>{name.split(/\s+/).map(word => word[0]).slice(0, 2).join('').toUpperCase()}</ThemedText>
  </View>;
}

const styles = StyleSheet.create({
  logoCrop: { overflow: 'hidden' },
  logoFallback: { borderRadius: 12, backgroundColor: '#34526A', alignItems: 'center', justifyContent: 'center' },
  logoInitials: { color: '#FFFFFF' },
});
