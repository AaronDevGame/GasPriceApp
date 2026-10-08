import { useCallback, useEffect, useRef, useState } from 'react';
import { subscribeToForeground } from '@/auth/foreground';
import { getLatestNews, type NewsItem } from './api';
import { useNewsClock } from './use-news-clock';

export function useLatestNews() {
  const [item, setItem] = useState<NewsItem | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);
  const generation = useRef(0);
  const now = useNewsClock();
  const reload = useCallback(async () => {
    const current = ++generation.current;
    setLoading(true);
    try {
      const result = await getLatestNews();
      if (generation.current === current) { setItem(result); setError(false); }
    } catch { if (generation.current === current) setError(true); }
    finally { if (generation.current === current) setLoading(false); }
  }, []);
  useEffect(() => {
    const requests = generation;
    const current = ++requests.current;
    void getLatestNews().then(result => {
      if (requests.current === current) { setItem(result); setError(false); }
    }, () => { if (requests.current === current) setError(true); })
      .finally(() => { if (requests.current === current) setLoading(false); });
    const unsubscribe = subscribeToForeground(() => void reload());
    return () => { requests.current++; unsubscribe(); };
  }, [reload]);
  const visible = item && Date.parse(item.article.expiresAtUtc) > now && !item.supersededById ? item : null;
  return { item: visible, loading, error, reload };
}
