import { StyleSheet, View, type ColorValue } from 'react-native';

export type AppIconName = 'overview' | 'fuel' | 'news' | 'changes';

/** Small cross-platform line icons without a font or native dependency. */
export function AppIcon({ name, color = '#9DA9BA' }: { name: AppIconName; color?: ColorValue }) {
  const line = { borderColor: color };
  return <View style={styles.frame} accessible={false}>
    {name === 'overview' ? <View style={styles.grid}>{[0, 1, 2, 3].map(n => <View key={n} style={[styles.square, line]} />)}</View>
      : name === 'changes' ? <View style={styles.bars}>{[8, 15, 21].map(height => <View key={height} style={[styles.bar, { height, backgroundColor: color }]} />)}</View>
      : name === 'news' ? <View style={[styles.paper, line]}>{[0, 1, 2].map(n => <View key={n} style={[styles.rule, { backgroundColor: color }]} />)}</View>
      : <><View style={[styles.pump, line]}><View style={[styles.window, line]} /><View style={[styles.rule, { backgroundColor: color }]} /></View><View style={[styles.hose, line]} /></>}
  </View>;
}
const styles = StyleSheet.create({
  frame: { width: 24, height: 24, alignItems: 'center', justifyContent: 'center' },
  grid: { width: 21, height: 21, flexDirection: 'row', flexWrap: 'wrap', gap: 3 },
  square: { width: 9, height: 9, borderWidth: 1.6, borderRadius: 2 },
  bars: { flexDirection: 'row', alignItems: 'flex-end', gap: 4, height: 22 },
  bar: { width: 4, borderRadius: 2 },
  paper: { width: 21, height: 20, borderWidth: 1.6, borderRadius: 3, padding: 4, gap: 3 },
  rule: { height: 1.5, width: '100%', borderRadius: 1 },
  pump: { width: 15, height: 22, borderWidth: 1.6, borderRadius: 3, padding: 3, gap: 4, marginRight: 5 },
  window: { height: 7, borderWidth: 1.3, borderRadius: 1 },
  hose: { position: 'absolute', right: 0, top: 6, width: 6, height: 14, borderWidth: 1.6, borderTopRightRadius: 4, borderBottomRightRadius: 3 },
});
