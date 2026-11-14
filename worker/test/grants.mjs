/**
 * 设备白名单的回归。
 *
 * 跑之前先起服务：npx wrangler dev --port 8787 --local
 * 然后：node test/grants.mjs
 *
 * 覆盖的是这条链路上的每一个判定点，而不是"能跑通"：
 * 待审只签只读令牌（能拉不能写）、批准后续期自动升级、撤销当场断续期、
 * 无指纹被挡、拒绝需显式恢复、切回 open 后老行为不变。任何一处写反，
 * 都会表现成"某台机器进不来"或者更糟 —— "谁都能进"。
 */
import { randomUUID, randomBytes, pbkdf2Sync } from 'node:crypto';

const BASE = process.env.BASE_URL ?? 'http://127.0.0.1:8787/api/v1';

/** 客户端 Security.cs 里的格式：pbkdf2$轮数$base64盐$base64密钥 */
const pinHash = (pin, iterations = 100000) => {
  const salt = randomBytes(16);
  const key = pbkdf2Sync(pin, salt, iterations, 32, 'sha256');
  return `pbkdf2$${iterations}$${salt.toString('base64')}$${key.toString('base64')}`;
};

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

const hex = (seed) => seed.toString(16).padStart(20, '0').slice(0, 20);
const PIN = '246810';

console.log('\n1) 建班：默认是 open，老行为不变');
const created = await call('POST', '/classes', {
  body: { name: '白名单测试班', ownerName: '测试老师', ownerPin: PIN, kind: 'desk', deviceFingerprint: hex(1) },
});
check('POST /classes 201', created.status === 201, `status=${created.status} ${JSON.stringify(created.data)}`);
const code = created.data?.code;
check('建班的设备也记下了指纹', !!created.data?.access);

const openPair = await call('POST', '/pair', { body: { code, secret: PIN, kind: 'desk', deviceFingerprint: hex(2) } });
if (openPair.status === 429) {
  // 本地反复跑这个脚本会把 pair 的失败计数堆到阈值（比如刚跑过冒烟里的限流用例）。
  // 这时报 429 不是白名单的问题，直接说清楚怎么清，别让人去翻代码。
  console.log('  被限流了：本地先执行 npx wrangler d1 execute classpoints --local --command "DELETE FROM auth_throttle" 再跑');
  process.exit(1);
}
check('open 模式下新指纹直接配对成功', openPair.status === 200, `status=${openPair.status}`);
const openRefresh = openPair.data?.refresh;

console.log('\n2) 网页登录');
const badLogin = await call('POST', '/admin/session', { body: { code, secret: '000000' } });
check('密码错了 401', badLogin.status === 401, `status=${badLogin.status}`);

const login = await call('POST', '/admin/session', { body: { code, secret: PIN } });
check('班主任密码登录成功', login.status === 200 && !!login.data?.access, `status=${login.status}`);
check('登录响应带班级名和模式', login.data?.className === '白名单测试班' && login.data?.joinMode === 'open');
const admin = login.data?.access;

console.log('\n3) 切到审核模式');
const mode = await call('PATCH', '/admin/settings', { token: admin, body: { joinMode: 'approval' } });
check('PATCH /admin/settings 切换成功', mode.status === 200 && mode.data?.joinMode === 'approval', `status=${mode.status}`);

const afterMode = await call('GET', '/admin/devices', { token: admin });
const inherited = (afterMode.data?.grants ?? []).find((g) => g.fingerprint === hex(2));
check('开审核时，已在用的设备自动进白名单', inherited?.status === 'approved', JSON.stringify(inherited ?? null));
const badMode = await call('PATCH', '/admin/settings', { token: admin, body: { joinMode: 'whatever' } });
check('非法模式被拒', badMode.status === 422, `status=${badMode.status}`);

const noFp = await call('POST', '/pair', { body: { code, secret: PIN, kind: 'desk' } });
check('审核模式下不带指纹 → 422', noFp.status === 422 && noFp.data?.error === 'device_fingerprint_required',
  `status=${noFp.status} ${noFp.data?.error}`);

const pendingPair = await call('POST', '/pair', {
  body: { code, secret: PIN, kind: 'desk', deviceLabel: '讲台机', deviceFingerprint: hex(3) },
});
check('新设备第一次来 → 200 但只读', pendingPair.status === 200 && pendingPair.data?.readOnly === true,
  `status=${pendingPair.status} readOnly=${pendingPair.data?.readOnly}`);
