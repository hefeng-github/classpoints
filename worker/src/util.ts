/** 统一的 JSON 响应、错误、编码小工具。不引任何依赖，Worker 冷启动才快。 */
import type { Env } from './types';

export class HttpError extends Error {
  constructor(readonly status: number, readonly code: string, message: string) {
    super(message);
  }
}

export function json(data: unknown, status = 200): Response {
  return new Response(JSON.stringify(data), {
    status,
    headers: { 'content-type': 'application/json; charset=utf-8' },
  });
}

/**
 * 预检。真正的 CORS 头由 harden() 按白名单补 —— 这里只回 204，
 * 不回 access-control-allow-origin，等于对浏览器说"跨域读不了"，正是我们要的。
 */
export function preflight(): Response {
  return new Response(null, {
    status: 204,
    headers: {
      'access-control-allow-methods': 'GET,POST,PATCH,OPTIONS',
      'access-control-allow-headers': 'content-type,authorization,if-match,x-bootstrap-secret',
      'access-control-max-age': '86400',
      vary: 'origin',
    },
  });
}

/**
 * 所有响应统一加安全头，顺便按白名单决定给不给 CORS。
 *
 * 为什么默认不给 CORS：这个服务端只有 WinUI 客户端在调，没有任何浏览器端用法。
 * 原来回 `*` 是没有实际危害的（凭据走 Authorization 头，不是 Cookie，`*` 不能带凭据），
 * 但"没人用却允许跨域读取"本身就是多余的攻击面：一旦哪天有人在网页里嵌了这段 API，
 * 通配 CORS 会让浏览器的同源策略形同虚设。要开的话显式配 ALLOWED_ORIGINS。
 */
export function harden(res: Response, request: Request, env: Env): Response {
  const h = new Headers(res.headers);
  h.set('strict-transport-security', 'max-age=31536000; includeSubDomains');
  h.set('x-content-type-options', 'nosniff');
  h.set('x-frame-options', 'DENY');
  h.set('referrer-policy', 'no-referrer');
  // 这个域只吐 JSON，没有任何静态资源，所以 CSP 可以一刀切到 'none'
  h.set('content-security-policy', "default-src 'none'; frame-ancestors 'none'; base-uri 'none'");
  h.set('cross-origin-resource-policy', 'same-origin');

  const origin = request.headers.get('origin');
  const allow = (env.ALLOWED_ORIGINS ?? '')
    .split(',')
    .map((s) => s.trim())
    .filter(Boolean);
  if (origin && allow.includes(origin)) {
    h.set('access-control-allow-origin', origin);
    h.set('vary', 'origin');
  }
  return new Response(res.body, { status: res.status, statusText: res.statusText, headers: h });
}

/**
 * 出错了怎么办。
 *
 * 5xx 一律不回原始 message：D1 的报错里带着表名、列名甚至 SQL 片段，
 * 那是给运维看的东西，不该送到任何一个客户端手里。只回一个 requestId，
 * 拿着它去 `wrangler tail` 里搜同一条日志。
 */
export function fail(e: unknown): Response {
  if (e instanceof HttpError) {
    return json({ error: e.code, message: e.message }, e.status);
  }
  const requestId = randomHex(4);
  const message = e instanceof Error ? e.message : String(e);
  console.error(`[${requestId}] unhandled`, message, e instanceof Error ? e.stack : '');
  return json({ error: 'internal', message: '服务器内部错误', requestId }, 500);
}

/** 单次请求体上限。最大的一批是 50 条流水，实际不到 40 KB，1 MB 是留足余量后的硬顶。 */
const MAX_BODY_BYTES = 1024 * 1024;

export async function readJson<T>(request: Request): Promise<T> {
  const declared = Number(request.headers.get('content-length') ?? '0');
  if (Number.isFinite(declared) && declared > MAX_BODY_BYTES) {
    throw new HttpError(413, 'body_too_large', '请求体太大');
  }
  const text = await request.text();
  // Content-Length 可以撒谎（chunked 上传就没有这个头），所以读完再量一次
  if (text.length > MAX_BODY_BYTES) throw new HttpError(413, 'body_too_large', '请求体太大');
  if (!text) throw new HttpError(400, 'empty_body', '请求体是空的');
  try {
    return JSON.parse(text) as T;
  } catch {
    throw new HttpError(400, 'bad_json', '请求体不是合法 JSON');
  }
}

