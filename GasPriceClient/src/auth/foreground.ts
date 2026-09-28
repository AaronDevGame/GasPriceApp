import { AppState } from 'react-native';

export function subscribeToForeground(onReturn: () => void) {
  let previous = AppState.currentState;
  const subscription = AppState.addEventListener('change', next => {
    const returning = (previous === 'background' || previous === 'inactive') && next === 'active';
    previous = next;
    if (returning) onReturn();
  });
  return () => subscription.remove();
}
