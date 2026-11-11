/**
 * 冒烟测试：把十个接口按真实使用顺序跑一遍。
 * 运行前先起服务：npx wrangler dev --port 8787 --local
 * 然后：node test/smoke.mjs
 *
 * 这不是单元测试，是"接口契约还算不算数"的回归：
 * 客户端 C# 那边的 payload 形状一改，这里就该红。
 */
import { randomUUID, randomBytes, pbkdf2Sync } from 'node:crypto';

const BASE = process.env.BASE_URL ?? 'http://127.0.0.1:8787/api/v1';

let pass = 0;
const failures = [];

function check(name, ok, detail = '') {
  if (ok) {
    pass++;
    console.log(`  ✓ ${name}`);
  } else {
    failures.push(`${name}${detail ? ' — ' + detail : ''}`);
    console.log(`  ✗ ${name}${detail ? ' — ' + detail : ''}`);
  }
}

async function call(method, path, { body, token, headers = {} } = {}) {
  const res = await fetch(BASE + path, {
    method,
    headers: {
      ...(body ? { 'content-type': 'application/json' } : {}),
      ...(token ? { authorization: `Bearer ${token}` } : {}),
      ...headers,
    },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  let data = null;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    data = text;
  }
  return { status: res.status, data, headers: res.headers };
}

const now = Date.now();
const uuid = () => randomUUID();

console.log('\n1) 建班 + 配对');
const created = await call('POST', '/classes', {
  body: { name: '高二 (3) 班', ownerName: '张老师', ownerPin: '123456', recoveryCode: 'REC-8888', kind: 'board' },
});
check('POST /classes 返回 201', created.status === 201, `status=${created.status} ${JSON.stringify(created.data)}`);
const code = created.data?.code;
let access = created.data?.access;
let refresh = created.data?.refresh;
check('拿到班级码', typeof code === 'string' && code.length === 8, code);
check('拿到 access/refresh', !!access && !!refresh);

const paired = await call('POST', '/pair', { body: { code, secret: '123456', kind: 'desk', deviceLabel: '办公室' } });
check('POST /pair 成功', paired.status === 200, `status=${paired.status}`);
check('配对照旧拿到 token', !!paired.data?.access);

const badPair = await call('POST', '/pair', { body: { code, secret: '000000', kind: 'desk' } });
check('密码错了返回 401', badPair.status === 401, `status=${badPair.status}`);

const recPair = await call('POST', '/pair', { body: { code, secret: 'REC-8888', kind: 'desk' } });
check('恢复码也能配对', recPair.status === 200, `status=${recPair.status}`);

const refreshed = await call('POST', '/refresh', { body: { refresh } });
check('POST /refresh 续期成功', refreshed.status === 200 && !!refreshed.data?.access);
check('refresh token 换了一个（rotate）', refreshed.data?.refresh !== refresh);
const staleRefresh = await call('POST', '/refresh', { body: { refresh } });
check('旧 refresh 立刻失效', staleRefresh.status === 401, `status=${staleRefresh.status}`);
access = refreshed.data?.access;

console.log('\n2) 先灌名单和周期（同步的前置条件）');
const periodId = uuid();
const p1 = await call('POST', '/periods', {
  token: access,
  body: { period: { id: periodId, name: '2026 秋季学期', termKey: '2026-AUT', startAt: now, status: 'active', createdAt: now } },
});
check('POST /periods 建周期', p1.status === 200, `status=${p1.status} ${JSON.stringify(p1.data)}`);

const studentIds = [];
for (const [i, name] of ['杨欣怡', '陈芷若', '刘书豪'].entries()) {
  const id = uuid();
  studentIds.push(id);
  const r = await call('POST', '/students', {
    token: access,
    body: { action: 'upsert', student: { id, name, seatNo: i + 1, active: true, rev: 1, createdAt: now, updatedAt: now } },
  });
  check(`POST /students 第 ${i + 1} 个`, r.status === 200 && r.data?.applied === true, JSON.stringify(r.data));
}

console.log('\n3) 批量入账 + 幂等');
const batchId = uuid();
const entries = studentIds.map((sid) => ({
  id: uuid(),
  batchId,
  periodId,
  studentId: sid,
  ruleId: 'r1',
  ruleLabel: '回答问题',
  ruleDeltaDefault: 5,
  delta: 5,
  source: 'rule',
  operatorId: 'm1',
  operatorName: '张老师',
  identifyMethod: 'nfc',
  occurredAt: now,
  createdAt: now,
}));
const posted = await call('POST', '/entries', { token: access, body: { entries } });
check('POST /entries 整批入库', posted.status === 200 && posted.data?.inserted === 3, JSON.stringify(posted.data));

const replayed = await call('POST', '/entries', { token: access, body: { entries } });
check('同一批重传不重复计分', replayed.data?.inserted === 0 && replayed.data?.duplicates === 3, JSON.stringify(replayed.data));

const tooMany = await call('POST', '/entries', {
  token: access,
  body: { entries: Array.from({ length: 51 }, () => ({ ...entries[0], id: uuid() })) },
});
check('超过 50 条被拒绝', tooMany.status === 422, `status=${tooMany.status}`);

const noAuth = await call('POST', '/entries', { body: { entries } });
check('没令牌写入被拒', noAuth.status === 401, `status=${noAuth.status}`);

console.log('\n4) 拉取');
const pulled = await call('GET', '/sync?since=0&limit=200', { token: access });
check('GET /sync 有游标', typeof pulled.data?.next === 'number' && pulled.data.next > 0, `next=${pulled.data?.next}`);
// 本地 D1 是持久的，多次跑会累积；只数本次造出来的那几条
const mineStudents = (pulled.data?.students ?? []).filter((s) => studentIds.includes(s.id)).length;
// /sync 返回的是库里的原始列名（snake_case），和本地 SQLite 列名一一对应
const mineEntries = (pulled.data?.entries ?? []).filter((e) => e.batch_id === batchId).length;
check('拉回本次的 3 个学生', mineStudents === 3, `got=${mineStudents}`);
check('拉回本次的 3 条流水', mineEntries === 3, `got=${mineEntries}`);
check('拉回本次的周期', (pulled.data?.periods ?? []).some((p) => p.id === periodId));

const again = await call('GET', `/sync?since=${pulled.data.next}`, { token: access });
check('用 next 再拉一次是空的', (again.data?.entries?.length ?? 1) === 0 && again.data?.hasMore === false);

console.log('\n5) 撤销');
const rev = await call('POST', `/entries/batch/${batchId}/reverse`, {
  token: access,
  body: { operatorId: 'm1', operatorName: '张老师' },
});
check('按批次撤销 3 条', rev.status === 200 && rev.data?.reversed === 3, JSON.stringify(rev.data));
const revAgain = await call('POST', `/entries/batch/${batchId}/reverse`, {
  token: access,
  body: { operatorId: 'm1', operatorName: '张老师' },
});
check('同一批不能再撤一次', revAgain.status === 404, `status=${revAgain.status}`);

const afterRev = await call('GET', `/sync?since=${pulled.data.next}`, { token: access });
// 只数本批：本地 D1 会累积多次跑的数据，全局求和没有意义
const sum = (afterRev.data?.entries ?? []).filter((e) => e.batch_id === batchId).reduce((a, e) => a + e.delta, 0);
check('冲销行把总分抵回 0', sum === 0, `sum=${sum}`);

console.log('\n6) 乐观锁');
const stuId = studentIds[0];
const cur = await call('GET', `/sync?since=0&limit=500`, { token: access });
const mineStudent = (cur.data?.students ?? []).find((s) => s.id === stuId) ?? (pulled.data?.students ?? []).find((s) => s.id === stuId);
const serverRev = mineStudent?.rev ?? 1;
const okPatch = await call('PATCH', `/students/${stuId}`, {
  token: access,
  headers: { 'if-match': String(serverRev) },
  body: { name: '杨欣怡', rev: serverRev + 1, updatedAt: now },
});
check('If-Match 对得上就放行', okPatch.status === 200, `status=${okPatch.status}`);
const stale = await call('PATCH', `/students/${stuId}`, {
  token: access,
  headers: { 'if-match': String(serverRev) },
  body: { name: '杨筱铭', rev: serverRev + 1, updatedAt: now },
});
check('If-Match 过期返回 409', stale.status === 409, `status=${stale.status}`);

const staleUpsert = await call('POST', '/students', {
  token: access,
  body: { action: 'upsert', student: { id: stuId, name: '旧名字', rev: 1, updatedAt: now } },
});
check('离线补推的旧 rev 被静默丢弃', staleUpsert.status === 200 && staleUpsert.data?.applied === false, JSON.stringify(staleUpsert.data));

console.log('\n7) 考试与成绩');
const examId = uuid();
const examUp = await call('POST', '/exams', {
  token: access,
  body: {
    action: 'upsert',
    exam: { id: examId, periodId, name: '期中考试', fullScore: 750, examDate: '2026-11-01', importedAt: now },
    scores: [
      { studentId: studentIds[0], score: 712, absent: false },
      { studentId: studentIds[1], score: 688, absent: false },
      { studentId: studentIds[2], score: null, absent: true },
    ],
  },
});
check('POST /exams 整批落库', examUp.status === 200 && examUp.data?.scores === 3, JSON.stringify(examUp.data));

const examPull = await call('GET', `/sync?since=${afterRev.data.next}`, { token: access });
check('拉回本次的考试', (examPull.data?.exams ?? []).some((e) => e.id === examId));
const mineScores = (examPull.data?.exam_scores ?? []).filter((s) => s.exam_id === examId).length;
check('拉回本次的 3 条成绩', mineScores === 3, `got=${mineScores}`);

console.log('\n8) 重置（两阶段）');
const noToken = await call('POST', '/periods/reset', { token: access, body: { phase: 'commit', period: { id: uuid(), name: '11 月', termKey: '2026-AUT' } } });
check('没令牌提交被拒', noToken.status >= 400 && noToken.status < 500, `status=${noToken.status}`);

const req = await call('POST', '/periods/reset', {
  token: access,
  body: { phase: 'request', period: { name: '11 月', termKey: '2026-AUT' } },
});
check('申请到重置令牌', req.status === 200 && typeof req.data?.token === 'string');

const blocked = await call('POST', '/entries', { token: access, body: { entries: [{ ...entries[0], id: uuid() }] } });
check('重置期间普通写入被拦 (423)', blocked.status === 423, `status=${blocked.status}`);

const newPeriodId = uuid();
const committed = await call('POST', '/periods/reset', {
  token: access,
  body: { phase: 'commit', token: req.data.token, period: { id: newPeriodId, name: '11 月', termKey: '2026-AUT', startAt: now } },
});
check('提交成功', committed.status === 200 && committed.data?.periodId === newPeriodId, JSON.stringify(committed.data));

const writeAfter = await call('POST', '/entries', { token: access, body: { entries: [{ ...entries[0], id: uuid(), periodId: newPeriodId }] } });
check('提交后写入恢复', writeAfter.status === 200 && writeAfter.data?.inserted === 1, JSON.stringify(writeAfter.data));

const periodPull = await call('GET', '/sync?since=0&limit=500', { token: access });
const activeCount = (periodPull.data?.periods ?? []).filter((p) => p.status === 'active').length;
check('同时只有一个 active 周期', activeCount === 1, `active=${activeCount}`);

console.log('\n9) 多班隔离（同一套 D1 服务两个班，谁也不许看见谁）');
const clsB = await call('POST', '/classes', {
  body: { name: '高三 (1) 班', ownerName: '李老师', ownerPin: '654321', kind: 'desk' },
});
check('第二个班也能建（班级码不撞）', clsB.status === 201 && clsB.data?.code !== code, `status=${clsB.status}`);
const accessB = clsB.data?.access;

// A 班这条学号 '01' 是班内唯一的；B 班再用 '01' 必须也能成功（唯一索引按班隔离）
const aNo01 = await call('POST', '/students', {
  token: access,
  body: { action: 'upsert', student: { id: uuid(), name: '学号一班', studentNo: '01', active: true, rev: 1, createdAt: now, updatedAt: now } },
});
check('A 班先占住学号 01', aNo01.status === 200 && aNo01.data?.applied === true, JSON.stringify(aNo01.data));
const bNo01 = await call('POST', '/students', {
  token: accessB,
  body: { action: 'upsert', student: { id: uuid(), name: '学号二班', studentNo: '01', active: true, rev: 1, createdAt: now, updatedAt: now } },
});
check('B 班用同一个学号 01 不被拦', bNo01.status === 200 && bNo01.data?.applied === true, JSON.stringify(bNo01.data));

const pullB = await call('GET', '/sync?since=0&limit=500', { token: accessB });
check('B 班拉不到 A 班的学生', (pullB.data?.students ?? []).every((s) => !studentIds.includes(s.id)), `got=${(pullB.data?.students ?? []).length} 行`);
check('B 班拉不到 A 班的流水', (pullB.data?.entries ?? []).every((e) => e.batch_id !== batchId), `got=${(pullB.data?.entries ?? []).length} 行`);
check('B 班拉不到 A 班的周期', (pullB.data?.periods ?? []).every((p) => p.id !== periodId && p.id !== newPeriodId));
check('B 班拉不到 A 班的考试', (pullB.data?.exams ?? []).every((e) => e.id !== examId));
check('B 班只看到自己的那一个成员', (pullB.data?.members ?? []).length === 1, `got=${(pullB.data?.members ?? []).length}`);

// 拿到 A 班某个真实 id 也不能改/删别人的行
const crossDelete = await call('POST', '/students', {
  token: accessB,
  body: { action: 'delete', student: { id: studentIds[1] } },
});
check('B 班拿 A 班 id 删除不报错但也不生效', crossDelete.status === 200);
const crossHijack = await call('POST', '/students', {
  token: accessB,
  body: { action: 'upsert', student: { id: studentIds[1], name: '被串改的名字', rev: 99, createdAt: now, updatedAt: now } },
});
check('B 班抢同一个主键 id 被 guard 挡下', crossHijack.status === 200 && crossHijack.data?.applied === false, JSON.stringify(crossHijack.data));

const checkA = await call('GET', '/sync?since=0&limit=500', { token: access });
const victim = (checkA.data?.students ?? []).find((s) => s.id === studentIds[1]);
check('A 班那个学生没被删（deleted_at 仍为空）', !!victim && victim.deleted_at === null, JSON.stringify(victim));
check('A 班那个学生名字没被改掉', victim?.name !== '被串改的名字', victim?.name);

const crossReverse = await call('POST', `/entries/batch/${batchId}/reverse`, {
  token: accessB,
  body: { operatorId: 'm2', operatorName: '李老师' },
});
check('B 班撤不到 A 班的批次（404）', crossReverse.status === 404, `status=${crossReverse.status}`);

// 折算方案权重同样要按班校验
const schemeA = uuid();
const schemeUp = await call('POST', '/schemes', {
  token: access,
  body: { action: 'upsert', scheme: { id: schemeA, name: '默认 7:3', examWeight: 0.7, dailyWeight: 0.3, isActive: true, createdAt: now, rev: 1 } },
});
check('A 班建折算方案', schemeUp.status === 200 && schemeUp.data?.applied === true, JSON.stringify(schemeUp.data));
const crossWeights = await call('POST', `/schemes/${schemeA}/weights`, {
  token: accessB,
  body: { weights: [{ examId, weight: 1 }] },
});
check('B 班改不到 A 班方案的权重（404）', crossWeights.status === 404, `status=${crossWeights.status}`);

const periodB = uuid();
const pB = await call('POST', '/periods', {
  token: accessB,
  body: { period: { id: periodB, name: '2026 秋季学期', termKey: '2026-AUT', startAt: now, status: 'active', createdAt: now } },
});
check('B 班能建自己的 active 周期', pB.status === 200, `status=${pB.status} ${JSON.stringify(pB.data)}`);
const stillActiveA = await call('GET', '/sync?since=0&limit=500', { token: access });
const activeA = (stillActiveA.data?.periods ?? []).filter((p) => p.status === 'active');
check('A 班的 active 周期不受 B 班影响', activeA.length === 1 && activeA[0].id === newPeriodId, JSON.stringify(activeA.map((p) => p.id)));

console.log('\n10) 角色鉴权（输谁的口令就是谁）');

/** 从 JWT 里读 claims，确认服务端签出来的角色真的是那个角色 */
const claimsOf = (token) => {
  const p = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
  return JSON.parse(Buffer.from(p, 'base64').toString('utf8'));
};

/** 客户端 PasswordHasher 的同款格式：pbkdf2$轮数$base64盐$base64密钥 */
const pinHash = (pin, iterations = 120000) => {
  const salt = randomBytes(16);
  const key = pbkdf2Sync(pin, salt, iterations, 32, 'sha256');
  return `pbkdf2$${iterations}$${salt.toString('base64')}$${key.toString('base64')}`;
};

// 单独开一个班：角色用例必须和前面的数据隔开，否则 403 会把别的用例带崩
const roleClass = await call('POST', '/classes', {
  body: { name: '角色测试班', ownerName: '李老师', ownerPin: '135790', kind: 'desk' },
});
check('建班返回 201', roleClass.status === 201, `status=${roleClass.status} ${JSON.stringify(roleClass.data)}`);
const roleCode = roleClass.data?.code;
const ownerAccess = roleClass.data?.access;
check('建班令牌带 role=owner', claimsOf(ownerAccess ?? '').role === 'owner', claimsOf(ownerAccess ?? '').role);

const monitorId = uuid();
const officerId = uuid();
const mkMember = await call('POST', '/members', {
  token: ownerAccess,
  body: {
    action: 'upsert',
    member: { id: monitorId, displayName: '班长小林', role: 1, pinHash: pinHash('246810'), rev: 1, createdAt: now, updatedAt: now },
  },
});
check('建班长成员', mkMember.status === 200, `status=${mkMember.status} ${JSON.stringify(mkMember.data)}`);
const mkOfficer = await call('POST', '/members', {
  token: ownerAccess,
  body: {
    action: 'upsert',
    member: { id: officerId, displayName: '班委小周', role: 2, pinHash: pinHash('112233'), rev: 1, createdAt: now, updatedAt: now },
  },
});
check('建班委成员', mkOfficer.status === 200, `status=${mkOfficer.status}`);

// 班长拿自己的口令配对：以前这里会签成 owner，现在必须是 co_owner
const monitorPair = await call('POST', '/pair', { body: { code: roleCode, secret: '246810', kind: 'desk' } });
check('班长口令能配对', monitorPair.status === 200, `status=${monitorPair.status} ${JSON.stringify(monitorPair.data)}`);
check('配对结果 role=co_owner', monitorPair.data?.role === 'co_owner', monitorPair.data?.role);
const monitorAccess = monitorPair.data?.access;
const monitorRefresh = monitorPair.data?.refresh;
check('班长令牌 claims 里也是 co_owner', claimsOf(monitorAccess ?? '').role === 'co_owner');

// 班主任与班长同权：班长能做所有管理动作
const rolePeriodId = uuid();
const monitorPeriod = await call('POST', '/periods', {
  token: monitorAccess,
  body: { period: { id: rolePeriodId, name: '第 1 周期', termKey: 'P1', startAt: now, status: 'active', createdAt: now } },
});
check('班长能建周期（与班主任同权）', monitorPeriod.status === 200, `status=${monitorPeriod.status}`);
const roleStudentId = uuid();
const monitorStudent = await call('POST', '/students', {
  token: monitorAccess,
  body: { action: 'upsert', student: { id: roleStudentId, name: '班长加的人', seatNo: 9, active: true, rev: 1, createdAt: now, updatedAt: now } },
});
check('班长能改名单', monitorStudent.status === 200 && monitorStudent.data?.applied === true, JSON.stringify(monitorStudent.data));

// 班委拿自己的口令配对 → officer
const officerPair = await call('POST', '/pair', { body: { code: roleCode, secret: '112233', kind: 'board' } });
check('班委口令能配对', officerPair.status === 200, `status=${officerPair.status}`);
check('配对结果 role=officer', officerPair.data?.role === 'officer', officerPair.data?.role);
const officerAccess = officerPair.data?.access;
let officerRefresh = officerPair.data?.refresh;

// 班委碰定义类数据：一律 403，而且错误码要能认出来
const deniedCases = [
  ['POST /students', () => call('POST', '/students', { token: officerAccess, body: { action: 'upsert', student: { id: uuid(), name: '不该成功', rev: 1 } } })],
  ['POST /members', () => call('POST', '/members', { token: officerAccess, body: { action: 'upsert', member: { id: uuid(), displayName: 'x', role: 2, rev: 1 } } })],
  ['POST /periods', () => call('POST', '/periods', { token: officerAccess, body: { period: { id: uuid(), name: 'x', termKey: 'x' } } })],
  ['POST /periods/reset', () => call('POST', '/periods/reset', { token: officerAccess, body: { phase: 'request' } })],
  ['POST /exams', () => call('POST', '/exams', { token: officerAccess, body: { exam: { id: uuid(), name: 'x' } } })],
  ['PATCH /students/:id', () => call('PATCH', `/students/${uuid()}`, { token: officerAccess, body: { name: 'x' } })],
];
for (const [name, run] of deniedCases) {
  const r = await run();
  check(`班委被拒：${name}`, r.status === 403 && r.data?.error === 'forbidden_role', `status=${r.status} ${JSON.stringify(r.data)}`);
}

// 但记分和读数据必须照旧 —— 班委的全部工作就靠这两件
const scoreBatch = uuid();
const officerScore = await call('POST', '/entries', {
  token: officerAccess,
  body: {
    entries: [{
      id: uuid(), batchId: scoreBatch, periodId: rolePeriodId, studentId: roleStudentId, ruleId: 'r1',
      ruleLabel: '回答问题', ruleDeltaDefault: 2, delta: 2, source: 'rule',
      operatorId: officerId, operatorName: '班委小周', identifyMethod: 'nfc',
      occurredAt: now, createdAt: now,
    }],
  },
});
check('班委能记分', officerScore.status === 200 && officerScore.data?.inserted === 1, JSON.stringify(officerScore.data));
const officerPull = await call('GET', '/sync?since=0&limit=50', { token: officerAccess });
check('班委能拉数据（看不见名单就没法记分）', officerPull.status === 200);

// 撤销必须放行：本地规则允许班委 5 分钟内撤掉自己刚记的那批，
// 云端要是比本地严，就会出现"界面上撤了、账对不上"
const officerReverse = await call('POST', `/entries/batch/${scoreBatch}/reverse`, {
  token: officerAccess,
  body: { operatorId: officerId, operatorName: '班委小周' },
});
check('班委能撤销自己记的一批', officerReverse.status === 200 && officerReverse.data?.reversed === 1, JSON.stringify(officerReverse.data));

// 续期不能把角色洗回班主任 —— 这是最容易漏的一处
const officerRefreshed = await call('POST', '/refresh', { body: { refresh: officerRefresh } });
check('班委刷新后仍是 officer', officerRefreshed.data?.role === 'officer' && claimsOf(officerRefreshed.data?.access ?? '').role === 'officer', JSON.stringify(officerRefreshed.data?.role));
officerRefresh = officerRefreshed.data?.refresh;
const monitorRefreshed = await call('POST', '/refresh', { body: { refresh: monitorRefresh } });
check('班长刷新后仍是 co_owner', monitorRefreshed.data?.role === 'co_owner', monitorRefreshed.data?.role);

// 把班委成员删掉：他手上那台设备下一轮续期就该断，而不是继续用 12 小时
const removeOfficer = await call('POST', '/members', { token: ownerAccess, body: { action: 'delete', member: { id: officerId } } });
check('删除班委成员', removeOfficer.status === 200, `status=${removeOfficer.status}`);
const afterRevoke = await call('POST', '/refresh', { body: { refresh: officerRefresh } });
check('成员被删后刷新被拒', afterRevoke.status === 401, `status=${afterRevoke.status}`);
check('错误码是 member_revoked', afterRevoke.data?.error === 'member_revoked', afterRevoke.data?.error);

console.log('\n11) 收尾');
const pong = await call('GET', '/ping', { token: access });
check('GET /ping 通', pong.status === 200 && pong.data?.ok === true);

const out = await call('POST', '/signout', { token: access });
check('POST /signout 撤销设备', out.status === 200);

const afterOut = await call('GET', '/ping', { token: access });
check('撤销后 access 仍可解析（到期自然失效），refresh 已废', afterOut.status === 200);

console.log('\n12) 安全加固');

// 客户端是桌面程序，不需要跨域。响应里不该出现 access-control-allow-origin，
// 除非部署者显式配了 ALLOWED_ORIGINS。
const noOrigin = await call('GET', '/ping', { token: access });
check('默认不返回 CORS 通配', !noOrigin.headers.get('access-control-allow-origin'));

const evil = await call('GET', '/ping', { token: access, headers: { origin: 'https://evil.example' } });
check(
  '不在白名单的 Origin 拿不到 CORS 头',
  evil.headers.get('access-control-allow-origin') === null,
  evil.headers.get('access-control-allow-origin') ?? 'null',
);

check('带 nosniff', noOrigin.headers.get('x-content-type-options') === 'nosniff');
check('带 CSP（default-src none）', (noOrigin.headers.get('content-security-policy') ?? '').includes("default-src 'none'"));
check('带 HSTS', (noOrigin.headers.get('strict-transport-security') ?? '').includes('max-age='));

// 超大请求体必须在读进内存之前就被挡掉，否则一个 100 MB 的 POST 能直接把实例打爆
const huge = await call('POST', '/entries', {
  token: access,
  body: { entries: [], pad: 'x'.repeat(1_200_000) },
});
check('超过 1 MB 的请求体被拒（413）', huge.status === 413, `status=${huge.status}`);

// 500 不能把内部 message 抖出来。这里没法稳定造一个真 500，
// 但可以确认 4xx 的 message 是预设话术，而不是异常原文（含 SQL 片段）。
const badJson = await fetch(BASE + '/pair', {
  method: 'POST',
  headers: { 'content-type': 'application/json' },
  body: '{not json',
}).then(async (r) => ({ status: r.status, data: await r.json().catch(() => null) }));
check('坏 JSON 返回 400 且带错误码', badJson.status === 400 && !!badJson.data?.error, JSON.stringify(badJson.data));

// 这条必须放最后：一旦触发，这个来源 15 分钟内不许再配对。
// 前 6 次是"密码错"，第 7 次应该在算哈希之前就被挡掉（429）。
let lastStatus = 0;
for (let i = 0; i < 7; i++) {
  const r = await call('POST', '/pair', { body: { code, secret: 'definitely-wrong', kind: 'desk' } });
  lastStatus = r.status;
  if (r.status === 429) break;
}
check('连续猜错密码会被限流（429）', lastStatus === 429, `last=${lastStatus}`);

console.log(`\n${failures.length === 0 ? '✅' : '❌'} 通过 ${pass} 项，失败 ${failures.length} 项`);
if (failures.length) {
  for (const f of failures) console.log('   - ' + f);
  process.exit(1);
}
