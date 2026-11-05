import { DarkTheme, Stack, ThemeProvider, router } from 'expo-router';
import { useEffect } from 'react';
import { StatusBar } from 'expo-status-bar';

import { AuthProvider } from '@/auth/auth-provider';
import { listenForNewsNotifications } from '@/news/push';
import { newsIdPattern } from '@/news/api';

export default function RootLayout() {
  useEffect(() => listenForNewsNotifications(id => {
    if (newsIdPattern.test(id)) router.push({ pathname: '/news-detail', params: { id } });
  }), []);
  return (
    <ThemeProvider value={DarkTheme}>
      <StatusBar style="light" />
      <AuthProvider><Stack screenOptions={{ headerShown: false }}><Stack.Screen name="(tabs)" /></Stack></AuthProvider>
    </ThemeProvider>
  );
}