export const now = (): number => Date.now();

export function requireString(v: unknown, field: string, maxLen = 200): string {
  if (typeof v !== 'string' || v.trim() === '') {
    throw new HttpError(422, 'invalid_' + field, `${field} 不能为空`);
  }
  if (v.length > maxLen) throw new HttpError(422, 'invalid_' + field, `${field} 太长`);
  return v;
}

export function optionalString(v: unknown, maxLen = 500): string | null {
  if (v === null || v === undefined) return null;
  if (typeof v !== 'string') return null;
  return v.length > maxLen ? v.slice(0, maxLen) : v;
}

export function intOrNull(v: unknown): number | null {
  return typeof v === 'number' && Number.isFinite(v) ? Math.trunc(v) : null;
}

/** 0/1 —— SQLite 没有布尔 */
export const bit = (v: unknown): number => (v ? 1 : 0);

export function randomHex(bytes: number): string {
  const buf = new Uint8Array(bytes);
  crypto.getRandomValues(buf);
  return [...buf].map((b) => b.toString(16).padStart(2, '0')).join('');
}

/** 班级码：去掉 0/O/1/I，人念得出来，也不至于念错 */
const CODE_ALPHABET = '23456789ABCDEFGHJKLMNPQRSTUVWXYZ';

export function classCode(len = 8): string {
  const buf = new Uint8Array(len);
  crypto.getRandomValues(buf);
  return [...buf].map((b) => CODE_ALPHABET[b % CODE_ALPHABET.length]).join('');
}

/**
 * UUIDv7：48 位毫秒时间 + 版本位 + 随机。
 * 主键带时间前缀，索引友好；时间戳也让"两台设备各自生成"不会撞。
 */
export function uuidv7(): string {
  const ms = Date.now();
  const b = new Uint8Array(16);
  crypto.getRandomValues(b);
  // 前 6 字节 = 时间戳（大端）
  b[0] = (ms / 2 ** 40) & 0xff;
  b[1] = (ms / 2 ** 32) & 0xff;
  b[2] = (ms / 2 ** 24) & 0xff;
  b[3] = (ms / 2 ** 16) & 0xff;
  b[4] = (ms / 2 ** 8) & 0xff;
  b[5] = ms & 0xff;
  b[6] = (b[6] & 0x0f) | 0x70; // version 7
  b[8] = (b[8] & 0x3f) | 0x80; // variant
  const hex = [...b].map((x) => x.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/** 常量时间比较，别用 === 比哈希 */
export function safeEqual(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

export const b64urlEncode = (s: string): string =>
  btoa(String.fromCharCode(...new TextEncoder().encode(s)))
    .replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

export const b64urlDecode = (s: string): string => {
  const pad = s.replace(/-/g, '+').replace(/_/g, '/');
  const raw = atob(pad + '='.repeat((4 - (pad.length % 4)) % 4));
  return new TextDecoder().decode(Uint8Array.from(raw, (c) => c.charCodeAt(0)));
};

/**
 * 一次 changelog 追加，所有写路由都用它，保证游标不漏。
 *
 * classId 是必填的：changelog 不按班隔离的话，A 班的设备会读到 B 班的变更记录，
 * 进而把 B 班的行 id 当成"我这边该拉的东西"。这类漏洞在单班试点时完全看不出来，
 * 一到两个班主任共用一套部署就露馅 —— 所以这里不给默认值，漏传就编译报错。
 */
export function changeStmt(
  db: D1Database,
  classId: string,
  entity: string,
  rowId: string,
  op: 'upsert' | 'delete',
  at = Date.now(),
): D1PreparedStatement {
  return db
    .prepare('INSERT INTO changelog (class_id, entity, row_id, op, at) VALUES (?, ?, ?, ?, ?)')
    .bind(classId, entity, rowId, op, at);
}

export function chunk<T>(items: T[], size: number): T[][] {
  const out: T[][] = [];
  for (let i = 0; i < items.length; i += size) out.push(items.slice(i, i + size));
  return out;
}
