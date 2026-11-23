import { ApiError, send } from '@/auth/http';
import type { FuelAdjustment } from './api';

export type AdviceFuel = 'gasoline' | 'diesel' | 'kerosene';
export type AdviceStatus = 'gas_now' | 'wait' | 'neutral' | 'unknown';
export type AdviceEvidence = {
  confirmationStatus: 'confirmed' | 'forecast'; effectiveDatePhilippines: string;
  effectiveAtUtc: string | null; minChangePerLiter: number; maxChangePerLiter: number;
  oilCompany: string | null; newsId: string | null; adjustmentId: number | null;
  sourceUrls: string[]; validUntilUtc: string; isEffectiveTimeDefaulted?: boolean;
};
export type FuelAdvice = {
  fuelType: AdviceFuel; oilCompany: string | null; status: AdviceStatus; reason: string;
  message: string; evaluatedAtUtc: string; validUntilUtc: string | null;
  evidence: AdviceEvidence[]; forecast: AdviceEvidence | null;
  currentAdjustments?: AdviceEvidence[];
};

export async function getFuelAdvice(fuelType: AdviceFuel, oilCompany: string | null) {
  const params = new URLSearchParams({ fuelType });
  if (oilCompany) params.set('oilCompany', oilCompany);
  const advice = await send<FuelAdvice>(`/fuel-advice?${params}`);
  if (!advice || advice.fuelType !== fuelType || advice.oilCompany !== oilCompany ||
      !['gas_now', 'wait', 'neutral', 'unknown'].includes(advice.status) || typeof advice.message !== 'string' ||
      !Array.isArray(advice.evidence) || !Number.isFinite(Date.parse(advice.evaluatedAtUtc)) ||
      (advice.validUntilUtc !== null && !Number.isFinite(Date.parse(advice.validUntilUtc))))
    throw new ApiError(200, 'invalid_api_response');
  return advice;
}

export function forecastLabel(forecast: AdviceEvidence | null) {
  if (forecast && forecast.maxChangePerLiter < 0) return 'Price drop not yet confirmed';
  if (forecast && forecast.minChangePerLiter > 0) return 'Price increase not yet confirmed';
  return 'Price change not yet confirmed';
}

export function adviceHeadline(advice: FuelAdvice | null, loading: boolean, error: boolean) {
  if (!advice) return loading ? 'Checking fuel advice…' : error ? 'Advice temporarily unavailable' : 'No current advice available';
  switch (advice.status) {
    case 'gas_now': return advice.reason === 'increase_upcoming' ? 'Refuel before the increase' : 'Good time to refuel';
    case 'wait': return 'Wait for the rollback';
    case 'neutral': return 'No clear advantage to filling up now';
    default: return advice.reason === 'forecast_only' ? forecastLabel(advice.forecast) : 'No clear recommendation yet';
  }
}

export function adviceExpiresAt(advice: FuelAdvice) {
  const evaluated = Date.parse(advice.evaluatedAtUtc);
  const evidence = [...advice.evidence, ...(advice.forecast ? [advice.forecast] : []), ...(advice.currentAdjustments ?? [])];
  const deadlines = [Date.parse(advice.validUntilUtc ?? ''), ...evidence.flatMap(item => {
    const effective = Date.parse(item.effectiveAtUtc ?? '');
    return [Date.parse(item.validUntilUtc), ...(effective > evaluated ? [effective] : [])];
  })].filter(Number.isFinite);
  return Math.min(Infinity, ...deadlines);
}

export function adviceIsCurrent(advice: FuelAdvice, now: number) {
  return adviceExpiresAt(advice) > now;
}

// Keep price direction independent from the recommendation to buy or wait.
export function adviceSummary(advice: FuelAdvice | null, loading: boolean, error: boolean) {
  const action = !advice ? (loading ? 'Checking…' : error ? 'Temporarily unavailable' : 'No confirmed update')
    : advice.status === 'gas_now' ? 'Gas up now'
    : advice.status === 'wait' ? 'Wait if you can'
    : advice.reason === 'conflicting_adjustments' ? 'Check sources'
    : advice.reason === 'forecast_only' ? forecastLabel(advice.forecast)
    : advice.reason === 'no_price_change' ? 'No price change' : 'No clear advantage';
  const confirmed = advice?.reason !== 'conflicting_adjustments' && advice?.evidence.length
    && advice.evidence.every(e => e.confirmationStatus === 'confirmed'
      && e.minChangePerLiter === advice.evidence[0].minChangePerLiter
      && e.maxChangePerLiter === advice.evidence[0].maxChangePerLiter
      && e.effectiveAtUtc === advice.evidence[0].effectiveAtUtc);
  const evidence = confirmed ? advice.evidence[0] : advice?.evidence.length ? null : advice?.forecast;
  if (!evidence || !Number.isFinite(evidence.minChangePerLiter) || !Number.isFinite(evidence.maxChangePerLiter))
    return { action, direction: 'none', amount: '—', timing: advice?.reason === 'conflicting_adjustments'
      ? 'Sources disagree' : '' };
  const min = evidence.minChangePerLiter, max = evidence.maxChangePerLiter;
  const direction = max < 0 ? 'down' : min > 0 ? 'up' : min === 0 && max === 0 ? 'flat' : 'mixed';
  const amount = min === max ? `₱${Math.abs(min).toFixed(2)}/L`
    : direction === 'mixed' ? `${min > 0 ? '+' : ''}${min.toFixed(2)} to ${max > 0 ? '+' : ''}${max.toFixed(2)} ₱/L`
    : `₱${Math.min(Math.abs(min), Math.abs(max)).toFixed(2)}–${Math.max(Math.abs(min), Math.abs(max)).toFixed(2)}/L`;
  const date = new Date(evidence.effectiveAtUtc ?? `${evidence.effectiveDatePhilippines}T12:00:00+08:00`);
  const when = date.toLocaleDateString('en-PH', { timeZone: 'Asia/Manila', month: 'short', day: 'numeric' });
  return { action, direction, amount, timing: `${confirmed ? 'Effective' : 'Estimated effective'} ${when}` };
}

