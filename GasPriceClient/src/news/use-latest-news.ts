import { useCallback, useEffect, useRef, useState } from 'react';
import { subscribeToForeground } from '@/auth/foreground';
import { getNewsPage, type NewsItem } from './api';
import { useNewsClock } from './use-news-clock';

export function useLatestNews({ refreshOnForeground = true } = {}) {
  const [feed, setFeed] = useState<NewsItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);
  const generation = useRef(0);
  const now = useNewsClock();
  const load = useCallback(() => {
    const current = ++generation.current;
    return getNewsPage().then(page => {
      if (generation.current === current) { setFeed(page.items); setError(false); }
    }, () => {
      if (generation.current === current) setError(true);
    }).finally(() => {
      if (generation.current === current) setLoading(false);
    });
  }, []);
  const reload = useCallback(() => {
    setLoading(true);
    return load();
  }, [load]);
  useEffect(() => {
    const requests = generation;
    void load();
    const unsubscribe = refreshOnForeground ? subscribeToForeground(() => void reload()) : () => {};
    return () => { requests.current++; unsubscribe(); };
  }, [load, reload, refreshOnForeground]);
  return { items: selectHeadlines(feed, now), loading, error, reload };
}

// The feed is ordered by publication time, newest first.
export function selectHeadlines(feed: NewsItem[], now: number) {
  const cutoff = now - 7 * 24 * 60 * 60 * 1000;
  const selected: NewsItem[] = [];
  const topics = new Set<string>();
  let hasAdjustment = false;
  for (const item of feed) {
    const { article } = item;
    const published = Date.parse(item.publishedAtUtc);
    if (published < cutoff) break;
    if (published > now || item.isExpired || item.supersededById || Date.parse(article.expiresAtUtc) <= now) continue;
    const adjustment = article.category === 'adjustment';
    if (topics.has(article.topicKey) || (adjustment && hasAdjustment)) continue;
    topics.add(article.topicKey);
    hasAdjustment ||= adjustment;
    selected.push(item);
    if (selected.length === 3) break;
  }
  return selected;
}
