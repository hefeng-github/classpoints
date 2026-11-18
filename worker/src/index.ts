/**
 * 路由。这一层要薄：只做鉴权、校验、幂等写入、增量查询，不做业务计算。
 * 所有排名与折算都在客户端算 —— 这样"换云端只改 Repository"才不是一句口号。
 *
 * 十个业务接口，一个都不多：
 *   POST /api/v1/classes            建班（向导第 1 步，全程只调用一次）
 *   POST /api/v1/pair               班级码 + 口令 → token 对（角色跟着口令走）
 *   POST /api/v1/refresh            静默续期
 *   POST /api/v1/signout            退出登录（撤销设备）
 *   GET  /api/v1/sync?since=&limit= 增量拉取
 *   POST /api/v1/entries            批量入账 1–50 条
 *   POST /api/v1/counter_ticks      批量写事件打点 1–100 条（只追加，幂等）
 *   POST /api/v1/entries/batch/:id/reverse  按批次撤销
 *   POST /api/v1/exams              一场考试 + 全部成绩
 *   POST|PATCH /api/v1/:entity      新增 / 修改（students / rules / members / note_templates / schemes）
 *   POST /api/v1/periods/reset      两阶段重置
 *   POST /api/v1/purge              彻底删除（归档项/回收站项，硬删 + 墓碑）
 * 另加 GET /api/v1/ping 给唤醒探测用（一体机休眠后 TCP 假活，这个端点比任何离线逻辑都先写）。
 *
 * 管理台是第二摊，和上面十个不是一类东西：它服务的是"人"，不是客户端。
 *   GET   /admin                    网页管理台（HTML，零依赖）
 *   POST  /api/v1/admin/session     只认班主任密码 / 恢复码
 *   GET   /api/v1/admin/devices     待审 / 已通过 / 已拒绝
 *   POST  /api/v1/admin/devices/:id/approve|reject|revoke
 *   POST  /api/v1/admin/devices     手动预置一台
 *   DELETE /api/v1/admin/devices/:id
 *   GET   /api/v1/admin/overview    班级总览（人数、流水、周期、考试、设备、留存）
 *   PATCH /api/v1/admin/class       改云端显示名
 *   POST  /api/v1/admin/class/trim  手动清理更早的周期
 *   POST  /api/v1/admin/class/close 注销这个班（软删 + 撤销全部设备，要重输班级码）
 *   PATCH /api/v1/admin/settings    切 join_mode
 * 它存在的原因见 routes/grants.ts 开头：口令会传开，机器要单独放行。
 *
 * 权限分三档（见 auth.ts 的 assertWritable / requireManager / canManage）：
 *   - 待审设备（只读票，claims.ro）：读（/sync /ping），别的全 403 read_only_device
 *   - 任意已配对身份：读（/sync /ping）、记分（/entries）、撤销（/entries/batch/:id/reverse）
 *   - 班主任与班长：其余全部写入（名单、规则、成员、周期、考试、折算方案）
 * 撤销之所以不下放给"仅管理"，是因为本地规则允许班委在 5 分钟内撤掉自己刚记的那批；
 * 云端要是比本地严，就会出现"界面上撤了、同步时被拒"，账对不上。
 *
 * 自动更新**不在这里**：客户端直连公开版本库（CNB）的 Release 附件读清单和安装包。
 * 这一层不持有任何 CNB 凭据，也不替谁代理下载 —— 少一个能读私有库的长期令牌，
 * 就少一处要盯的泄露面。
 */
