export const stationPinSize = { width: 40, height: 36 };
export const stationPinAnchor = { x: 20, y: 34 };
export const stationPinLabel = { x: 20, y: 13 };

const singlePin: number = require('./data/station-pin-single.svg');
const pairPin: number = require('./data/station-pin-pair.svg');
const stackedPin: number = require('./data/station-pin-stacked.svg');

export function stationPinImage(count: number) {
  if (count >= 3) return stackedPin;
  return count === 2 ? pairPin : singlePin;
}
