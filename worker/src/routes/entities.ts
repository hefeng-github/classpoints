/**
 * 通用实体写入：students / rules / members / note_templates / schemes。
 * 这几张表没有特殊逻辑，硬给每个开一个端点只会多写五套一模一样的校验。
 *
 * 并发：客户端把整条对象连同 rev 一起发上来。
 * - 离线补推（outbox 重放）走 rev 比较：excluded.rev > 库里的 rev 才覆盖，否则当作旧消息丢弃，
 *   不返回 409 —— 否则队列会卡死在一条永远推不上去的旧记录上。
 * - 人在界面上改（电脑端交互）带 If-Match: rev，严格比对，不匹配返回 409，
 *   让 UI 弹"用我的 / 用教室那台的"。
 *
 * 枚举坑：客户端用 System.Text.Json 默认序列化，C# enum 落到 JSON 里是数字，不是字符串。
 * 所以 role / cap_scope 要在这里翻译，不能指望拿到 'owner'。
 */
import type { Env } from '../types';
import { requireAuth } from '../auth';
import {
  HttpError,
  bit,
  changeStmt,
  intOrNull,
  json,
  now,
  optionalString,
  readJson,
  requireString,
} from '../util';

type Kind = 'text' | 'int' | 'real' | 'bool';

interface Col {
  /** 客户端 JSON 字段名（camelCase） */
  key: string;
  /** 数据库列名（snake_case） */
  col: string;
  kind: Kind;
  /** 缺省值（客户端没发这个字段时用） */
  dflt?: string | number | null;
  /** 值翻译，枚举、时间戳这类走这里 */
  map?: (v: unknown) => string | number | null;
}

interface Spec {
  /** 规范路径用的名字 */
  entity: string;
  table: string;
  cols: Col[];
  hasRev: boolean;
}

const str = (v: unknown): string | null => (typeof v === 'string' ? v : v == null ? null : String(v));
const num = (v: unknown): number | null => (typeof v === 'number' && Number.isFinite(v) ? v : null);

/** MemberRole: Owner=0, CoOwner=1, Officer=2 */
function roleOf(v: unknown): string {
  if (typeof v === 'string') {
    const s = v.toLowerCase();
    if (s === 'owner' || s === 'co_owner' || s === 'officer') return s;
  }
  return v === 0 ? 'owner' : v === 1 ? 'co_owner' : 'officer';
}

/** CapScope: None=0, Single=1, Period=2 */
function capScopeOf(v: unknown): string | null {
  if (typeof v === 'string') {
    const s = v.toLowerCase();
    return s === 'single' || s === 'period' ? s : null;
  }
  return v === 1 ? 'single' : v === 2 ? 'period' : null;
}

const column = (key: string, col: string, kind: Kind = 'text', extra: Partial<Col> = {}): Col => ({
  key, col, kind, ...extra,
});

