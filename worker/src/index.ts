/**
 * 路由。这一层要薄：只做鉴权、校验、幂等写入、增量查询，不做业务计算。
 * 所有排名与折算都在客户端算 —— 这样"换云端只改 Repository"才不是一句口号。
 *
 * 十个接口，一个都不多：
 *   POST /api/v1/classes            建班（向导第 1 步，全程只调用一次）
 *   POST /api/v1/pair               班级码 + 密码 → token 对
 *   POST /api/v1/refresh            静默续期
 *   POST /api/v1/signout            退出登录（撤销设备）
 *   GET  /api/v1/sync?since=&limit= 增量拉取
 *   POST /api/v1/entries            批量入账 1–50 条
 *   POST /api/v1/entries/batch/:id/reverse  按批次撤销
 *   POST /api/v1/exams              一场考试 + 全部成绩
 *   POST|PATCH /api/v1/:entity      新增 / 修改（students / rules / members / note_templates / schemes）
 *   POST /api/v1/periods/reset      两阶段重置
 * 另加 GET /api/v1/ping 给唤醒探测用（一体机休眠后 TCP 假活，这个端点比任何离线逻辑都先写）。
 *
 * 自动更新**不在这里**：客户端直连公开版本库（CNB）的 Release 附件读清单和安装包。
 * 这一层不持有任何 CNB 凭据，也不替谁代理下载 —— 少一个能读私有库的长期令牌，
 * 就少一处要盯的泄露面。
 */
import type { Env } from './types';
import { HttpError, fail, harden, json, preflight, readJson } from './util';
import { requireAuth } from './auth';
import { ping, createClass, pair, refresh, signOut } from './routes/classes';
import { postEntries, reverseBatch } from './routes/entries';
import { putSchemeWeights, resolveEntity, upsertEntity } from './routes/entities';
import { upsertExam } from './routes/exams';
import { resetPeriod, upsertPeriod } from './routes/periods';
import { assertNotResetting, pull } from './sync';

/**
 * 入口。这里只做三件事，然后全部交给 route：
 *   1. 预检（OPTIONS）
 *   2. 密钥强度自检 —— 弱密钥等于没有密钥，不如直接拒绝服务，别让部署者以为自己配好了
 *   3. 给所有响应补安全头（harden）
 */
export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    return harden(await handle(request, env), request, env);
  },
};

/** TOKEN_SECRET 至少 32 字符。HS256 的密钥短于 32 字节，爆破成本低到没有意义。 */
const MIN_SECRET_LEN = 32;

async function handle(request: Request, env: Env): Promise<Response> {
  if (request.method === 'OPTIONS') return preflight();

  if (!env.TOKEN_SECRET || env.TOKEN_SECRET.length < MIN_SECRET_LEN) {
    // 这条 500 的 message 是故意给部署者看的，不是给攻击者看的：
    // 它只说"去配 TOKEN_SECRET"，不暴露任何服务器状态。
    return json(
      {
        error: 'weak_token_secret',
        message: `TOKEN_SECRET 没有配置或长度不足 ${MIN_SECRET_LEN}，请在服务端执行 wrangler secret put TOKEN_SECRET`,
      },
      500,
    );
  }

  try {
    return await route(request, env);
  } catch (e) {
    return fail(e);
  }
}

async function route(request: Request, env: Env): Promise<Response> {
  const url = new URL(request.url);
  const parts = url.pathname.split('/').filter(Boolean);

  if (parts[0] !== 'api' || parts[1] !== 'v1') {
    throw new HttpError(404, 'not_found', '只提供 /api/v1 下的接口');
  }
  const rest = parts.slice(2);
  const [head, second, third, fourth] = rest;
  const method = request.method.toUpperCase();

  if (!head) throw new HttpError(404, 'not_found', '缺路径');

  switch (head) {
    case 'ping':
      return ping(env, request);

    case 'classes':
      if (method === 'POST') return createClass(env, request);
      break;

    case 'pair':
      if (method === 'POST') return pair(env, request);
      break;

    case 'refresh':
      if (method === 'POST') return refresh(env, request);
      break;

    case 'signout':
      if (method === 'POST') return signOut(env, request);
      break;

    case 'sync':
      if (method === 'GET') return pull(env, request);
      break;

    case 'entries': {
      if (!second) {
        if (method !== 'POST') break;
        const claims = await requireAuth(env, request);
        await assertNotResetting(env, claims.cid);
        return postEntries(env, request);
      }
      // /entries/batch/:id/reverse
      if (second === 'batch' && third && fourth === 'reverse' && method === 'POST') {
        const claims = await requireAuth(env, request);
        await assertNotResetting(env, claims.cid);
        const body = await readJson<{ operatorId?: string; operatorName?: string }>(request);
        const operator = {
          id: typeof body.operatorId === 'string' && body.operatorId ? body.operatorId : 'unknown',
          name: typeof body.operatorName === 'string' && body.operatorName ? body.operatorName : '未知',
        };
        return reverseBatch(env, request, claims.cid, decodeURIComponent(third), operator);
      }
      break;
    }

    case 'exams':
      if (method === 'POST') {
        const claims = await requireAuth(env, request);
        await assertNotResetting(env, claims.cid);
        return upsertExam(env, request);
      }
      break;

    case 'periods':
      if (method === 'POST') {
        if (second === 'reset') return resetPeriod(env, request);
        const claims = await requireAuth(env, request);
        await assertNotResetting(env, claims.cid);
        return upsertPeriod(env, request);
      }
      break;

    default:
      break;
  }

  // 通用实体：/students、/rules、/members、/note_templates、/schemes
  const spec = head ? resolveEntity(head) : null;
  if (spec) {
    if (method === 'POST' && !second) {
      const claims = await requireAuth(env, request);
      await assertNotResetting(env, claims.cid);
      return upsertEntity(env, request, head);
    }
    if (method === 'PATCH' && second) {
      const claims = await requireAuth(env, request);
      await assertNotResetting(env, claims.cid);
      // PATCH 的 id 走路径，body 里可能没有；这里必须新建一个对象，
      // 不能把 body 自己塞回自己（会变成循环引用，JSON.stringify 直接炸）
      const body = await readJson<Record<string, unknown>>(request);
      const key = spec.entity.replace(/s$/, '');
      const nested = (body[key] ?? body[spec.entity]) as Record<string, unknown> | undefined;
      const inner: Record<string, unknown> = nested && typeof nested === 'object' ? { ...nested } : { ...body };
      inner.id = decodeURIComponent(second);
      const shaped = {
        action: (body.action as string) ?? 'upsert',
        [spec.entity]: inner,
      };
      const replay = new Request(request.url, {
        method: 'POST',
        headers: [...request.headers.entries()],
        body: JSON.stringify(shaped),
      });
      return upsertEntity(env, replay, head);
    }
    if (method === 'POST' && second && third === 'weights') {
      await requireAuth(env, request);
      return putSchemeWeights(env, request, decodeURIComponent(second));
    }
  }

  // 兜底：路径对不上就明确告诉对方，别让人对着 500 猜
  if (head === 'entries' || head === 'exams') {
    throw new HttpError(405, 'method_not_allowed', `${head} 不支持 ${method}`);
  }
  throw new HttpError(404, 'not_found', `没有这个接口：${method} ${url.pathname}`);
}
