/**
 * 에이전트 ↔ 서버 ↔ 부모앱 통신 규약의 기준 구현.
 * shared/protocol/PROTOCOL.md 와 fixtures/*.json 이 이 스키마를 따른다.
 * Kotlin(android-agent) / C#(windows-agent) 모델은 이 파일과 필드명을 맞춘다.
 */
import { z } from 'zod';

export const PlatformSchema = z.enum(['android', 'windows']);
export type Platform = z.infer<typeof PlatformSchema>;

const HHMM = z.string().regex(/^([01]\d|2[0-3]):[0-5]\d$/, 'HH:mm 형식');
const YMD = z.string().regex(/^\d{4}-\d{2}-\d{2}$/, 'YYYY-MM-DD 형식');

const TimezoneSchema = z.string().refine((tz) => {
  try {
    new Intl.DateTimeFormat('en-US', { timeZone: tz });
    return true;
  } catch {
    return false;
  }
}, '유효한 IANA 시간대가 아님');

export const BedtimeSchema = z.object({ start: HHMM, end: HHMM });

/**
 * 사이트 입력을 Chrome/Edge URL 필터 형식(도메인, 하위 도메인 포함)으로 정규화.
 *   "https://www.ebsi.co.kr/main" → "ebsi.co.kr/main"
 *   "*.eunjae.co.kr" → "eunjae.co.kr"
 * 도메인 형태가 아니면 null (Chrome 필터는 "*ebs*" 같은 중간 와일드카드를 지원하지 않음).
 */
