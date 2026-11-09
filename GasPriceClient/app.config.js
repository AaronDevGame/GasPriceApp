// Keys come from local ignored environment files or build-service environment settings.
// Native keys are embedded by their SDKs; restrict them by app ID/signing certificate in Google Cloud.
module.exports = ({ config }) => ({
  ...config,
  ios: { ...config.ios, ...(process.env.IOS_BUNDLE_IDENTIFIER ? { bundleIdentifier: process.env.IOS_BUNDLE_IDENTIFIER } : {}) },
  android: { ...config.android, ...(process.env.ANDROID_APPLICATION_ID ? { package: process.env.ANDROID_APPLICATION_ID } : {}) },
  plugins: [...(config.plugins ?? []), ['react-native-maps', {
    ...(process.env.GOOGLE_MAPS_ANDROID_API_KEY ? { androidGoogleMapsApiKey: process.env.GOOGLE_MAPS_ANDROID_API_KEY } : {}),
    ...(process.env.GOOGLE_MAPS_IOS_API_KEY ? { iosGoogleMapsApiKey: process.env.GOOGLE_MAPS_IOS_API_KEY } : {}),
  }]],
  extra: { ...config.extra, fuelMap: {
    androidConfigured: !!process.env.GOOGLE_MAPS_ANDROID_API_KEY,
    iosConfigured: !!process.env.GOOGLE_MAPS_IOS_API_KEY,
  } },
});
