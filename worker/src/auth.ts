/**
 * 鉴权：两种端，两种策略。
 * - 电脑端免密，靠 refresh token（90 天）静默续期。token 由客户端用 DPAPI 加密后存盘，
 *   换 Windows 用户就解不开 —— 这一层服务端不用管。
 * - 大屏端只在进管理模式时用密码，班委刷卡记分不碰密码。
 *
 * 但"免密"不等于"免身份"：配对时输的是谁的口令，签出来的就是谁的角色，
 * 之后每一次写入都按这个角色放行（见 requireManager）。客户端也有一份同样的
 * 本地身份锁，两边必须是同一套规则，否则会出现"界面上能点、同步时被拒"。
 *
 * 令牌是 HS256 的 JWT 简化版（header.payload.signature），不引库：
 * 只有本服务自己签、自己验，不需要 RS256，也不需要 JWKS。
 */
import type { Claims, Env, Role } from './types';
import { HttpError, b64urlDecode, b64urlEncode, now, randomHex, safeEqual } from './util';

const HEADER = b64urlEncode(JSON.stringify({ alg: 'HS256', typ: 'JWT' }));

async function hmacKey(secret: string): Promise<CryptoKey> {
  return crypto.subtle.importKey(
    'raw',
    new TextEncoder().encode(secret),
    { name: 'HMAC', hash: 'SHA-256' },
    false,
    ['sign', 'verify'],
  );
}

async function sign(data: string, secret: string): Promise<string> {
  const sig = await crypto.subtle.sign('HMAC', await hmacKey(secret), new TextEncoder().encode(data));
  return b64urlEncode(String.fromCharCode(...new Uint8Array(sig)));
}

export async function signToken(env: Env, claims: Omit<Claims, 'iat' | 'exp'>, ttlSec: number): Promise<string> {
  const iat = Math.floor(now() / 1000);
  const body = b64urlEncode(JSON.stringify({ ...claims, iat, exp: iat + ttlSec }));
  return `${HEADER}.${body}.${await sign(`${HEADER}.${body}`, env.TOKEN_SECRET)}`;
}

export async function verifyToken(env: Env, token: string): Promise<Claims> {
  const parts = token.split('.');
  if (parts.length !== 3) throw new HttpError(401, 'bad_token', '令牌格式不对');
  const [h, p, s] = parts;
  const expected = await sign(`${h}.${p}`, env.TOKEN_SECRET);
  if (!safeEqual(s, expected)) throw new HttpError(401, 'bad_signature', '令牌签名不对');

  let claims: Claims;
  try {
    claims = JSON.parse(b64urlDecode(p)) as Claims;
  } catch {
    throw new HttpError(401, 'bad_token', '令牌内容不是合法 JSON');
  }
  if (claims.exp * 1000 < now()) throw new HttpError(401, 'token_expired', '令牌已过期，请刷新');
  // 签名过了，内容仍然归一化一次：万一日后有人手写了一段缺 role 的 claims，
  // 这里默认落到权限最低的那一档，而不是让它变成 undefined 一路放行。
  claims.role = asRole(claims.role);
  return claims;
}

export function bearer(request: Request): string {
  const raw = request.headers.get('authorization') ?? '';
  const m = /^Bearer\s+(.+)$/i.exec(raw);
  if (!m) throw new HttpError(401, 'no_token', '缺少 Authorization: Bearer');
  return m[1].trim();
}

export async function requireAuth(env: Env, request: Request): Promise<Claims> {
  const claims = await verifyToken(env, bearer(request));
  // 平台管理员的令牌是部署级的，不属于任何一个班。放它进来等于让"能看所有班"
  // 顺带获得了某个班的写权限 —— 而中间的 cid 校验一旦有谁写漏，就是越权。
  if (claims.plat) {
    throw new HttpError(403, 'platform_token_not_allowed', '平台管理员令牌不能用于班级接口，请用班级自己的口令配对');
  }
  return claims;
}

// ---------------- 角色 ----------------

/** 认不出来的角色一律当班委：权限最低的那一档，宁可少给也不多给。 */
export function asRole(v: unknown): Role {
  return v === 'owner' || v === 'co_owner' ? v : 'officer';
}

/**
 * 能不能管这个班的东西（名单、规则、成员、周期、考试、折算方案）。
 * 班主任和班长同权 —— 和客户端 MemberRoleNames.CanManage 保持一致。
 * 班委只记分，改不了任何"定义"类的数据。
 */