check('只读也签了令牌（能看）', !!pendingPair.data?.access && !!pendingPair.data?.refresh);
check('待审响应里给出可抄的特征码', typeof pendingPair.data?.message === 'string' && pendingPair.data.message.includes('0000-0000'));
const pendingAccess = pendingPair.data?.access;
const pendingRefresh = pendingPair.data?.refresh;

console.log('\n3.5) 待审设备：能拉，不能写');
const pendingPull = await call('GET', '/sync?since=0&limit=50', { token: pendingAccess });
check('待审令牌能拉数据', pendingPull.status === 200, `status=${pendingPull.status}`);
check('/sync 明确回 readOnly=true', pendingPull.data?.readOnly === true, `readOnly=${pendingPull.data?.readOnly}`);
check('拉到了名单（不是空壳）', Array.isArray(pendingPull.data?.members) && pendingPull.data.members.length >= 1,
  `members=${pendingPull.data?.members?.length}`);

const pendingWrite = await call('POST', '/entries', {
  token: pendingAccess,
  body: {
    entries: [{
      id: randomUUID(), studentId: 'nobody', ruleId: null, ruleName: '只读测试', delta: 1,
      note: null, operatorId: null, operatorName: '测试', at: Date.now(), rev: 1,
      createdAt: Date.now(), updatedAt: Date.now(), periodId: null, count: 1, reversedAt: null,
      reversedBy: null, batchId: null,
    }],
  },
});
check('待审令牌记分 → 403 read_only_device', pendingWrite.status === 403 && pendingWrite.data?.error === 'read_only_device',
  `status=${pendingWrite.status} ${pendingWrite.data?.error}`);

const pendingManager = await call('POST', '/students', {
  token: pendingAccess,
  body: { action: 'upsert', student: { id: randomUUID(), name: '只读测试', seat: 1, rev: 1, createdAt: Date.now(), updatedAt: Date.now() } },
});
check('待审令牌改名单 → 403', pendingManager.status === 403 && pendingManager.data?.error === 'read_only_device',
  `status=${pendingManager.status} ${pendingManager.data?.error}`);

const again = await call('POST', '/pair', {
  body: { code, secret: PIN, kind: 'desk', deviceLabel: '讲台机', deviceFingerprint: hex(3) },
});
check('再试一次仍然是待审（不是 429）', again.status === 200 && again.data?.readOnly === true, `status=${again.status}`);

console.log('\n4) 管理台看得到、点得动');
const list = await call('GET', '/admin/devices', { token: admin });
const grants = list.data?.grants ?? [];
const mine = grants.find((g) => g.fingerprint === hex(3));
check('列表里有这台待审设备', !!mine && mine.status === 'pending', JSON.stringify(mine ?? null));
check('attempts 记了两次', mine?.attempts === 2, String(mine?.attempts));
check('列表带设备名', mine?.label === '讲台机', String(mine?.label));

const approved = await call('POST', `/admin/devices/${mine.id}/approve`, { token: admin });
check('通过成功', approved.status === 200 && approved.data?.status === 'approved', `status=${approved.status}`);

// 关键的一条：通过了不用重新配对，手里的只读 refresh 换出来就该是全权令牌。
// 老代码这里会把设备撤销掉（status !== approved 一律 revoke），
// 结果班主任点了通过，那台机器还是进不来，得让老师再输一遍口令。
const upgraded = await call('POST', '/refresh', { body: { refresh: pendingRefresh } });
check('通过后续期自动升级为全权令牌', upgraded.status === 200 && upgraded.data?.readOnly === false,
  `status=${upgraded.status} readOnly=${upgraded.data?.readOnly}`);
const upgradedWrite = await call('POST', '/entries', {
  token: upgraded.data?.access,
  body: {
    entries: [{
      id: randomUUID(), studentId: 'nobody', ruleId: null, ruleName: '升级测试', delta: 1,
      note: null, operatorId: null, operatorName: '测试', at: Date.now(), rev: 1,
      createdAt: Date.now(), updatedAt: Date.now(), periodId: null, count: 1, reversedAt: null,
      reversedBy: null, batchId: null,
    }],
  },
});
check('升级后的令牌写接口不再是 read_only_device',
  !(upgradedWrite.status === 403 && upgradedWrite.data?.error === 'read_only_device'),
  `status=${upgradedWrite.status} ${upgradedWrite.data?.error}`);

const okPair = await call('POST', '/pair', {
  body: { code, secret: PIN, kind: 'desk', deviceLabel: '讲台机', deviceFingerprint: hex(3) },
});
check('批准后同一台机器配对成功', okPair.status === 200 && okPair.data?.readOnly === false && !!okPair.data?.access,
  `status=${okPair.status}`);
const keptRefresh = okPair.data?.refresh;

