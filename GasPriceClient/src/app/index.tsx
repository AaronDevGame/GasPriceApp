import { Button, StyleSheet } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import { useAuth } from '@/auth/auth-provider';
import { ThemedText } from '@/components/themed-text';
import { ThemedView } from '@/components/themed-view';

export default function HomeScreen() {
  const { session, player, restore, logout } = useAuth();
  return (
    <ThemedView style={styles.page}>
      <SafeAreaView style={styles.content}>
        <ThemedText type="title">Welcome, {session?.playerName}</ThemedText>
        <ThemedText>Your guest session is ready.</ThemedText>
        <ThemedView type="backgroundElement" style={styles.summary}>
          <ThemedText>Player ID: {player?.playerId}</ThemedText>
          <ThemedText>Health: {player?.health}</ThemedText>
          <ThemedText>Money: {player?.money}</ThemedText>
        </ThemedView>
        <ThemedText type="small">Signing out keeps this guest on your device so you can continue later.</ThemedText>
        <Button title="Sign out" onPress={() => void logout()} />
        <Button title="Refresh player" onPress={() => void restore()} />
      </SafeAreaView>
    </ThemedView>
  );
}
const styles = StyleSheet.create({
  page: { flex: 1, justifyContent: 'center', alignItems: 'center', padding: 24 },
  content: { width: '100%', maxWidth: 640, gap: 24 },
  summary: { padding: 24, borderRadius: 16, gap: 12 },
});