export const canManage = (role: Role): boolean => role === 'owner' || role === 'co_owner';

/**
 * 只读令牌的写入闸门。
 *
 * 待审设备拿的是只读票（claims.ro），能 /sync 拉、能 /ping，但任何改动都要拦。
 * 用 403 + 独立错误码，客户端据此把界面切到只读并显示"等待班主任通过"，
 * 而不是把它当成一次神秘失败去重试。
 *
 * 注意：**这道门不记 auth_throttle**。它不是猜密码，是"还没点通过"，
 * 记了会让等待中的机器越试越被封。
 */
export function assertWritable(claims: Claims): void {
  if (claims.ro) {
    throw new HttpError(
      403,
      'read_only_device',
      '这台设备还在等班主任通过，现在只能查看。通过之后会自动恢复记分，不用重新配对。',
    );
  }
}

/**
 * 管理类接口的闸门。403 而不是 404：调用方本来就持有这个班的令牌，
 * 班级存在与否早就知道了，藏也藏不住；这里要的是让界面能说清"身份不够"。
 */
export async function requireManager(env: Env, request: Request): Promise<Claims> {
  const claims = await verifyToken(env, bearer(request));
  // 平台令牌不是班级身份：没有 cid，一旦从这里漏过去，管理员台的每个查询
  // 都在拿空班号过滤 —— 看着"能用"，实际是在一条不存在的路上走。
  if (claims.plat) {
    throw new HttpError(403, 'platform_token_not_allowed', '平台管理员令牌不能用于班级接口，请用班级自己的口令配对');
  }
  assertWritable(claims);
  if (!canManage(claims.role)) {
    throw new HttpError(403, 'forbidden_role', '当前身份是班委，只能记分；改名单请用班主任或班长的密码');
  }
  return claims;
}

/**
 * 平台管理员的闸门：只认 plat 令牌。
 *
 * 刻意不复用 requireManager —— 收藏在同一个判断里最容易出的事故是
 * "哪天有人为了让某个接口好测，把 role 抬一下就顺带放开了平台权限"。
 */
export async function requirePlatform(env: Env, request: Request): Promise<Claims> {
  const claims = await verifyToken(env, bearer(request));
  if (!claims.plat) {
    throw new HttpError(403, 'platform_required', '需要平台管理员口令');
  }
  return claims;
}

// ---------------- 口令哈希 ----------------

const enc = new TextEncoder();

/**
 * Workers 运行时对 PBKDF2 迭代数的**硬上限**。
 *
 * 这不是性能建议，是平台拒收：crypto.subtle.deriveBits 收到超过 100000 轮会直接抛
 * NotSupportedError（"iteration counts above 100000 are not supported"），任何套餐都不放宽。
 * 本地 workerd 不做这个检查 —— 于是在开发机上一路绿灯，部署之后第一下建班就 500。
 *
 * 还有一条推论：**已经存进库的、轮数超过这个上限的哈希，在 Workers 上永远验不过。**
 * 遇到这种行不要抛错（那会变成 500，看起来像整台服务器坏了），当成"口令不对"处理。
 */
export const PBKDF2_MAX_ITERATIONS = 100_000;

/** 把轮数夹进运行时允许的区间：太小等于没哈希，超过上限平台直接拒收。 */
const clampIterations = (n: number): number =>
  Math.min(PBKDF2_MAX_ITERATIONS, Math.max(1000, Math.floor(Number.isFinite(n) ? n : PBKDF2_MAX_ITERATIONS)));