const SPECS: Record<string, Spec> = {
  students: {
    entity: 'students',
    table: 'students',
    hasRev: true,
    cols: [
      column('name', 'name'),
      column('studentNo', 'student_no'),
      column('seatNo', 'seat_no', 'int'),
      column('groupName', 'group_name'),
      column('active', 'active', 'bool', { dflt: 1 }),
      column('updatedAt', 'updated_at', 'int', { dflt: 0 }),
      column('deletedAt', 'deleted_at', 'int'),
      column('createdAt', 'created_at', 'int', { dflt: 0 }),
    ],
  },
  rules: {
    entity: 'rules',
    table: 'rules',
    hasRev: true,
    cols: [
      column('label', 'label'),
      column('delta', 'delta', 'int', { dflt: 0 }),
      column('icon', 'icon', 'text', { dflt: '⭐' }),
      column('sort', 'sort', 'int', { dflt: 0 }),
      column('presetKey', 'preset_key'),
      // NULL = 全班共用；非空 = 只属于这个成员（换台设备也要保住归属）
      column('ownerId', 'owner_id'),
      column('capValue', 'cap_value', 'int'),
      column('capScope', 'cap_scope', 'text', { map: capScopeOf }),
      column('archivedAt', 'archived_at', 'int'),
      column('updatedAt', 'updated_at', 'int', { dflt: 0 }),
      column('deletedAt', 'deleted_at', 'int'),
    ],
  },
  members: {
    entity: 'members',
    table: 'members',
    hasRev: true,
    cols: [
      column('displayName', 'display_name'),
      column('role', 'role', 'text', { map: roleOf, dflt: 'officer' }),
      column('pinHash', 'pin_hash'),
      column('createdAt', 'created_at', 'int', { dflt: 0 }),
      column('revokedAt', 'revoked_at', 'int'),
      column('updatedAt', 'updated_at', 'int', { dflt: 0 }),
      column('deletedAt', 'deleted_at', 'int'),
    ],
  },
  note_templates: {
    entity: 'note_templates',
    table: 'note_templates',
    hasRev: true,
    cols: [
      column('text', 'text'),
      column('useCount', 'use_count', 'int', { dflt: 1 }),
      column('lastUsedAt', 'last_used_at', 'int', { dflt: 0 }),
      column('archivedAt', 'archived_at', 'int'),
      column('updatedAt', 'updated_at', 'int', { dflt: 0 }),
      column('deletedAt', 'deleted_at', 'int'),
    ],
  },
  counters: {
    entity: 'counters',
    table: 'counters',
    hasRev: true,
    cols: [
      column('label', 'label'),
      column('icon', 'icon', 'text', { dflt: '⚑' }),
      column('sort', 'sort', 'int', { dflt: 0 }),
      // 预设事件的去重键：班内唯一，两个班各有一份"迟到"是正常的
      column('presetKey', 'preset_key'),
      column('archivedAt', 'archived_at', 'int'),
      // 老客户端不发这两个字段，给个默认值再入库
      column('updatedAt', 'updated_at', 'int', { dflt: 0 }),
      column('deletedAt', 'deleted_at', 'int'),
    ],
  },
  schemes: {
    entity: 'schemes',
    table: 'schemes',
    hasRev: false,
    cols: [
      column('name', 'name'),
      column('examWeight', 'exam_weight', 'real', { dflt: 0.7 }),
      column('dailyWeight', 'daily_weight', 'real', { dflt: 0.3 }),
      column('normalize', 'normalize', 'bool', { dflt: 0 }),
      column('isActive', 'is_active', 'bool', { dflt: 0 }),
      column('createdAt', 'created_at', 'int', { dflt: 0 }),
    ],
  },
};

/** 客户端 outbox 的 kind 用的是单数名，这里做一次别名收敛 */
const ALIASES: Record<string, string> = {
  student: 'students', students: 'students',
  rule: 'rules', rules: 'rules',
  member: 'members', members: 'members',
  note: 'note_templates', notes: 'note_templates', note_templates: 'note_templates',
  scheme: 'schemes', schemes: 'schemes',
  counter: 'counters', counters: 'counters',
};

export function resolveEntity(raw: string): Spec | null {
  const name = ALIASES[raw];
  return name ? SPECS[name] : null;
}

function coerce(col: Col, raw: unknown): string | number | null {
  if (col.map) return col.map(raw);
  if (raw === undefined || raw === null) return col.dflt ?? null;
  switch (col.kind) {
    case 'int':
    case 'real':
      return num(raw) ?? col.dflt ?? null;
    case 'bool':
      return bit(raw);
    default:
      return str(raw) ?? col.dflt ?? null;
  }
}

/** 从客户端 payload 里取出实体对象：{action:'upsert', student:{...}} */
function pickEntity(body: Record<string, unknown>, spec: Spec): Record<string, unknown> {
  const keys = [spec.entity, spec.entity.replace(/s$/, ''), 'student', 'rule', 'member', 'note', 'scheme', 'period', 'exam'];
  for (const k of keys) {
    const v = body[k];
    if (v && typeof v === 'object') return v as Record<string, unknown>;
  }
  if (typeof body.id === 'string') return body; // 直接发裸对象也收
  throw new HttpError(422, 'no_payload', `请求体里找不到 ${spec.entity} 对象`);
}

