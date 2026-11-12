/**
 * 防爆破与防滥用。
 *
 * 这一层解决三个真实问题：
 *   1. /pair 可以被无限次试密码 —— 班级码是 8 位、密码是 6 位数字时，离线猜是慢，
 *      在线猜却没有任何门槛。Worker 又不在本地，挡不住 IP 层的洪水。
 *   2. /classes 是唯一不需要令牌的写接口 —— 部署在公网时，任何人能往里塞班级，
 *      把 D1 的免费额度吃干净。
 *   3. 失败次数得有地方记 —— Workers 是无状态的，只能落 D1。
 *
 * 记的是 IP 的哈希，不是 IP 本身：日志和表里都不该出现"哪个地址试过密码"这种信息，
 * 而"同一个地址试了几次"用哈希一样能算出来。哈希里混了 TOKEN_SECRET，
 * 别人拿到这张表也反查不出 IP（除非他已经知道 secret，那时也没必要反查了）。
 */
import type { Env } from './types';
import { HttpError, now } from './util';

interface Rule {
  /** 窗口内允许失败几次 */
  maxFails: number;
  /** 窗口长度 */
  windowMs: number;
  /** 触发后封多久 */
  blockMs: number;
}

export const RULES: Record<string, Rule> = {
  // 一个班一小时被猜 6 次已经很不正常了；封 15 分钟，猜的人一天试不了几百次
  pair: { maxFails: 6, windowMs: 5 * 60_000, blockMs: 15 * 60_000 },
  // 建班是正常的一次性动作，一个人一小时不可能建 10 个班
  create: { maxFails: 10, windowMs: 60 * 60_000, blockMs: 60 * 60_000 },
  // refresh 失败通常是设备被撤销或换了机器，门槛放宽，只拦明显的循环重试
  refresh: { maxFails: 30, windowMs: 10 * 60_000, blockMs: 10 * 60_000 },
  // 网页管理台登录：只认班主任密码，和 /pair 同一级别，甚至更值得防
  admin: { maxFails: 6, windowMs: 5 * 60_000, blockMs: 15 * 60_000 },
};

interface Row {
  fails: number;
  window_start: number;
  blocked_until: number | null;
}

/** 客户端指纹：IP + UA 的加盐哈希，取前 24 位十六进制。 */
export async function fingerprint(request: Request, env: Env): Promise<string> {
  const ip = request.headers.get('cf-connecting-ip') ?? 'unknown';
  const ua = request.headers.get('user-agent') ?? '';
  const data = new TextEncoder().encode(`${env.TOKEN_SECRET}|${ip}|${ua}`);
  const buf = await crypto.subtle.digest('SHA-256', data);
  return [...new Uint8Array(buf)]
    .slice(0, 12)
    .map((b) => b.toString(16).padStart(2, '0'))
    .join('');
}

async function load(env: Env, scope: string, fp: string): Promise<Row | null> {
  return env.DB.prepare(
    'SELECT fails, window_start, blocked_until FROM auth_throttle WHERE scope = ? AND fp = ?',
  )
    .bind(scope, fp)
    .first<Row>();
}

/** 还在封禁期就直接 429，连密码哈希都不用算 —— 这也顺便省了 CPU 时间。 */
export async function assertNotBlocked(env: Env, scope: string, fp: string): Promise<void> {
  const row = await load(env, scope, fp);
  if (!row) return;
  const t = now();
  const rule = RULES[scope];
  if (rule && t - row.window_start > rule.windowMs && !(row.blocked_until && row.blocked_until > t)) {
    // 过期行顺手清掉，省掉一个定时任务；这张表平时只有个位数行
    await env.DB.prepare('DELETE FROM auth_throttle WHERE scope = ? AND fp = ?').bind(scope, fp).run();
    return;
  }
  if (row.blocked_until && row.blocked_until > t) {
    const sec = Math.ceil((row.blocked_until - t) / 1000);
    throw new HttpError(429, 'too_many_attempts', `尝试次数太多，请 ${Math.ceil(sec / 60)} 分钟后再试`);
  }
}

/** 记一次（失败或调用）。达到阈值就进入封禁。 */
export async function record(env: Env, scope: string, fp: string): Promise<void> {
  const rule = RULES[scope] ?? { maxFails: 20, windowMs: 10 * 60_000, blockMs: 10 * 60_000 };
  const t = now();
  const row = await load(env, scope, fp);

  if (!row || t - row.window_start > rule.windowMs) {
    await env.DB.prepare(
      `INSERT INTO auth_throttle (scope, fp, fails, window_start, blocked_until) VALUES (?, ?, 1, ?, NULL)
       ON CONFLICT(scope, fp) DO UPDATE SET fails = 1, window_start = excluded.window_start, blocked_until = NULL`,
    )
      .bind(scope, fp, t)
      .run();
    return;
  }

  const fails = row.fails + 1;
  const blocked = fails >= rule.maxFails ? t + rule.blockMs : null;
  await env.DB.prepare('UPDATE auth_throttle SET fails = ?, blocked_until = ? WHERE scope = ? AND fp = ?')
    .bind(fails, blocked, scope, fp)
    .run();
}

/** 成功了就把计数清掉 —— 正常用的人不该被自己的历史失败连累。 */
export async function clear(env: Env, scope: string, fp: string): Promise<void> {
  await env.DB.prepare('DELETE FROM auth_throttle WHERE scope = ? AND fp = ?').bind(scope, fp).run();
}
