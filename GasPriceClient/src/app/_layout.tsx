import { DarkTheme, DefaultTheme, Stack, ThemeProvider, router } from 'expo-router';
import { useEffect } from 'react';
import { useColorScheme } from 'react-native';

import { AuthProvider } from '@/auth/auth-provider';
import { listenForNewsNotifications } from '@/news/push';
import { newsIdPattern } from '@/news/api';

export default function RootLayout() {
  const colorScheme = useColorScheme();
  useEffect(() => listenForNewsNotifications(id => {
    if (newsIdPattern.test(id)) router.push({ pathname: '/news-detail', params: { id } });
  }), []);
  return (
    <ThemeProvider value={colorScheme === 'dark' ? DarkTheme : DefaultTheme}>
      <AuthProvider><Stack screenOptions={{ headerShown: false }}><Stack.Screen name="(tabs)" /></Stack></AuthProvider>
    </ThemeProvider>
  );
}