export async function upsertEntity(env: Env, request: Request, rawEntity: string): Promise<Response> {
  const claims = await requireAuth(env, request);
  const spec = resolveEntity(rawEntity);
  if (!spec) throw new HttpError(404, 'unknown_entity', `未知实体 ${rawEntity}`);

  const body = await readJson<Record<string, unknown>>(request);
  const action = (body.action as string) ?? 'upsert';
  const data = pickEntity(body, spec);
  const id = requireString(data.id ?? body.id, 'id', 40);
  const at = now();

  // 这个 id 已经被"彻底删除"过（见 routes/purge.ts）。硬删之后行没了，
  // 离线设备手里那份旧数据再推上来就是一条干净的 INSERT —— 人/规则就这么复活了。
  // 所以墓碑要挡在所有写入之前：upsert、软删、恢复，一律不认。
  const dead = await env.DB
    .prepare('SELECT 1 AS x FROM purged WHERE class_id = ? AND entity = ? AND row_id = ?')
    .bind(claims.cid, spec.entity, id)
    .first();
  if (dead) {
    throw new HttpError(409, 'purged', '这条已经在所有设备上彻底删除了，不能再写回来');
  }

  if (action === 'delete' || action === 'restore') {
    let stmt: D1PreparedStatement;
    // 每条 UPDATE 都带 class_id：否则拿到别的班某个 id 就能把别人的记录删掉
    if (action === 'delete') {
      const deletedAt = intOrNull(body.deletedAt) ?? at;
      stmt = spec.hasRev
        ? env.DB.prepare(`UPDATE ${spec.table} SET deleted_at = ?, rev = rev + 1, updated_at = ? WHERE id = ? AND class_id = ?`)
            .bind(deletedAt, at, id, claims.cid)
        : env.DB.prepare(`UPDATE ${spec.table} SET deleted_at = ? WHERE id = ? AND class_id = ?`).bind(deletedAt, id, claims.cid);
    } else {
      stmt = spec.hasRev
        ? env.DB.prepare(`UPDATE ${spec.table} SET deleted_at = NULL, rev = rev + 1, updated_at = ? WHERE id = ? AND class_id = ?`)
            .bind(at, id, claims.cid)
        : env.DB.prepare(`UPDATE ${spec.table} SET deleted_at = NULL WHERE id = ? AND class_id = ?`).bind(id, claims.cid);
    }
    await env.DB.batch([
      stmt,
      changeStmt(env.DB, claims.cid, spec.entity, id, action === 'delete' ? 'delete' : 'upsert', at),
    ]);
    return json({ ok: true, id, action });
  }

  const incomingRev = spec.hasRev ? intOrNull(data.rev) ?? 1 : 0;
  const ifMatch = request.headers.get('if-match');

  if (spec.hasRev && ifMatch !== null) {
    const current = await env.DB.prepare(`SELECT rev FROM ${spec.table} WHERE id = ? AND class_id = ?`)
      .bind(id, claims.cid)
      .first<{ rev: number }>();
    if (current && String(current.rev) !== ifMatch.trim()) {
      const row = await env.DB.prepare(`SELECT * FROM ${spec.table} WHERE id = ? AND class_id = ?`)
        .bind(id, claims.cid)
        .first();
      // 交给客户端弹"用我的 / 用教室那台的"
      return json({ error: 'conflict', message: '这条记录刚在别处被改过', server: row }, 409);
    }
  }

  const cols = spec.cols;
  const names = ['id', 'class_id', ...cols.map((c) => c.col)];
  if (spec.hasRev) names.push('rev');
  const placeholders = names.map(() => '?').join(', ');
  const values: (string | number | null)[] = [id, claims.cid, ...cols.map((c) => coerce(c, data[c.key]))];
  if (spec.hasRev) values.push(incomingRev);

  const updates = cols.map((c) => `${c.col} = excluded.${c.col}`).join(', ');
  // rev 比较：只有更新的版本能覆盖。相等视为同一条消息重放，直接忽略。
  // class_id 也进 WHERE：主键是全局唯一 id，万一两个班撞了同一个 id，
  // 后到的那个绝不允许改掉别人的行 —— 宁可这条不落地，也不能串班。
  const guard = spec.hasRev
    ? ` WHERE excluded.rev > ${spec.table}.rev AND ${spec.table}.class_id = excluded.class_id`
    : ` WHERE ${spec.table}.class_id = excluded.class_id`;
  const sql = `INSERT INTO ${spec.table} (${names.join(', ')}) VALUES (${placeholders})
               ON CONFLICT(id) DO UPDATE SET ${updates}${spec.hasRev ? ', rev = excluded.rev' : ''}${guard}`;

  const results = await write(env, claims.cid, id, at, values, sql, cols, spec);

  // 这个 id 属于别的班：明确回 409（客户端按"永久失败"处理，从队列里移出并显示原因），
  // 而不是回 200 applied:false —— 那会让客户端以为推成功了，两边都看不出问题。
  if (results.foreign) {
    throw new HttpError(
      409,
      'foreign_row',
      '这条记录在云端属于另一个班（同一个数据只能呆在一个班），没有写进这个班',
    );
  }

  // 班主任只能有一个：老客户端（≤1.3.11）在"本机还没同步到班主任"时会自建一行
  // 「班主任」推上来，两台设备先后开机就是两个。这里按创建时间认正本，
  // 把第二个写成"已删除"（行还在、changelog 也写了，好让发起端那份跟着消失）。
  const ownerNote = await settleOwner(env, claims.cid, id, at, values, cols, spec);

  // 名单被整份推两遍：配置包/备份导入过的设备，本机名单有自己的一套 id，
  // 一配对就整份推上来，云端没有"同名同座位"的约束 —— 51 人变 102 人。
  const studentNote = await settleStudent(env, claims.cid, id, at, values, cols, spec);

  const applied = (results.meta?.changes ?? 0) > 0;
  return json({
    ok: true,
    id,
    applied,
    rev: incomingRev,
    classId: claims.cid,
    ...results.note,
    ...ownerNote,
    ...studentNote,
  });
}

