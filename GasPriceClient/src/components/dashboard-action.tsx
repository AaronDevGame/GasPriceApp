import { Pressable, StyleSheet } from 'react-native';
import { ThemedText } from '@/components/themed-text';

export function DashboardAction({ title, onPress, disabled = false, primary = false }: {
  title: string; onPress: () => void; disabled?: boolean; primary?: boolean;
}) {
  return <Pressable accessibilityRole="button" disabled={disabled} accessibilityState={{ disabled }} onPress={onPress}
    style={({ hovered, pressed }) => [styles.button, primary && styles.primary, (hovered || pressed) && styles.hover, disabled && styles.disabled]}>
    <ThemedText type="smallBold" style={primary ? styles.primaryText : styles.text}>{title}</ThemedText>
  </Pressable>;
}
const styles = StyleSheet.create({
  button: { minHeight: 44, paddingHorizontal: 16, paddingVertical: 11, borderRadius: 9, backgroundColor: '#253246', alignItems: 'center', justifyContent: 'center' },
  primary: { backgroundColor: '#76B4FF' }, hover: { opacity: 0.85 }, disabled: { opacity: 0.45 },
  text: { color: '#B9D8FF' }, primaryText: { color: '#102039' },
});
