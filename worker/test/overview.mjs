/**
 * 班级总览与管理的回归。
 *
 * 跑之前先起服务：npx wrangler dev --port 8787 --local
 * 然后：node test/overview.mjs
 *
 * 覆盖的是"网页管理台看到的东西和真实数据一致"这条线：
 *   总览数字对得上（人数 / 流水 / 周期 / 当前周期）
 *   改显示名只改云端那一份
 *   手动清理在没到留存上限时什么都不做
 *   注销要重输班级码，注销后配对 404、续期 401（设备被撤）
 * 任何一处写反，老师看到的就是"云端明明有数据，页面说 0"或者
 * "点了注销，教室那台还在推"。
 */
import { randomUUID } from 'node:crypto';

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

async function call(method, path, { body, token } = {}) {
  const res = await fetch(BASE + path, {
    method,
    headers: {
      ...(body ? { 'content-type': 'application/json' } : {}),
      ...(token ? { authorization: `Bearer ${token}` } : {}),
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
  return { status: res.status, data };
}

const uuid = () => randomUUID();
const PIN = '112233';

console.log('\n1) 建班 + 两个周期 + 一个学生一条流水');
const created = await call('POST', '/classes', {
  body: { name: '总览测试班', ownerName: '测试老师', ownerPin: PIN, kind: 'desk' },
});
if (created.status === 409) {
  console.log('  本地班级数到上限了：清 worker/.wrangler/state/v3/d1 后重跑迁移');
  process.exit(1);
}
check('POST /classes 201', created.status === 201, `status=${created.status} ${JSON.stringify(created.data)}`);
const code = created.data?.code;
const access = created.data?.access;
const refresh = created.data?.refresh;

const now = Date.now();
const p1 = uuid();
const r1 = await call('POST', '/periods', {
  token: access,
  body: { period: { id: p1, name: '第一周期', termKey: '2026-AUT', startAt: now, status: 'active', createdAt: now } },
});
check('建第一个周期', r1.status === 200, JSON.stringify(r1.data));

const p2 = uuid();
const r2 = await call('POST', '/periods', {
  token: access,
  body: { action: 'rollover', period: { id: p2, name: '第二周期', termKey: '2026-AUT', startAt: now + 1000, status: 'active', createdAt: now + 1000 } },
});
check('rollover 到第二个周期', r2.status === 200, JSON.stringify(r2.data));

const sid = uuid();
const rs = await call('POST', '/students', {
  token: access,
  body: { action: 'upsert', student: { id: sid, name: '总览同学', seatNo: 1, active: true, rev: 1, createdAt: now, updatedAt: now } },
});
check('建一个学生', rs.status === 200 && rs.data?.applied === true, JSON.stringify(rs.data));

const re = await call('POST', '/entries', {
  token: access,
  body: {
    entries: [{
      id: uuid(), batchId: uuid(), periodId: p2, studentId: sid,
      ruleId: 'r1', ruleLabel: '回答问题', ruleDeltaDefault: 5, delta: 5, source: 'rule',
      operatorId: 'm1', operatorName: '测试老师', occurredAt: now + 2000, createdAt: now + 2000,
    }],
  },
});
check('入账一条流水', re.status === 200 && re.data?.inserted === 1, JSON.stringify(re.data));

console.log('\n2) 网页管理台登录 + 总览');
const login = await call('POST', '/admin/session', { body: { code, secret: PIN } });
check('班主任密码登录成功', login.status === 200 && !!login.data?.access, `status=${login.status}`);
const admin = login.data?.access;

const ov = await call('GET', '/admin/overview', { token: admin });
check('GET /admin/overview 200', ov.status === 200, `status=${ov.status} ${JSON.stringify(ov.data)}`);
const o = ov.data ?? {};
check('班级码与班名对得上', o.class?.code === code && o.class?.name === '总览测试班',
  `${o.class?.code} / ${o.class?.name}`);
check('在册学生 1 人', o.counts?.students === 1, `students=${o.counts?.students}`);
check('累计流水 1 条', o.counts?.entries === 1, `entries=${o.counts?.entries}`);
check('近 7 天包含这一条', o.counts?.entriesWeek === 1, `week=${o.counts?.entriesWeek}`);
check('周期数 ≥ 2（建于第一个周期）', (o.counts?.periods ?? 0) >= 2, `periods=${o.counts?.periods}`);
check('当前周期是第二个', o.current?.id === p2 && o.current?.name === '第二周期',
  `${o.current?.id} / ${o.current?.name}`);
check('当前周期的流水数 1', o.current?.entries === 1, `entries=${o.current?.entries}`);
check('保留窗口是 4 个周期', o.keepPeriods === 4, `keep=${o.keepPeriods}`);
check('最近一条流水时间有值', (o.activity?.lastEntryAt ?? 0) > 0, `last=${o.activity?.lastEntryAt}`);
check('云端变更记录不为 0', (o.counts?.changes ?? 0) > 0, `changes=${o.counts?.changes}`);

console.log('\n3) 改云端显示名');
const renamed = await call('PATCH', '/admin/class', { token: admin, body: { name: '总览测试班 (改)' } });
check('PATCH /admin/class 成功', renamed.status === 200 && renamed.data?.name === '总览测试班 (改)', JSON.stringify(renamed.data));
const afterRename = await call('GET', '/admin/overview', { token: admin });
check('总览里读到新名字', afterRename.data?.class?.name === '总览测试班 (改)', afterRename.data?.class?.name);

const emptyName = await call('PATCH', '/admin/class', { token: admin, body: { name: '   ' } });
check('空班名被拒 422', emptyName.status === 422, `status=${emptyName.status}`);

console.log('\n4) 手动清理：没到上限就什么都不动');
const trim = await call('POST', '/admin/class/trim', { token: admin });
check('POST /admin/class/trim 成功', trim.status === 200, `status=${trim.status} ${JSON.stringify(trim.data)}`);
check('不足 5 个周期时一条都不清', trim.data?.dropped === 0, `dropped=${trim.data?.dropped}`);
check('清理后流水还在', trim.data?.after?.entries === 1, `entries=${trim.data?.after?.entries}`);

console.log('\n5) 注销：要重输班级码');
const badClose = await call('POST', '/admin/class/close', { token: admin, body: { code: 'ZZZZZZZZ' } });
check('班级码不对 422 code_mismatch', badClose.status === 422 && badClose.data?.error === 'code_mismatch',
  `status=${badClose.status} ${badClose.data?.error}`);
const stillThere = await call('GET', '/admin/overview', { token: admin });
check('没注销成功时班级还在', stillThere.status === 200, `status=${stillThere.status}`);

const closed = await call('POST', '/admin/class/close', { token: admin, body: { code } });
check('POST /admin/class/close 成功', closed.status === 200, `status=${closed.status} ${JSON.stringify(closed.data)}`);

const gone = await call('GET', '/admin/overview', { token: admin });
check('注销后总览 404 class_not_found', gone.status === 404 && gone.data?.error === 'class_not_found',
  `status=${gone.status} ${gone.data?.error}`);

const pairClosed = await call('POST', '/pair', { body: { code, secret: PIN, kind: 'desk' } });
check('注销后再配对 404', pairClosed.status === 404, `status=${pairClosed.status} ${pairClosed.data?.error}`);

const refreshClosed = await call('POST', '/refresh', { body: { refresh } });
check('注销后旧设备续期 401 device_revoked',
  refreshClosed.status === 401 && refreshClosed.data?.error === 'device_revoked',
  `status=${refreshClosed.status} ${refreshClosed.data?.error}`);

console.log(`\n通过 ${pass} 项` + (failures.length ? `，失败 ${failures.length} 项：\n  - ` + failures.join('\n  - ') : ''));
process.exit(failures.length ? 1 : 0);