/**
 * 同一个人在名单里只能有一条。
 *
 * 现场：老师按"家里配好配置包 → 到学校配对"的流程又配了两台设备。配置包里的名单
 * 是导入时新建的行（自己的 id），配对后整份推上来，云端就多出一整套同名同座位的学生；
 * 再同步回所有设备，名单页每人都出现两次，看板/统计也跟着乱。
 *
 * 判据刻意窄：**name 相同 + seat_no 相同且非空**。只靠姓名不行 —— 一个班里两个
 * 「张伟」很常见；座位号是名单自带的，同名的两个人不会坐同一个位置。
 *
 * 收法跟 settleOwner 一样：按 created_at 认正本（建班/最早那份），晚到的写成
 * "已删除"，行留着一份、changelog 写一条 delete，发起端拉一轮就把自己那份清掉。
 */
async function settleStudent(
  env: Env,
  classId: string,
  id: string,
  at: number,
  values: (string | number | null)[],
  cols: Col[],
  spec: Spec,
): Promise<Record<string, unknown>> {
  if (spec.entity !== 'students') return {};

  const nameIdx = cols.findIndex((c) => c.col === 'name');
  const seatIdx = cols.findIndex((c) => c.col === 'seat_no');
  const createdIdx = cols.findIndex((c) => c.col === 'created_at');
  if (nameIdx < 0 || seatIdx < 0) return {};

  const name = values[2 + nameIdx];
  const seat = values[2 + seatIdx];
  // 没座位号的名单（只导了姓名）不做合并：那样只能靠姓名认人，会把同名的两个人并掉
  if (typeof name !== 'string' || name.length === 0 || seat === null || seat === undefined) return {};

  const mine = createdIdx >= 0 ? Number(values[2 + createdIdx] ?? 0) : 0;
  const others =
    (
      await env.DB.prepare(
        `SELECT id, created_at FROM students
          WHERE class_id = ? AND name = ? AND seat_no = ? AND deleted_at IS NULL AND id <> ?`,
      )
        .bind(classId, name, seat, id)
        .all<{ id: string; created_at: number }>()
    ).results ?? [];
  if (others.length === 0) return {};

  const earliest = others.reduce((a, b) =>
    b.created_at < a.created_at || (b.created_at === a.created_at && b.id < a.id) ? b : a,
  );
  // created_at 缺失（0）一律按"不是正本"处理，别凭一个 0 把正主删掉
  const iAmKeeper =
    mine > 0 && (mine < earliest.created_at || (mine === earliest.created_at && id < earliest.id));

  const softDelete = (rowId: string) =>
    env.DB.prepare(
      'UPDATE students SET deleted_at = ?, rev = rev + 1, updated_at = ? WHERE id = ? AND class_id = ?',
    ).bind(at, at, rowId, classId);

  if (iAmKeeper) {
    await env.DB.batch(
      others.flatMap((o) => [softDelete(o.id), changeStmt(env.DB, classId, 'students', o.id, 'delete', at)]),
    );
    return { studentMerged: others.length };
  }

  await env.DB.batch([
    softDelete(id),
    changeStmt(env.DB, classId, 'students', id, 'delete', at),
  ]);
  return { deduped: true, useStudentId: earliest.id };
}

