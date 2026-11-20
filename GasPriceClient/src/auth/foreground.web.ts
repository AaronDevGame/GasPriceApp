export function isAppForeground() {
  return typeof document !== 'undefined' && document.visibilityState === 'visible';
}

export function subscribeToAppVisibility(onChange: (visible: boolean) => void) {
  if (typeof document === 'undefined') return () => {};
  let previous = isAppForeground();
  const onVisibilityChange = () => {
    const visible = isAppForeground();
    if (visible !== previous) { previous = visible; onChange(visible); }
  };
  document.addEventListener('visibilitychange', onVisibilityChange);
  return () => document.removeEventListener('visibilitychange', onVisibilityChange);
}

export function subscribeToForeground(onReturn: () => void) {
  return subscribeToAppVisibility(visible => { if (visible) onReturn(); });
}
