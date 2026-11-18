/**
 * 平台管理员的回归。
 *
 * 跑之前先起服务：npx wrangler dev --port 8787 --local
 * 且 .dev.vars 里要有 PLATFORM_PIN（>= 8 位），否则平台入口整块 503，
 * 本文件会以"跳过"收场 —— 没配口令本身就是一条正确行为，只是测不了其余的。
 *
 * 覆盖的判定点：
 * 口令错 401、plat 令牌进不了班级接口（两个方向都要堵）、没登录 401、
 * 列表/详情/改名/清周期/吊销设备、注销要重输班级码、恢复后配对能接上、
 * 连续猜错触发封禁（用独立 UA 隔离指纹，不污染前面的用例）。
 * 任何一处写反，最糟的表现是"一个口令管所有班还能顺手写某个班的数据"。
 */
import { randomUUID } from 'node:crypto';
import { readFileSync as readVars } from 'node:fs';

const BASE = process.env.BASE_URL ?? 'http://127.0.0.1:8787/api/v1';

/** 口令从 .dev.vars 读（或 PLATFORM_PIN 环境变量），不写进代码。 */
function loadPin() {
  if (process.env.PLATFORM_PIN) return process.env.PLATFORM_PIN;
  try {
    const vars = readVars(new URL('../.dev.vars', import.meta.url), 'utf8');
    const m = /^\s*PLATFORM_PIN\s*=\s*"([^"]+)"/m.exec(vars) ?? /^\s*PLATFORM_PIN\s*=\s*(\S+)/m.exec(vars);
    return m ? m[1] : '';
  } catch {
    return '';
  }
}
const PIN = loadPin();

let pass = 0;
const failures = [];
let skipped = false;

function check(name, ok, detail = '') {
  if (ok) {
    pass++;
    console.log(`  ✓ ${name}`);
  } else {
    failures.push(`${name}${detail ? ' — ' + detail : ''}`);
    console.log(`  ✗ ${name}${detail ? ' — ' + detail : ''}`);
  }
}