/**
 * 一个班只留一个班主任。
 *
 * 判据是 created_at（并列按 id）—— 建班时那行最早，兜底自建的一定更晚，
 * 所以"先建的就是正本"这条规则在现实里不会误伤。
 *
 * 两种收法：
 * - 这次推上来的不是正本：把刚写的行标成已删除（行保留，pull 端才知道要删本机那份）；
 * - 这次推上来的是正本（离线设备补推了一条更早的）：把其余 owner 一并软删，
 *   各自写 changelog，别的设备拉一轮就都只剩一个了。
 *
 * 只在实体是 members 且 role='owner' 时动手；其余实体原样返回。
 */
async function settleOwner(
  env: Env,
  classId: string,
  id: string,
  at: number,
  values: (string | number | null)[],
  cols: Col[],
  spec: Spec,
): Promise<Record<string, unknown>> {
  if (spec.entity !== 'members') return {};
  const roleIdx = cols.findIndex((c) => c.col === 'role');
  if (roleIdx < 0 || values[2 + roleIdx] !== 'owner') return {};

  const createdIdx = cols.findIndex((c) => c.col === 'created_at');
  const mine = createdIdx >= 0 ? Number(values[2 + createdIdx] ?? 0) : 0;

  const others = (
    await env.DB.prepare(
      `SELECT id, created_at FROM members
        WHERE class_id = ? AND role = 'owner' AND deleted_at IS NULL AND id <> ?`,
    )
      .bind(classId, id)
      .all<{ id: string; created_at: number }>()
  ).results ?? [];
  if (others.length === 0) return {};

  const earliest = others.reduce((a, b) =>
    b.created_at < a.created_at || (b.created_at === a.created_at && b.id < a.id) ? b : a,
  );
  // 客户端没带 createdAt（0/缺省）时一律按"不是正本"处理 —— 宁可把这条并掉，
  // 也不能凭一个 0 去把真正的班主任删掉。
  const iAmKeeper =
    mine > 0 && (mine < earliest.created_at || (mine === earliest.created_at && id < earliest.id));

  if (iAmKeeper) {
    await env.DB.batch(
      others.flatMap((o) => [
        env.DB.prepare(
          'UPDATE members SET deleted_at = ?, rev = rev + 1, updated_at = ? WHERE id = ? AND class_id = ?',
        ).bind(at, at, o.id, classId),
        changeStmt(env.DB, classId, 'members', o.id, 'delete', at),
      ]),
    );
    return { ownerMerged: others.length };
  }

  // 不是正本：把这次写进去的那行改为已删除（rev 加一，让别的设备也接受这次变更）
  await env.DB.batch([
    env.DB.prepare(
      'UPDATE members SET deleted_at = ?, rev = rev + 1, updated_at = ? WHERE id = ? AND class_id = ?',
    ).bind(at, at, id, classId),
    changeStmt(env.DB, classId, 'members', id, 'delete', at),
  ]);
  return { deduped: true, useMemberId: earliest.id };
}

