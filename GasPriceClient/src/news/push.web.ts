// Native push capabilities never enter the web bundle.
export function listenForNewsNotifications(_open: (id: string) => void) { return () => {}; }
