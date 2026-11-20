import { AppState } from 'react-native';

export function isAppForeground() {
  // Older native bridges can briefly report null during startup.
  return AppState.currentState !== 'background' && AppState.currentState !== 'inactive';
}

export function subscribeToAppVisibility(onChange: (visible: boolean) => void) {
  let previous = isAppForeground();
  const subscription = AppState.addEventListener('change', next => {
    const visible = next === 'active';
    if (visible !== previous) { previous = visible; onChange(visible); }
  });
  return () => subscription.remove();
}

export function subscribeToForeground(onReturn: () => void) {
  return subscribeToAppVisibility(visible => { if (visible) onReturn(); });
}