export function adviceCurrentDecrease(advice: FuelAdvice) {
  if (advice.status !== 'gas_now' || advice.reason !== 'rollback_effective' ||
      !advice.evidence.length || advice.evidence[0].minChangePerLiter >= 0) return null;
  const impact = adviceImpact(advice);
  return impact ? `↓ ₱${impact.amount.toFixed(2)}/L decrease now in effect` : null;
}

function decreaseAmount(min: number, max: number) {
  const low = Math.abs(max).toFixed(2);
  return min === max ? `₱${low}/L` : `₱${low}–₱${Math.abs(min).toFixed(2)}/L`;
}

export function summarizeWeeklyAdjustments(rows: FuelAdjustment[], fuel: AdviceFuel) {
  const reported = new Map<number, Set<string>>();
  const companies = new Set<string>();
  const withAmounts = new Set<string>();
  for (const row of rows) {
    const company = row.oilCompany.trim().toLowerCase();
    companies.add(company);
    const amount = row[`${fuel}ChangePerLiter`];
    if (amount === null) continue;
    withAmounts.add(company);
    const group = reported.get(amount) ?? new Set<string>();
    group.add(company);
    reported.set(amount, group);
  }
  const amounts = [...reported.keys()].sort((a, b) => a - b);
  const groups = amounts.length > 1 && amounts[amounts.length - 1] < 0
    ? [{ min: amounts[0], max: amounts[amounts.length - 1], count: withAmounts.size }]
    : amounts.map(amount => ({ min: amount, max: amount, count: reported.get(amount)!.size }));
  return { groups, missing: companies.size - withAmounts.size };
}

export function weeklyAdjustmentLabel(min: number, max: number) {
  if (min < 0) return `↓ ${decreaseAmount(min, max)}`;
  return min === 0 ? 'No change' : `↑ ₱${min.toFixed(2)}/L`;
}

// Company reports provide current context even when nationwide advice has only a forecast.
export function adviceCurrentDecreaseContext(advice: FuelAdvice, now = Date.now()) {
  if (advice.status !== 'unknown' || !['forecast_only', 'no_confirmed_adjustment'].includes(advice.reason) ||
      advice.evidence.length || !advice.currentAdjustments?.length) return null;
  const rows = advice.currentAdjustments;
  if (rows.some(row => row.confirmationStatus !== 'confirmed' ||
    !Number.isFinite(row.minChangePerLiter) || !Number.isFinite(row.maxChangePerLiter) ||
    row.minChangePerLiter > row.maxChangePerLiter || row.maxChangePerLiter >= 0 ||
    row.effectiveDatePhilippines !== rows[0].effectiveDatePhilippines ||
    !Number.isFinite(Date.parse(row.effectiveAtUtc ?? '')) || Date.parse(row.effectiveAtUtc!) > now ||
    !(Date.parse(row.validUntilUtc) > now) ||
    (advice.oilCompany !== null && row.oilCompany !== null && row.oilCompany.toLowerCase() !== advice.oilCompany.toLowerCase()))) return null;
  return {
    change: `↓ ${decreaseAmount(Math.min(...rows.map(row => row.minChangePerLiter)),
      Math.max(...rows.map(row => row.maxChangePerLiter)))} decrease now in effect`,
    companies: [...new Set(rows.flatMap(row => row.oilCompany ? [row.oilCompany] : []))],
    evidence: rows[0],
  };
}

// Only calculate the example from agreed confirmed amounts; forecasts stay separate.
export function adviceImpact(advice: FuelAdvice) {
  if (advice.status === 'unknown' || !advice.evidence.length) return null;
  const amount = advice.evidence[0].minChangePerLiter;
  if (!Number.isFinite(amount) || amount === 0 || advice.evidence.some(e =>
    e.confirmationStatus !== 'confirmed' || e.minChangePerLiter !== amount || e.maxChangePerLiter !== amount)) return null;
  const upcoming = advice.reason === 'rollback_upcoming' || advice.reason === 'increase_upcoming';
  return {
    amount: Math.abs(amount), total: Math.abs(amount) * 40,
    label: upcoming ? (amount < 0 ? 'Potential savings by waiting · 40 L' : 'Potential savings by filling before the increase · 40 L')
      : (amount < 0 ? 'Price reduction on a 40 L fill' : 'Price increase on a 40 L fill'),
  };
}

export function adviceTime(evidence: AdviceEvidence) {
  if (evidence.effectiveAtUtc) return new Date(evidence.effectiveAtUtc).toLocaleString('en-PH', {
    timeZone: 'Asia/Manila', weekday: 'short', month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit',
  }) + ' PHT';
  return new Date(`${evidence.effectiveDatePhilippines}T12:00:00+08:00`).toLocaleDateString('en-PH', {
    timeZone: 'Asia/Manila', weekday: 'short', month: 'short', day: 'numeric', year: 'numeric',
  }) + ' · Time not provided';
}
