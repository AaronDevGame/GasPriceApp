import { StyleSheet, View } from 'react-native';

import { ThemedText } from '@/components/themed-text';
import { useTheme } from '@/hooks/use-theme';

export function AppFooter() {
  const theme = useTheme();

  return (
    <View style={[styles.footer, { borderTopColor: theme.backgroundSelected }]}>
      <ThemedText type="small" themeColor="textSecondary" style={styles.notice}>
        © {new Date().getFullYear()} GasUp. All rights reserved.
      </ThemedText>
    </View>
  );
}

const styles = StyleSheet.create({
  footer: {
    alignSelf: 'stretch',
    marginTop: 'auto',
    borderTopWidth: 1,
    paddingHorizontal: 16,
    paddingVertical: 16,
  },
  notice: { fontSize: 12, textAlign: 'center' },
});