/**
 * 写一行，并处理"唯一索引撞车"这一类必然会发生的事：
 *
 * - students.student_no：两台设备各自导入名单时，同一个学号会落到两个不同的 id 上。
 *   硬写就是 UNIQUE 约束错误 → 500 → 客户端把它当 5xx 无限退避重试，
 *   那条学生永远上不了云，界面上只有一句"同步失败"。
 *   处理原则是**人比学号重要**：人照样进来，学号让出来（它只是导入时的对号线索）。
 * - note_templates.text：两台机器各自攒了同一句「作业未交」。文本一样就是同一条，
 *   留着已有的那条、这次当作已处理（不能 500，也不能 422 —— 422 会让客户端把它丢掉，
 *   结果一样是"这句没上云"）。
 */
async function write(
  env: Env,
  classId: string,
  id: string,
  at: number,
  values: (string | number | null)[],
  sql: string,
  cols: Col[],
  spec: Spec,
): Promise<{ meta?: { changes?: number }; note: Record<string, unknown>; foreign?: boolean }> {
  const change = changeStmt(env.DB, classId, spec.entity, id, 'upsert', at);
  try {
    const out = await env.DB.batch([env.DB.prepare(sql).bind(...values), change]);

    // 一条都没写进去有两种情况，必须分开：
    //  ① 同一条消息重放（rev 不大于库里的），行**还在本班** —— 正常，变更记录多一条也无害；
    //  ② 这个 id 属于**另一个班**（主键全局唯一，`class_id = excluded.class_id` 的守卫挡了）。
    //     这时候变更记录已经写下了，但那行在本班根本查不到 —— 拉取端会按
    //     "要了却查不到 = 服务端删了" 去删本机的数据（学生、周期都会被删，外键一拦就整页失败）。
    //     典型来源：老师建了两次班，第一次那个班的数据还在队列里重放。
    if ((out[0].meta?.changes ?? 0) === 0) {
      const own = await env.DB.prepare(`SELECT class_id FROM ${spec.table} WHERE id = ?`)
        .bind(id)
        .first<{ class_id: string }>();
      if (own && own.class_id !== classId) {
        // 预设事件（迟到/作业未交/课堂违纪）的 id 是写死的，换过班的机器一定撞：
        // 那三个 id 早被旧班占了。这个班如果已经有同语义的一份（preset_key 相同），
        // 就告诉客户端"用那一个"，不需要 409 也不需要第二份"迟到"。
        const twin = await presetTwin(env, classId, spec, cols, values, id);
        if (twin) {
          await env.DB.prepare('DELETE FROM changelog WHERE class_id = ? AND entity = ? AND row_id = ?')
            .bind(classId, spec.entity, id)
            .run();
          return { meta: out[0].meta as { changes?: number }, note: { deduped: true, useCounterId: twin } };
        }

        await env.DB.prepare('DELETE FROM changelog WHERE class_id = ? AND entity = ? AND row_id = ?')
          .bind(classId, spec.entity, id)
          .run();
        return { meta: out[0].meta as { changes?: number }, note: {}, foreign: true };
      }
    }

    return { meta: out[0].meta as { changes?: number }, note: {} };
  } catch (err) {
    const message = String((err as { message?: string })?.message ?? err);
    if (!/UNIQUE constraint failed/i.test(message)) throw err;

    if (spec.entity === 'students') {
      const idx = cols.findIndex((c) => c.col === 'student_no');
      if (idx >= 0) {
        // values = [id, class_id, ...cols]
        const relaxed = [...values];
        relaxed[2 + idx] = null;
        const out = await env.DB.batch([env.DB.prepare(sql).bind(...relaxed), change]);
        return { meta: out[0].meta as { changes?: number }, note: { studentNoDropped: true } };
      }
    }

    if (spec.entity === 'note_templates') {
      // 文本已经有一条一模一样的：这次不写，但算它处理过了
      return { note: { deduped: true } };
    }

    // 同一个班已经有同语义的预设事件（两台机器各自换过编号，最后落到同一个 preset_key）。
    // 回 500 会让客户端无限退避，回 422 会让它把这条丢掉 —— 两种都会让"迟到"永远上不了云，
    // 而引用它的打点只能在旁边一直 unknown_counter。所以给个明确的落点：用本班那一条。
    const twin = await presetTwin(env, classId, spec, cols, values, id);
    if (twin) return { note: { deduped: true, useCounterId: twin } };

    throw err;
  }
}

