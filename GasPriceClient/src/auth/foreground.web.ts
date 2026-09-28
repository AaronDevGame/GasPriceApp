export function subscribeToForeground(onReturn: () => void) {
  if (typeof document === 'undefined') return () => {};
  const onVisibilityChange = () => {
    if (document.visibilityState === 'visible') onReturn();
  };
  document.addEventListener('visibilitychange', onVisibilityChange);
  return () => document.removeEventListener('visibilitychange', onVisibilityChange);
}
