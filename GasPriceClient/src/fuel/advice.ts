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
