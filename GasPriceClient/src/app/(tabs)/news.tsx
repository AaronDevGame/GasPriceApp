import { ScrollView, StyleSheet, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import { ThemedText } from '@/components/themed-text';
import { ThemedView } from '@/components/themed-view';

export default function NewsScreen() {
  return <ThemedView style={styles.page}>
    <SafeAreaView style={styles.page}>
      <ScrollView contentContainerStyle={styles.scroll}>
        <View style={styles.content}>
          <ThemedText type="smallBold" style={styles.accent}>GASPRICE · PHILIPPINES</ThemedText>
          <ThemedText type="title" style={styles.title}>News</ThemedText>
        </View>
      </ScrollView>
    </SafeAreaView>
  </ThemedView>;
}

const styles = StyleSheet.create({
  page: { flex: 1 },
  scroll: { padding: 24, alignItems: 'center' },
  content: { width: '100%', maxWidth: 900, gap: 8, paddingVertical: 16 },
  accent: { color: '#208AEF' },
  title: { fontSize: 38, lineHeight: 44 },
});