const otherPair = await call('POST', '/pair', { body: { code, secret: PIN, kind: 'desk', deviceFingerprint: hex(4) } });
check('没批准的机器只能拿到只读令牌', otherPair.status === 200 && otherPair.data?.readOnly === true,
  `status=${otherPair.status} readOnly=${otherPair.data?.readOnly}`);

console.log('\n5) 撤销 = 当场断续期');
const revoke = await call('POST', `/admin/devices/${mine.id}/revoke`, { token: admin });
check('撤销成功', revoke.status === 200 && revoke.data?.status === 'blocked', `status=${revoke.status}`);
const revokedRefresh = await call('POST', '/refresh', { body: { refresh: keptRefresh } });
check('被撤销设备的 refresh 立刻失效', revokedRefresh.status === 401, `status=${revokedRefresh.status}`);
const blockedPair = await call('POST', '/pair', {
  body: { code, secret: PIN, kind: 'desk', deviceFingerprint: hex(3) },
});
check('被拒绝的设备再配对 → device_blocked', blockedPair.status === 403 && blockedPair.data?.error === 'device_blocked',
  `status=${blockedPair.status}`);

console.log('\n6) 手动添加与移出');
const manual = await call('POST', '/admin/devices', { token: admin, body: { fingerprint: hex(5), label: '办公室' } });
check('手动加白名单成功', manual.status === 200 && manual.data?.fingerprint === hex(5), `status=${manual.status}`);
const manualPair = await call('POST', '/pair', {
  body: { code, secret: PIN, kind: 'desk', deviceFingerprint: hex(5) },
});
check('预置的设备第一次就能进', manualPair.status === 200, `status=${manualPair.status}`);
const manualId = manual.data?.id;
const manualRefresh = manualPair.data?.refresh;
const removed = await call('DELETE', `/admin/devices/${manualId}`, { token: admin });
check('移出白名单成功', removed.status === 200, `status=${removed.status}`);
const removedRefresh = await call('POST', '/refresh', { body: { refresh: manualRefresh } });
check('移出后该设备的 refresh 也断了', removedRefresh.status === 401, `status=${removedRefresh.status}`);

const badFp = await call('POST', '/admin/devices', { token: admin, body: { fingerprint: '不是十六进制' } });
check('特征码格式不对被拒', badFp.status === 422, `status=${badFp.status}`);

console.log('\n7) 权限：班委能配对，但进不了管理台');
const officerId = randomUUID();
const mkOfficer = await call('POST', '/members', {
  token: admin,
  body: {
    action: 'upsert',
    member: {
      id: officerId, displayName: '班委小周', role: 2, pinHash: pinHash('112233'),
      rev: 1, createdAt: Date.now(), updatedAt: Date.now(),
    },
  },
});
check('建一个班委成员', mkOfficer.status === 200, `status=${mkOfficer.status} ${JSON.stringify(mkOfficer.data)}`);

await call('POST', '/admin/devices', { token: admin, body: { fingerprint: hex(7), label: '班委电脑' } });
const officerPair = await call('POST', '/pair', {
  body: { code, secret: '112233', kind: 'desk', deviceFingerprint: hex(7) },
});
check('班委口令 + 已批准设备 → 配对成功', officerPair.status === 200, `status=${officerPair.status}`);
check('签出来的是班委身份', officerPair.data?.role === 'officer', officerPair.data?.role);

const deny = await call('GET', '/admin/devices', { token: officerPair.data?.access });
check('班委读设备列表 403', deny.status === 403 && deny.data?.error === 'forbidden_role',
  `status=${deny.status} ${deny.data?.error}`);

const anon = await call('GET', '/admin/devices', {});
check('匿名读设备列表 401', anon.status === 401, `status=${anon.status}`);

console.log('\n8) 切回 open：老设备不被连累');
const back = await call('PATCH', '/admin/settings', { token: admin, body: { joinMode: 'open' } });
check('切回 open', back.status === 200 && back.data?.joinMode === 'open');
const legacy = await call('POST', '/pair', { body: { code, secret: PIN, kind: 'desk', deviceFingerprint: hex(9) } });
check('open 模式下未批准的新指纹照样能进', legacy.status === 200, `status=${legacy.status}`);
const legacyRefresh = await call('POST', '/refresh', { body: { refresh: openRefresh } });
check('升级前配的设备续期不受影响', legacyRefresh.status === 200, `status=${legacyRefresh.status}`);

console.log(`\n结果：${pass} 项通过，${failures.length} 项失败`);
if (failures.length) {
  for (const f of failures) console.log('  · ' + f);
  process.exit(1);
}
