import { useState, type ReactNode } from 'react';
import { ActivityIndicator, Button, StyleSheet, TextInput, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import { useAuth } from '@/auth/auth-provider';
import { ThemedText } from '@/components/themed-text';
import { ThemedView } from '@/components/themed-view';
import { useTheme } from '@/hooks/use-theme';

export function AuthGate({ children }: { children: ReactNode }) {
  const { session, player, loading, error, restore, login } = useAuth();
  const [name, setName] = useState('');
  const [confirmNew, setConfirmNew] = useState(false);
  const theme = useTheme();
  if (!loading && !error && session?.state === 'authenticated' && player) return children;
  const unavailable = session?.state === 'unavailable';
  const setup = session?.state === 'new' || unavailable;

  return (
    <ThemedView style={styles.page}>
      <SafeAreaView style={styles.card}>
        <ThemedText type="title">GasPrice</ThemedText>
        {loading ? <>
          <ActivityIndicator accessibilityLabel="Restoring guest session" />
          <ThemedText>Restoring your guest session…</ThemedText>
        </> : <>
          {error && <ThemedText accessibilityRole="alert">{error}</ThemedText>}
          {!session || session.state === 'authenticated' ?
            <Button title="Retry loading session" onPress={() => void restore()} /> : <>
              <ThemedText type="subtitle">{setup ? 'Welcome, guest' : 'You are signed out'}</ThemedText>
              <ThemedText>{unavailable
                ? 'This device cannot verify the saved guest. You can retry, or create a new guest. A new guest will not restore your previous progress.'
                : setup ? 'Choose an optional player name to get started. Your guest account is saved on this device.'
                : 'Continue with your saved guest when you are ready.'}</ThemedText>
              {setup && <TextInput
                accessibilityLabel="Player name (optional)" placeholder="Player name (optional)"
                placeholderTextColor={theme.textSecondary} value={name} onChangeText={setName}
                maxLength={24} autoCapitalize="words" autoCorrect={false}
                style={[styles.input, { color: theme.text, borderColor: theme.textSecondary }]}
              />}
              {unavailable && !confirmNew ? <>
                <Button title="Retry saved session" onPress={() => void restore()} />
                <Button title="Create a new guest instead" onPress={() => setConfirmNew(true)} />
              </> : <>
                {confirmNew && <ThemedText>This replaces the saved guest identity on this device. Continue only if you no longer need access to that guest here.</ThemedText>}
                <Button title={setup ? 'Create guest' : 'Continue as guest'} onPress={() => void login(name.trim() || undefined, unavailable)} />
                {confirmNew && <Button title="Cancel" onPress={() => setConfirmNew(false)} />}
              </>}
            </>}
          <View style={styles.note}><ThemedText type="small">Guest access stays on this device. Clearing app data or browser cookies can remove access to your guest.</ThemedText></View>
        </>}
      </SafeAreaView>
    </ThemedView>
  );
}
const styles = StyleSheet.create({
  page: { flex: 1, justifyContent: 'center', alignItems: 'center', padding: 24 },
  card: { width: '100%', maxWidth: 460, gap: 20 },
  input: { borderWidth: 1, borderRadius: 12, padding: 14, fontSize: 16 },
  note: { marginTop: 12 },
});
