import { ApiError, send } from '@/auth/http';

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

export function adviceHeadline(advice: FuelAdvice | null, loading: boolean, error: boolean) {
  if (!advice) return loading ? 'Checking fuel advice…' : error ? 'Advice temporarily unavailable' : 'No current advice available';
  switch (advice.status) {
    case 'gas_now': return advice.reason === 'increase_upcoming' ? 'Gas up before the increase' : 'Good time to gas up';
    case 'wait': return 'Wait for the rollback';
    case 'neutral': return 'No clear advantage to filling up now';
    default: return advice.reason === 'forecast_only' ? 'Awaiting a confirmed update' : 'No clear recommendation yet';
  }
}

export function adviceIsCurrent(advice: FuelAdvice, now: number) {
  return advice.validUntilUtc === null || Date.parse(advice.validUntilUtc) > now;
}

// Keep price direction independent from the recommendation to buy or wait.
export function adviceSummary(advice: FuelAdvice | null, loading: boolean, error: boolean) {
  const action = !advice ? (loading ? 'Checking…' : error ? 'Temporarily unavailable' : 'No confirmed update')
    : advice.status === 'gas_now' ? 'Gas up now'
    : advice.status === 'wait' ? 'Wait if you can'
    : advice.reason === 'conflicting_adjustments' ? 'Check sources'
    : advice.reason === 'forecast_only' ? 'Await confirmation'
    : advice.reason === 'no_price_change' ? 'No price change' : 'No clear advantage';
  const confirmed = advice?.reason !== 'conflicting_adjustments' && advice?.evidence.length
    && advice.evidence.every(e => e.confirmationStatus === 'confirmed'
      && e.minChangePerLiter === advice.evidence[0].minChangePerLiter
      && e.maxChangePerLiter === advice.evidence[0].maxChangePerLiter
      && e.effectiveAtUtc === advice.evidence[0].effectiveAtUtc);
  const evidence = confirmed ? advice.evidence[0] : advice?.evidence.length ? null : advice?.forecast;
  if (!evidence || !Number.isFinite(evidence.minChangePerLiter) || !Number.isFinite(evidence.maxChangePerLiter))
    return { action, direction: 'none', amount: '—', timing: advice?.reason === 'conflicting_adjustments'
      ? 'Sources disagree' : 'Nationwide · No current adjustment' };
  const min = evidence.minChangePerLiter, max = evidence.maxChangePerLiter;
  const direction = max < 0 ? 'down' : min > 0 ? 'up' : min === 0 && max === 0 ? 'flat' : 'mixed';
  const amount = min === max ? `₱${Math.abs(min).toFixed(2)}/L`
    : direction === 'mixed' ? `${min > 0 ? '+' : ''}${min.toFixed(2)} to ${max > 0 ? '+' : ''}${max.toFixed(2)} ₱/L`
    : `₱${Math.min(Math.abs(min), Math.abs(max)).toFixed(2)}–${Math.max(Math.abs(min), Math.abs(max)).toFixed(2)}/L`;
  const date = new Date(evidence.effectiveAtUtc ?? `${evidence.effectiveDatePhilippines}T12:00:00+08:00`);
  const when = date.toLocaleDateString('en-PH', { timeZone: 'Asia/Manila', month: 'short', day: 'numeric' });
  const time = evidence.effectiveAtUtc ? date.toLocaleTimeString('en-PH', {
    timeZone: 'Asia/Manila', hour: 'numeric', minute: '2-digit',
  }) + ' PHT' : 'time unannounced';
  const state = !confirmed ? 'Estimated' : Date.parse(evidence.effectiveAtUtc ?? '') <= Date.now() ? 'Now effective' : 'Confirmed';
  return { action, direction, amount, timing: `${state} · ${when}, ${time}${evidence.isEffectiveTimeDefaulted ? ' (default)' : ''}` };
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