export async function pbkdf2Hex(password: string, saltHex: string, iterations: number): Promise<string> {
  const key = await crypto.subtle.importKey('raw', enc.encode(password), 'PBKDF2', false, ['deriveBits']);
  const salt = Uint8Array.from(saltHex.match(/.{2}/g)!.map((x) => parseInt(x, 16)));
  const bits = await crypto.subtle.deriveBits(
    { name: 'PBKDF2', hash: 'SHA-256', salt, iterations },
    key,
    256,
  );
  return [...new Uint8Array(bits)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

export async function hashPassword(env: Env, password: string): Promise<{ hash: string; salt: string }> {
  const salt = randomHex(16);
  const iterations = clampIterations(Number(env.PBKDF2_ITERATIONS || '100000'));
  return { hash: await pbkdf2Hex(password, salt, iterations), salt };
}

export async function verifyPassword(env: Env, password: string, hash: string, salt: string): Promise<boolean> {
  const iterations = clampIterations(Number(env.PBKDF2_ITERATIONS || '100000'));
  const got = await pbkdf2Hex(password, salt, iterations);
  return safeEqual(got, hash);
}

/**
 * 校验成员口令。
 *
 * 和班主任密码不一样：成员的口令哈希是**客户端**算的，格式是
 * `pbkdf2$轮数$base64盐$base64密钥`（见 Core/Security.cs 的 PasswordHasher），
 * 轮数和盐都写在串里。好处是服务端不用另存一列盐；代价是每验一个人就要跑满
 * 一次 PBKDF2 —— 一个 45 人的班全不匹配，就是 500 万轮哈希。
 *
 * 所以调用方要限个数（见 pair 里的 PAIR_MEMBER_SCAN），并且只在班主任口令
 * 已经失配之后才走这条路：正常配对一次就命中，走不到这里。
 */
export async function verifyMemberPin(secret: string, stored: string | null): Promise<boolean> {
  if (!stored) return false;
  const parts = stored.split('$');
  if (parts.length !== 4 || parts[0] !== 'pbkdf2') return false;

  const iterations = Number(parts[1]);
  // 下界防呆：太小等于没哈希。这里是客户端给的数，不能全信。
  if (!Number.isFinite(iterations) || iterations < 1000) return false;
  // 上界是平台限制，不是防呆：超过 100000 轮 Workers 直接抛 NotSupportedError。
  // 返回 false 而不是让它抛 —— 抛出去就是 500，把"这个成员的口令验不了"
  // 表现成"整台服务器坏了"，老师完全无从下手。
  if (iterations > PBKDF2_MAX_ITERATIONS) return false;

  let salt: Uint8Array;
  let expected: Uint8Array;
  try {
    salt = base64Bytes(parts[2]);
    expected = base64Bytes(parts[3]);
  } catch {
    return false;
  }
  if (salt.length < 8 || expected.length < 16) return false;

  const key = await crypto.subtle.importKey('raw', enc.encode(secret), 'PBKDF2', false, ['deriveBits']);
  const bits = await crypto.subtle.deriveBits(
    { name: 'PBKDF2', hash: 'SHA-256', salt, iterations },
    key,
    expected.length * 8,
  );
  // 转成十六进制再比：safeEqual 是逐字符比较，字节数组直接比会退化成引用比较
  return safeEqual(hex(new Uint8Array(bits)), hex(expected));
}

const hex = (bytes: Uint8Array): string => [...bytes].map((b) => b.toString(16).padStart(2, '0')).join('');

const base64Bytes = (s: string): Uint8Array => Uint8Array.from(atob(s), (c) => c.charCodeAt(0));

const base64 = (bytes: Uint8Array): string => btoa(String.fromCharCode(...bytes));

/**
 * 按客户端同款格式生成成员口令哈希。
 *
 * 建班时服务端要顺手写一条"班主任"成员行，那行的 pin_hash 会被同步到所有设备，
 * 变成客户端校验管理密码的依据。所以这里必须用**客户端能解的格式**（PHC 串），
 * 不能用班级密码那套 hex —— 否则设备拉下来之后，班主任在本地就再也输不对密码了。
 */
export async function hashPinPhc(password: string, iterations = PBKDF2_MAX_ITERATIONS): Promise<string> {
  const iters = clampIterations(iterations);
  const salt = crypto.getRandomValues(new Uint8Array(16));
  const key = await crypto.subtle.importKey('raw', enc.encode(password), 'PBKDF2', false, ['deriveBits']);
  const bits = await crypto.subtle.deriveBits({ name: 'PBKDF2', hash: 'SHA-256', salt, iterations: iters }, key, 256);
  return `pbkdf2$${iters}$${base64(salt)}$${base64(new Uint8Array(bits))}`;
}

export function sha256Hex(input: string): Promise<string> {
  return crypto.subtle.digest('SHA-256', enc.encode(input)).then((buf) =>
    [...new Uint8Array(buf)].map((b) => b.toString(16).padStart(2, '0')).join(''),
  );
}

export const accessTtlSec = (env: Env): number => Number(env.TOKEN_TTL_HOURS || '12') * 3600;
export const refreshTtlSec = (env: Env): number => Number(env.REFRESH_TTL_DAYS || '90') * 86400;