/**
 * 这个班有没有"同语义"的预设行（counters.preset_key 唯一索引那条路）。
 * 学生/规则不走这里：学号让位、预设规则本来就按 preset_key 去重。
 */
async function presetTwin(
  env: Env,
  classId: string,
  spec: Spec,
  cols: Col[],
  values: (string | number | null)[],
  selfId: string,
): Promise<string | null> {
  if (spec.entity !== 'counters') return null;
  const idx = cols.findIndex((c) => c.col === 'preset_key');
  if (idx < 0) return null;
  const preset = values[2 + idx];
  if (typeof preset !== 'string' || preset.length === 0) return null;

  const twin = await env.DB
    .prepare('SELECT id FROM counters WHERE class_id = ? AND preset_key = ? AND id <> ?')
    .bind(classId, preset, selfId)
    .first<{ id: string }>();
  return twin?.id ?? null;
}

/** 各场考试的权重：一次换掉，不是逐条改 */
export async function putSchemeWeights(env: Env, request: Request, schemeId: string): Promise<Response> {
  const claims = await requireAuth(env, request);
  const body = await readJson<{ weights?: Record<string, number> | { examId: string; weight: number }[] }>(request);
  const raw = body.weights ?? {};
  const pairs: { examId: string; weight: number }[] = Array.isArray(raw)
    ? raw.map((w) => ({ examId: String(w.examId), weight: Number(w.weight) }))
    : Object.entries(raw).map(([examId, weight]) => ({ examId, weight: Number(weight) }));

  // 先确认这个折算方案属于本班，不然就是拿别人的 scheme_id 来改别人的权重
  const owned = await env.DB.prepare('SELECT 1 AS ok FROM schemes WHERE id = ? AND class_id = ?')
    .bind(schemeId, claims.cid)
    .first<{ ok: number }>();
  if (!owned) throw new HttpError(404, 'scheme_not_found', '这个折算方案不属于当前班级');

  const at = now();
  const stmts: D1PreparedStatement[] = [
    env.DB.prepare('DELETE FROM scheme_weights WHERE scheme_id = ? AND class_id = ?').bind(schemeId, claims.cid),
  ];
  for (const p of pairs) {
    stmts.push(
      env.DB.prepare('INSERT INTO scheme_weights (scheme_id, exam_id, class_id, weight) VALUES (?, ?, ?, ?)')
        .bind(schemeId, p.examId, claims.cid, p.weight),
    );
  }
  stmts.push(changeStmt(env.DB, claims.cid, 'scheme_weights', schemeId, 'upsert', at));
  await env.DB.batch(stmts);
  return json({ ok: true, count: pairs.length });
}

export { SPECS };
