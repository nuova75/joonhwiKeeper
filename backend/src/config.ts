export const config = {
  port: Number(process.env.PORT ?? 8080),
  host: process.env.HOST ?? '0.0.0.0',
  dbPath: process.env.DB_PATH ?? './data/xkeep.db',
  vapid: {
    publicKey: process.env.VAPID_PUBLIC_KEY ?? '',
    privateKey: process.env.VAPID_PRIVATE_KEY ?? '',
    subject: process.env.VAPID_SUBJECT ?? 'mailto:admin@example.invalid',
  },
  fcmServiceAccountPath: process.env.FCM_SERVICE_ACCOUNT ?? '',
  offlineAlertMinutes: Number(process.env.OFFLINE_ALERT_MINUTES ?? 20),
  pairingCodeTtlMinutes: 10,
  /** 하루 요약 푸시 시각(기기 시간대 기준 HH:mm). 빈 문자열이면 끔 */
  summaryTime: process.env.SUMMARY_TIME ?? '21:30',
} as const;

export type Config = typeof config;