import type { Env } from './types';
import { HttpError, fail, harden, json, preflight, readJson } from './util';
import { assertWritable, requireAuth, requireManager } from './auth';
import { ADMIN_HTML } from './admin';
import { ping, adminSession, createClass, pair, refresh, signOut } from './routes/classes';
import { postEntries, reverseBatch } from './routes/entries';
import { postTicks } from './routes/counters';
import { putSchemeWeights, resolveEntity, upsertEntity } from './routes/entities';
import { upsertExam } from './routes/exams';
import { resetPeriod, upsertPeriod } from './routes/periods';
import { addGrant, decideGrant, listGrants, removeGrant, setJoinMode } from './routes/grants';
import { classOverview, closeClass, renameClass, trimClass } from './routes/overview';
import { assertNotResetting, pull } from './sync';
import { purgeEntity } from './routes/purge';

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

  // 网页管理台。它不是 JSON 接口，所以走在自己这一支上，前面那套 /api/v1 校验管不到它。
  if (parts.length === 0 || (parts[0] === 'admin' && parts.length === 1)) {
    if (request.method !== 'GET' && request.method !== 'HEAD') {
      throw new HttpError(405, 'method_not_allowed', '管理台只支持 GET');
    }
    return new Response(ADMIN_HTML, {
      headers: {
        'content-type': 'text/html; charset=utf-8',
        // 页面自己会写这行 CSP（harden 见已有值就不覆盖）：
        // 只允许内联脚本和样式，零外部源 —— 页面里握着的 token 没有外带通道。
        'content-security-policy':
          "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
        'cache-control': 'no-store',
      },
    });
  }

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

    case 'admin':
      return adminRoute(env, request, rest.slice(1));

    case 'sync':
      if (method === 'GET') return pull(env, request);
      break;

    case 'entries': {
      if (!second) {
        if (method !== 'POST') break;
        const claims = await requireAuth(env, request);
        // 只读票（待审设备）能拉不能写：记分、撤销都要拦在入库之前
        assertWritable(claims);
        await assertNotResetting(env, claims.cid);
        return postEntries(env, request);
      }
      // /entries/batch/:id/reverse
      if (second === 'batch' && third && fourth === 'reverse' && method === 'POST') {
        const claims = await requireAuth(env, request);
        assertWritable(claims);
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

    // 彻底删除：比软删重，门槛和"改这条规则"一致（管理态）
    case 'purge': {
      if (!second && method === 'POST') {
        const claims = await requireManager(env, request);
        await assertNotResetting(env, claims.cid);
        return purgeEntity(env, request, claims.cid);
      }
      break;
    }

    // 事件打点：只追加，写门槛和记分一样（能写就能记一次）
    case 'counter_ticks': {
      if (!second && method === 'POST') {
        const claims = await requireAuth(env, request);
        assertWritable(claims);
        await assertNotResetting(env, claims.cid);
        return postTicks(env, request);
      }
      break;
    }

    case 'exams':
      if (method === 'POST') {
        const claims = await requireManager(env, request);
        await assertNotResetting(env, claims.cid);
        return upsertExam(env, request);
      }
      break;

    case 'periods':
      if (method === 'POST') {
        // 重置周期会把整班流水归档，是这里最重的一个动作 —— 只给班主任和班长
        if (second === 'reset') {
          await requireManager(env, request);
          return resetPeriod(env, request);
        }
        const claims = await requireManager(env, request);
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
      const claims = await requireManager(env, request);
      await assertNotResetting(env, claims.cid);
      return upsertEntity(env, request, head);
    }
    if (method === 'PATCH' && second) {
      const claims = await requireManager(env, request);
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
      await requireManager(env, request);
      return putSchemeWeights(env, request, decodeURIComponent(second));
    }
  }

  // 兜底：路径对不上就明确告诉对方，别让人对着 500 猜
  if (head === 'entries' || head === 'exams') {
    throw new HttpError(405, 'method_not_allowed', `${head} 不支持 ${method}`);
  }
  throw new HttpError(404, 'not_found', `没有这个接口：${method} ${url.pathname}`);
}

/**
 * 管理台接口。除了登录，全部要 requireManager —— 也就是说班长也能看，
 * 但只有班主任的密码能登进网页（见 adminSession），所以实际上还是班主任一个人。
 * 留 requireManager 而不是 requireOwner 是为了以后真要下放时不用改这里。
 */
async function adminRoute(env: Env, request: Request, rest: string[]): Promise<Response> {
  const [head, id, action] = rest;
  const method = request.method.toUpperCase();

  if (head === 'session' && !id && method === 'POST') return adminSession(env, request);

  if (head === 'devices') {
    if (!id) {
      const claims = await requireManager(env, request);
      if (method === 'GET') return listGrants(env, claims.cid);
      if (method === 'POST') return addGrant(env, request, claims.cid, claims.mid ?? claims.sid);
      throw new HttpError(405, 'method_not_allowed', `devices 不支持 ${method}`);
    }
    const claims = await requireManager(env, request);
    const key = decodeURIComponent(id);
    if (!action && method === 'DELETE') return removeGrant(env, claims.cid, key);
    if (action && method === 'POST') {
      if (action === 'approve' || action === 'reject' || action === 'revoke') {
        return decideGrant(env, claims.cid, key, action, claims.mid ?? claims.sid);
      }
      throw new HttpError(404, 'not_found', `没有这个动作：${action}`);
    }
    throw new HttpError(405, 'method_not_allowed', `${method} 用不到这个路径`);
  }

  if (head === 'overview' && !id) {
    const claims = await requireManager(env, request);
    if (method === 'GET') return classOverview(env, claims.cid);
    throw new HttpError(405, 'method_not_allowed', `overview 不支持 ${method}`);
  }

  // /class 与 /class/trim、/class/close 共用首段，所以看第二段（id）而不是 action
  if (head === 'class') {
    const claims = await requireManager(env, request);
    if (!id && method === 'PATCH') return renameClass(env, request, claims.cid);
    if (id === 'trim' && !action && method === 'POST') return trimClass(env, claims.cid);
    if (id === 'close' && !action && method === 'POST') return closeClass(env, request, claims.cid);
    throw new HttpError(404, 'not_found', `管理台没有这个动作：${method} /class/${id ?? ''}`);
  }

  if (head === 'settings' && !id) {
    const claims = await requireManager(env, request);
    if (method === 'PATCH') return setJoinMode(env, request, claims.cid);
    throw new HttpError(405, 'method_not_allowed', `settings 不支持 ${method}`);
  }

  throw new HttpError(404, 'not_found', `管理台没有这个接口：${method} /${rest.join('/')}`);
}