export function normalizeSite(input: string): string | null {
  let s = input.trim().toLowerCase();
  s = s.replace(/^[a-z][a-z0-9+.-]*:\/\//, ''); // scheme
  s = s.replace(/^\*\.?/, '').replace(/\/?\*$/, '').replace(/\/+$/, '');
  s = (s.split(/[?#]/)[0] ?? '').replace(/^www\./, '');
  return /^[a-z0-9-]+(\.[a-z0-9-]+)*\.[a-z]{2,}(\/[^\s*]*)?$/.test(s) ? s : null;
}

const SiteSchema = z.string().transform((v, ctx) => {
  const n = normalizeSite(v);
  if (!n) {
    ctx.addIssue({ code: z.ZodIssueCode.custom, message: `사이트 형식이 아님: ${v}` });
    return z.NEVER;
  }
  return n;
});

/** 학습 시간표 한 칸. days 는 ISO 요일(1=월 … 7=일), start < end (자정 넘김 없음) */
export const StudyBlockSchema = z
  .object({
    days: z.array(z.number().int().min(1).max(7)).min(1).max(7),
    start: HHMM,
    end: HHMM,
  })
  .refine((b) => b.start < b.end, '시작 시각이 종료 시각보다 빨라야 함');

export const StudySchema = z.object({
  /** 학습 모드에서 허용할 앱. Android: 패키지명 / Windows: exe 이름 또는 전체 경로 */
  apps: z.array(z.string().min(1).max(260)).max(200),
  /** 학습 모드에서 허용할 사이트 (도메인 단위, 하위 도메인 포함) */
  sites: z.array(SiteSchema).max(200),
  /** 자동으로 학습 모드가 켜지는 시간표 */
  schedule: z.array(StudyBlockSchema).max(30),
});
export type Study = z.infer<typeof StudySchema>;

export const PolicySchema = z.object({
  /** 서버가 정책을 바꿀 때마다 1씩 증가. 에이전트는 이 값으로 최신 여부를 판단. */
  version: z.number().int().nonnegative(),
  /** 하루 경계(자정)와 취침 시간 계산 기준 */
  timezone: TimezoneSchema,
  /** 하루 허용 사용 시간(분). null = 제한 없음 */
  dailyLimitMinutes: z.number().int().min(0).max(1440).nullable(),
  /** 취침 시간대(자정 넘김 허용, 예: 22:00~07:00). null = 없음 */
  bedtime: BedtimeSchema.nullable(),
  /** Android: 패키지명(com.example.game) / Windows: 실행 파일명(game.exe) */
  blockedApps: z.array(z.string().min(1).max(256)).max(500),
  /** 잠금 중에도 허용할 앱 (Android 전용: 전화, 메시지 등) */
  alwaysAllowed: z.array(z.string().min(1).max(256)).max(200),
  /** 부모의 즉시 잠금. 명령이 아니라 정책 상태라서 오프라인 기기도 재접속 시 반영됨 */
  manualLock: z.boolean(),
  /** 오늘만 적용되는 추가 시간 (date 가 기기 기준 오늘일 때만 유효) */
  extraTime: z.object({ date: YMD, minutes: z.number().int().min(0).max(720) }).nullable(),
  /** 학습 모드 설정: 이 시간 동안은 허용 목록에 있는 앱·사이트만 사용 가능 */
  study: StudySchema.default({ apps: [], sites: [], schedule: [] }),
  /** 시간표와 별개로 지금 진행 중인 학습 세션 (부모가 켜거나 아이가 "공부 시작") */
  studySession: z
    .object({ until: z.string().datetime({ offset: true }), by: z.enum(['parent', 'child']) })
    .nullable()
    .default(null),
  /** 요청 승인으로 "오늘만" 허용된 앱·사이트 (date 가 오늘일 때만 유효) */
  grants: z
    .object({ date: YMD, apps: z.array(z.string().max(260)).max(100), sites: z.array(z.string().max(260)).max(100) })
    .nullable()
    .default(null),
});
export type Policy = z.infer<typeof PolicySchema>;

/** 부모가 PUT 으로 수정 가능한 필드 (version/manualLock/extraTime 은 전용 API 사용) */
export const PolicyUpdateSchema = PolicySchema.pick({
  timezone: true,
  dailyLimitMinutes: true,
  bedtime: true,
  blockedApps: true,
  alwaysAllowed: true,
  study: true,
}).partial();

/** 학습 사이트 기본값 (가족이 쓰는 학원·EBS·밴드) */
export const DEFAULT_STUDY_SITES = ['eunjae.co.kr', 'hakwonsarang.co.kr', 'ebsi.co.kr', 'ebs.co.kr', 'band.us'];
export const DEFAULT_STUDY_APPS: Record<Platform, string[]> = {
  android: ['com.android.chrome', 'com.nhn.android.band'],
  windows: ['chrome.exe', 'msedge.exe'],
};

export function defaultPolicy(platform: Platform = 'android'): Policy {
  return {
    version: 1,
    timezone: 'Asia/Seoul',
    dailyLimitMinutes: 120,
    bedtime: null,
    blockedApps: [],
    alwaysAllowed: [],
    manualLock: false,
    extraTime: null,
    study: { apps: [...DEFAULT_STUDY_APPS[platform]], sites: [...DEFAULT_STUDY_SITES], schedule: [] },
    studySession: null,
    grants: null,
  };
}

// ---------- 에이전트 → 서버 ----------
export const AgentMessageSchema = z.discriminatedUnion('type', [
  z.object({
    type: z.literal('hello'),
    agentVersion: z.string().max(64),
    policyVersion: z.number().int().nonnegative(),
  }),
  z.object({
    type: z.literal('usage'),
    date: YMD,
    usedMinutes: z.number().int().min(0).max(1440),
    perApp: z.record(z.string(), z.number().int().min(0)).default({}),
    locked: z.boolean(),
    lockReason: z.enum(['manual', 'daily_limit', 'bedtime', 'tamper']).nullable(),
    /** 오늘 학습 모드에서 사용한 시간(분). 하루 한도는 (usedMinutes - studyMinutes) 기준 */
    studyMinutes: z.number().int().min(0).max(1440).default(0),
    /** 현재 모드 */
    mode: z.enum(['free', 'study', 'locked']).default('free'),
  }),
  z.object({
    /** 설치된 앱(Android) / 최근 실행된 프로그램(Windows) 목록. 부모앱에서 골라서 허용·차단 */
    type: z.literal('apps'),
    apps: z.array(z.object({ id: z.string().min(1).max(260), label: z.string().max(128) })).max(1000),
  }),
  z.object({
    /** 아이의 "학습에 필요해요" 요청 */
    type: z.literal('access_request'),
    kind: z.enum(['site', 'app']),
    value: z.string().min(1).max(500),
    note: z.string().max(200).default(''),
  }),
  z.object({
    /** 아이가 직접 "공부 시작" (끝내는 것은 부모만 가능) */
    type: z.literal('study_start'),
    minutes: z.number().int().min(10).max(240),
  }),
  z.object({
    type: z.literal('processes'),
    names: z.array(z.string().max(256)).max(1000),
  }),
  z.object({
    type: z.literal('event'),
    kind: z.string().max(64),
    detail: z.string().max(1024).default(''),
    at: z.string().datetime({ offset: true }),
  }),
  z.object({
    type: z.literal('goodbye'),
    reason: z.string().max(64),
  }),
]);
export type AgentMessage = z.infer<typeof AgentMessageSchema>;

// ---------- 서버 → 에이전트 ----------
export type RequestDecision = 'today' | 'always' | 'deny';
export type ServerToAgentMessage =
  | { type: 'policy'; policy: Policy }
  | { type: 'request_result'; requestId: number | null; kind: 'site' | 'app'; value: string; decision: RequestDecision | 'invalid' };

// ---------- 서버 → 부모 ----------
export type ServerToParentMessage =
  | { type: 'device_status'; deviceId: string }
  | { type: 'device_event'; deviceId: string; kind: string; detail: string; at: string }
  | { type: 'requests_changed' };

export const PairRequestSchema = z.object({
  code: z.string().regex(/^\d{6}$/),
  platform: PlatformSchema,
  agentVersion: z.string().max(64),
  fcmToken: z.string().max(4096).optional(),
});
