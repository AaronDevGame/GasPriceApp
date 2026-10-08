import { send } from '@/auth/http';

export type NewsChange = {
  fuel: 'gasoline' | 'diesel' | 'kerosene'; oilCompany: string | null;
  minChangePerLiter: number; maxChangePerLiter: number; status: 'forecast' | 'confirmed';
};
export type NewsItem = {
  id: string; revision: number; publishedAtUtc: string; updatedAtUtc: string;
  supersededById: string | null; isExpired: boolean;
  article: {
    importKey: string; topicKey: string; category: 'adjustment' | 'general';
    status: 'forecast' | 'confirmed' | 'general'; title: string; summary: string; body: string;
    effectiveDatePhilippines: string | null; effectiveAtUtc: string | null; expiresAtUtc: string;
    adjustments: NewsChange[];
    sources: { name: string; url: string; publishedAtUtc: string }[];
  };
};
export type NewsPage = { items: NewsItem[]; nextCursor: string | null };
export const newsIdPattern = /^[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}$/i;
export const getLatestNews = () => send<NewsItem | null>('/fuel-news/latest');
export const getNewsPage = (cursor?: string) => send<NewsPage>(`/fuel-news?limit=20${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`);
export const getNewsItem = (id: string) => send<NewsItem>(`/fuel-news/${id}`);

export function newsDate(value: string, includeTime = false) {
  return new Date(value.length === 10 ? `${value}T12:00:00+08:00` : value).toLocaleString('en-PH', {
    timeZone: 'Asia/Manila', month: 'short', day: 'numeric', year: 'numeric',
    ...(includeTime ? { hour: 'numeric' as const, minute: '2-digit' as const } : {}),
  });
}

export function changeLabel(change: NewsChange) {
  const { minChangePerLiter: min, maxChangePerLiter: max } = change;
  const amount = (n: number) => `₱${Math.abs(n).toFixed(2)}`;
  if (min === 0 && max === 0) return 'No change · ₱0.00/L';
  if (min >= 0) return `↑ Increase ${amount(min)}${min !== max ? `–${amount(max)}` : ''}/L`;
  if (max <= 0) return `↓ Decrease ${amount(max)}${min !== max ? `–${amount(min)}` : ''}/L`;
  return `↓ ${amount(min)} to ↑ ${amount(max)}/L`;
}