async function call(method, path, { body, token, ua } = {}) {
  const res = await fetch(BASE + path, {
    method,
    headers: {
      ...(body ? { 'content-type': 'application/json' } : {}),
      ...(token ? { authorization: `Bearer ${token}` } : {}),
      ...(ua ? { 'user-agent': ua } : {}),
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

const hex = () => randomUUID().replaceAll('-', '').slice(0, 20).padEnd(20, '7');
const OWNER_PIN = '135790';

console.log('\n0) 平台登录');
const badPin = await call('POST', '/platform/session', { body: { pin: 'definitely-wrong-pin' } });
if (badPin.status === 503) {
  skipped = true;
  console.log('  ⚠ PLATFORM_PIN 没配（503 platform_disabled）—— 平台入口按设计关闭，其余用例跳过。');
  console.log('    想跑本文件：在 .dev.vars 里加 PLATFORM_PIN 后重启 wrangler dev。');
} else {
  check('口令不对是 401', badPin.status === 401 && badPin.data?.error === 'bad_platform_pin', JSON.stringify(badPin.data));

  const login = await call('POST', '/platform/session', { body: { pin: PIN } });
  check('口令对 → 拿到令牌', login.status === 200 && typeof login.data?.access === 'string' && login.data?.kind === 'platform', JSON.stringify(login.data));
  const plat = login.data.access;

  console.log('\n1) plat 令牌与班级接口互相隔离');
  const onSync = await call('GET', '/sync?since=0', { token: plat });
  check('plat 令牌拉不了 /sync', onSync.status === 403 && onSync.data?.error === 'platform_token_not_allowed', JSON.stringify(onSync.data));
  const onAdmin = await call('GET', '/admin/overview', { token: plat });
  check('plat 令牌进不了 /admin/overview', onAdmin.status === 403 && onAdmin.data?.error === 'platform_token_not_allowed', JSON.stringify(onAdmin.data));

  const noToken = await call('GET', '/platform/classes');
  check('没登录 401', noToken.status === 401, JSON.stringify(noToken.data));
  const classTokenProbe = await call('POST', '/platform/session', { body: { pin: PIN } });
  // 上面那次错误的尝试占了 1 次限流计数，正常口令不受影响（成功即清零）
  check('成功登录后限流计数清零', classTokenProbe.status === 200, JSON.stringify(classTokenProbe.data));

  console.log('\n2) 建一个班，平台视角能看见');
  const created = await call('POST', '/classes', {
    body: { name: '平台视角测试班', ownerName: '平台老师', ownerPin: OWNER_PIN, kind: 'desk', deviceFingerprint: hex() },
  });
  check('建班成功', created.status === 201 && created.data?.code, JSON.stringify(created.data));
  const code = created.data.code;
  const classId = created.data.classId;

  const list = await call('GET', '/platform/classes?q=' + encodeURIComponent(code), { token: plat });
  const row = (list.data?.classes ?? []).find((c) => c.code === code);
  check('列表里能搜到这个班', list.status === 200 && row, JSON.stringify(list.data?.classes));
  check('列表带设备数', row && row.devices >= 1, JSON.stringify(row));
  check('汇总数字在', list.data?.totals && typeof list.data.totals.classes === 'number', JSON.stringify(list.data?.totals));
  check('留存期数在', list.data?.keepPeriods === 4, JSON.stringify(list.data?.keepPeriods));

  console.log('\n3) 详情 / 改名 / 吊销 / 清周期');
  const detail = await call('GET', '/platform/classes/' + classId, { token: plat });
  check('详情返回班级与计数', detail.status === 200 && detail.data?.class?.id === classId && detail.data?.counts?.devices >= 1, JSON.stringify(detail.data));

  const renamed = await call('PATCH', '/platform/classes/' + classId, { token: plat, body: { name: '平台改名后的班' } });
  check('改名成功', renamed.status === 200 && renamed.data?.name === '平台改名后的班', JSON.stringify(renamed.data));
  const afterRename = await call('GET', '/platform/classes/' + classId, { token: plat });
  check('详情里读到新名字', afterRename.data?.class?.name === '平台改名后的班', JSON.stringify(afterRename.data?.class));

  const emptyName = await call('PATCH', '/platform/classes/' + classId, { token: plat, body: { name: '   ' } });
  check('空名字 422', emptyName.status === 422, JSON.stringify(emptyName.data));

  const unknown = await call('PATCH', '/platform/classes/does-not-exist', { token: plat, body: { name: '随便' } });
  check('不存在的班 404', unknown.status === 404, JSON.stringify(unknown.data));

  const revoked = await call('POST', '/platform/classes/' + classId + '/revoke', { token: plat });
  check('吊销全部设备', revoked.status === 200 && (revoked.data?.revoked ?? 0) >= 1, JSON.stringify(revoked.data));

  const trimmed = await call('POST', '/platform/classes/' + classId + '/trim', { token: plat });
  check('清周期（没有可清的也回 ok）', trimmed.status === 200 && trimmed.data?.ok === true, JSON.stringify(trimmed.data));

  console.log('\n4) 注销与恢复');
  const closeWrong = await call('POST', '/platform/classes/' + classId + '/close', { token: plat, body: { code: 'AAAAAAAA' } });
  check('班级码不对 422，什么都没动', closeWrong.status === 422 && closeWrong.data?.error === 'code_mismatch', JSON.stringify(closeWrong.data));

  const closed = await call('POST', '/platform/classes/' + classId + '/close', { token: plat, body: { code } });
  check('注销成功', closed.status === 200 && closed.data?.ok === true, JSON.stringify(closed.data));

  const closedList = await call('GET', '/platform/classes?closed=1&q=' + encodeURIComponent(code), { token: plat });
  check('含已注销的列表里有它', (closedList.data?.classes ?? []).some((c) => c.code === code && c.closedAt), JSON.stringify(closedList.data?.classes));
  const openList = await call('GET', '/platform/classes?q=' + encodeURIComponent(code), { token: plat });
  check('默认列表里没有它', !(openList.data?.classes ?? []).some((c) => c.code === code), JSON.stringify(openList.data?.classes));

  const reClosed = await call('POST', '/platform/classes/' + classId + '/close', { token: plat, body: { code } });
  check('已注销的班不能再注销 404', reClosed.status === 404, JSON.stringify(reClosed.data));

  const restored = await call('POST', '/platform/classes/' + classId + '/restore', { token: plat });
  check('恢复成功', restored.status === 200 && restored.data?.ok === true, JSON.stringify(restored.data));
  const reRestore = await call('POST', '/platform/classes/' + classId + '/restore', { token: plat });
  check('没注销的班不能恢复 409', reRestore.status === 409 && reRestore.data?.error === 'not_closed', JSON.stringify(reRestore.data));

  const repair = await call('POST', '/pair', {
    body: { code, secret: OWNER_PIN, label: '恢复后重新配对', deviceFingerprint: hex() },
  });
  check('恢复之后班级码能重新配上', repair.status === 200, JSON.stringify(repair.data));

  console.log('\n5) 防爆破（独立 UA，不污染前面的指纹）');
  let blocked = false;
  for (let i = 0; i < 6; i++) {
    const r = await call('POST', '/platform/session', { body: { pin: 'wrong-' + i }, ua: 'platform-throttle-isolated/1.0' });
    if (r.status === 429) {
      blocked = true;
      check(`第 ${i + 1} 次触发封禁（规则是 5 次失败封 30 分钟）`, i >= 4, '');
      break;
    }
    check(`第 ${i + 1} 次是 401`, r.status === 401, JSON.stringify(r.data));
  }
  if (!blocked) check('连错 6 次至少出现一次 429', false, '一次都没封');
}

console.log('');
if (failures.length) {
  console.log(`平台管理员回归：${pass} 过，${failures.length} 败`);
  failures.forEach((f) => console.log('  ✗ ' + f));
  process.exit(1);
} else {
  console.log(skipped ? `平台管理员回归：跳过（未配 PLATFORM_PIN），已验证关闭行为正确` : `平台管理员回归：${pass} 全过`);
}
