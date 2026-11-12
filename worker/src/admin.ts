/**
 * 网页管理台。
 *
 * 为什么塞在 Worker 里而不是单独一个前端工程：这里只有一件事要做 —— 让班主任
 * 看一眼"谁在门口等着进来"，点一下通过。为这点东西引一套构建、一个域名、
 * 一份部署流程，比功能本身还重。整个页面就是一个字符串，零依赖、零外链，
 * 加载时间只取决于到边缘的一跳。
 *
 * 安全上守三条：
 *   1. 页面自己不做鉴权判断，所有数据都从带 Bearer 的接口拿；页面被篡改也拿不到别的班的数据。
 *   2. CSP 只放行内联脚本和样式，没有任何外部源 —— 上面的 token 不会被外带。
 *   3. token 放 sessionStorage，关标签页即失效；不用 cookie，就没有 CSRF 那一摊。
 */

export const ADMIN_HTML = `<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="robots" content="noindex">
<title>班级管理台 · 班级积分</title>
<style>
  * { box-sizing: border-box; }
  :root {
    --bg: #f4f6fa; --card: #ffffff; --line: #e4e8ef; --ink: #1a2233;
    --muted: #6b7688; --brand: #2563eb; --ok: #15803d; --ok-bg: #e8f6ee;
    --warn: #b45309; --warn-bg: #fdf3e3; --bad: #b42318; --bad-bg: #fdeceb;
  }
  body {
    margin: 0; background: var(--bg); color: var(--ink);
    /* 不用 font 简写：简写里只要有一个字体名解析失败，整条声明就作废，
       中文会掉回宋体 —— 这是最难看出来的一种"样式没生效"。 */
    font-family: "Microsoft YaHei UI", "Microsoft YaHei", "PingFang SC", "Noto Sans SC",
                 "Segoe UI", system-ui, sans-serif;
    font-size: 15px;
    line-height: 1.6;
    -webkit-font-smoothing: antialiased;
  }
  .wrap { max-width: 940px; margin: 0 auto; padding: 28px 20px 80px; }
  h1 { font-size: 22px; margin: 0 0 4px; letter-spacing: .2px; }
  h2 { font-size: 15px; margin: 0; }
  .sub { color: var(--muted); font-size: 13px; margin: 0; }
  .card {
    background: var(--card); border: 1px solid var(--line); border-radius: 14px;
    padding: 18px 20px; margin-top: 16px;
  }
  .row { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
  .spread { justify-content: space-between; }
  label { display: block; font-size: 13px; color: var(--muted); margin: 12px 0 6px; }
  input[type=text], input[type=password] {
    width: 100%; padding: 10px 12px; border: 1px solid var(--line); border-radius: 9px;
    font: inherit; background: #fbfcfe; color: var(--ink);
  }
  input[type=text]:focus, input[type=password]:focus {
    outline: 2px solid rgba(37,99,235,.18); border-color: var(--brand); background: #fff;
  }
  button {
    font: inherit; border-radius: 9px; border: 1px solid var(--line); background: #fff;
    color: var(--ink); padding: 8px 14px; cursor: pointer; transition: .15s;
  }
  button:hover { border-color: #c9d2e0; background: #fafbfd; }
  button.primary { background: var(--brand); border-color: var(--brand); color: #fff; }
  button.primary:hover { background: #1d4fd8; }
  button.good { color: var(--ok); border-color: #bfe3cd; background: var(--ok-bg); }
  button.bad { color: var(--bad); border-color: #f3c9c5; background: var(--bad-bg); }
  button.ghost { border-color: transparent; background: transparent; color: var(--muted); }
  button:disabled { opacity: .55; cursor: default; }
  .mono {
    font-family: "Cascadia Mono", Consolas, "SF Mono", monospace; font-size: 13px;
    letter-spacing: .4px; word-break: break-all;
  }
  .fp {
    display: inline-block; padding: 2px 7px; border-radius: 6px; background: #f2f5fa;
    border: 1px solid #e8edf5; cursor: copy;
  }
  .badge { font-size: 12px; padding: 2px 8px; border-radius: 999px; white-space: nowrap; }
  .b-pending { color: var(--warn); background: var(--warn-bg); }
  .b-approved { color: var(--ok); background: var(--ok-bg); }
  .b-blocked { color: var(--bad); background: var(--bad-bg); }
  .item { border-top: 1px solid var(--line); padding: 14px 0; display: flex; gap: 14px; align-items: flex-start; justify-content: space-between; flex-wrap: wrap; }
  .item:first-of-type { border-top: 0; }
  .grow { flex: 1 1 320px; min-width: 0; }
  .meta { color: var(--muted); font-size: 12.5px; margin-top: 4px; }
  .actions { display: flex; gap: 8px; align-items: center; }
  .hint { color: var(--muted); font-size: 13px; }
  .err { color: var(--bad); font-size: 13px; min-height: 20px; margin-top: 10px; }
  .empty { color: var(--muted); font-size: 13.5px; padding: 14px 0; }
  .switch { display: inline-flex; align-items: center; gap: 8px; font-size: 13.5px; }
  .switch input { width: 40px; height: 22px; appearance: none; background: #dfe4ec; border-radius: 999px; position: relative; cursor: pointer; transition: .18s; }
  .switch input:checked { background: var(--brand); }
  .switch input::after {
    content: ""; position: absolute; top: 3px; left: 3px; width: 16px; height: 16px;
    border-radius: 50%; background: #fff; transition: .18s;
  }
  .switch input:checked::after { left: 21px; }
  .stats { color: var(--muted); font-size: 13px; }
  .toast {
    position: fixed; left: 50%; bottom: 28px; transform: translateX(-50%) translateY(12px);
    background: #1a2233; color: #fff; font-size: 13.5px; padding: 9px 16px; border-radius: 9px;
    opacity: 0; pointer-events: none; transition: .2s; max-width: 82vw;
  }
  .toast.show { opacity: 1; transform: translateX(-50%) translateY(0); }
  code.k { background: #f2f5fa; padding: 1px 5px; border-radius: 5px; }
</style>
</head>
<body>
<div class="wrap">

  <div id="loginView">
    <h1>班级管理台</h1>
    <p class="sub">设备白名单：哪台电脑能进这个班，由这里点通过。</p>
    <div class="card" style="max-width:420px">
      <label for="code">班级码</label>
      <input id="code" type="text" autocomplete="off" spellcheck="false" placeholder="8 位班级码">
      <label for="secret">班主任密码</label>
      <input id="secret" type="password" autocomplete="current-password" placeholder="班主任密码或恢复码">
      <div class="err" id="loginErr"></div>
      <button class="primary" id="loginBtn" style="width:100%">进入</button>
      <p class="hint" style="margin:12px 0 0">
        只认班主任密码和恢复码。班长、班委的口令进不来 —— 审批权只在班主任手里。
      </p>
    </div>
  </div>

  <div id="appView" hidden>
    <div class="row spread">
      <div>
        <h1 id="className">班级</h1>
        <p class="sub"><span id="classCode"></span> · <span class="stats" id="stats"></span></p>
      </div>
      <div class="row">
        <label class="switch" style="margin:0">
          <input type="checkbox" id="mode">
          <span>新设备需批准</span>
        </label>
        <button class="ghost" id="logout">退出</button>
      </div>
    </div>

    <div class="card">
      <div class="row spread">
        <h2>待审核</h2>
        <span class="hint" id="pendingHint"></span>
      </div>
      <div id="pendingList"></div>
    </div>

    <div class="card">
      <div class="row spread">
        <h2>已通过</h2>
        <span class="hint">撤销后，那台机器的同步会立刻断开（最长 12 小时内彻底失效）。</span>
      </div>
      <div id="approvedList"></div>
    </div>

    <div class="card">
      <div class="row spread">
        <h2>已拒绝</h2>
        <span class="hint">拒绝过的设备不会反复出现在待审里，改成通过即可恢复。</span>
      </div>
      <div id="blockedList"></div>
    </div>

    <div class="card">
      <h2>手动添加</h2>
      <p class="hint" style="margin:6px 0 0">
        在客户端「设置 → 同步」里能看到本机特征码，抄过来提前放行，那台机器第一次就能进。
      </p>
      <div class="row" style="margin-top:12px">
        <input id="addFp" type="text" placeholder="设备特征码，例如 A1B2-C3D4-E5F6-7890-ABCD" style="flex:1 1 280px">
        <input id="addLabel" type="text" placeholder="备注（可选）" style="flex:0 1 200px">
        <button class="primary" id="addBtn">加入白名单</button>
      </div>
      <div class="err" id="addErr"></div>
    </div>
  </div>

</div>
<div class="toast" id="toast"></div>

<script>
(function () {
  var KEY = 'cp.admin.session';
  var state = { token: '', info: null, timer: null, busy: false };

  var $ = function (id) { return document.getElementById(id); };

  function toast(text) {
    var el = $('toast');
    el.textContent = text;
    el.classList.add('show');
    clearTimeout(el._t);
    el._t = setTimeout(function () { el.classList.remove('show'); }, 2200);
  }

  var ROLE = { owner: '班主任', co_owner: '班长', officer: '班委' };

  function fmt(ms) {
    if (!ms) return '—';
    var d = new Date(ms);
    var p = function (n) { return n < 10 ? '0' + n : '' + n; };
    return (d.getMonth() + 1) + '-' + p(d.getDate()) + ' ' + p(d.getHours()) + ':' + p(d.getMinutes());
  }

  function ago(ms) {
    if (!ms) return '—';
    var s = Math.max(0, (Date.now() - ms) / 1000);
    if (s < 60) return '刚刚';
    if (s < 3600) return Math.floor(s / 60) + ' 分钟前';
    if (s < 86400) return Math.floor(s / 3600) + ' 小时前';
    if (s < 86400 * 30) return Math.floor(s / 86400) + ' 天前';
    if (s < 86400 * 365) return Math.floor(s / 86400 / 30) + ' 个月前';
    return Math.floor(s / 86400 / 365) + ' 年前';
  }

  // 服务端返回的字段一律当不可信文本处理：除了 textContent，不往 innerHTML 里塞。
  function el(tag, cls, text) {
    var n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text !== undefined && text !== null) n.textContent = text;
    return n;
  }

  function api(path, options) {
    options = options || {};
    var headers = { 'content-type': 'application/json' };
    if (state.token) headers.authorization = 'Bearer ' + state.token;
    return fetch('/api/v1/admin' + path, {
      method: options.method || 'GET',
      headers: headers,
      body: options.body ? JSON.stringify(options.body) : undefined,
    }).then(function (res) {
      return res.text().then(function (text) {
        var data = null;
        try { data = text ? JSON.parse(text) : null; } catch (e) { data = null; }
        if (res.status === 401 && state.token) {
          signOut('登录已过期，请重新进入');
          throw new Error('unauthorized');
        }
        if (!res.ok) throw new Error((data && data.message) || ('请求失败（' + res.status + '）'));
        return data;
      });
    });
  }

  function showLogin(message) {
    $('appView').hidden = true;
    $('loginView').hidden = false;
    $('loginErr').textContent = message || '';
    if (state.timer) { clearInterval(state.timer); state.timer = null; }
  }

  function signOut(message) {
    state.token = '';
    state.info = null;
    sessionStorage.removeItem(KEY);
    showLogin(message || '');
  }

  function enter(token, info) {
    state.token = token;
    state.info = info;
    sessionStorage.setItem(KEY, JSON.stringify({ token: token, info: info }));
    $('loginView').hidden = true;
    $('appView').hidden = false;
    $('className').textContent = info.className || '班级';
    $('classCode').textContent = '班级码 ' + info.code;
    if (state.timer) clearInterval(state.timer);
    state.timer = setInterval(function () {
      if (!document.hidden && !state.busy) load();
    }, 15000);
    load();
  }

  function grantRow(g, actions) {
    var item = el('div', 'item');
    var left = el('div', 'grow');

    var title = el('div');
    title.appendChild(el('strong', null, g.label || '未命名设备'));
    title.appendChild(document.createTextNode('  '));
    var badge = el('span', 'badge b-' + g.status,
      g.status === 'pending' ? '待审核' : g.status === 'approved' ? '已通过' : '已拒绝');
    title.appendChild(badge);
    left.appendChild(title);

    var fpLine = el('div', 'meta');
    var chip = el('span', 'fp mono', g.display);
    chip.title = '点击复制';
    chip.addEventListener('click', function () {
      var text = g.display;
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).then(function () { toast('已复制 ' + text); },
          function () { toast('复制失败，请手动选中'); });
      } else {
        var r = document.createRange();
        r.selectNodeContents(chip);
        var sel = window.getSelection();
        sel.removeAllRanges();
        sel.addRange(r);
        toast('已选中，按 Ctrl+C 复制');
      }
    });
    fpLine.appendChild(chip);
    left.appendChild(fpLine);

    var parts = [];
    if (g.role) parts.push('用的是' + (ROLE[g.role] || '未知') + '口令');
    if (g.attempts > 1) parts.push('试过 ' + g.attempts + ' 次');
    if (g.status === 'pending') parts.push('首次 ' + fmt(g.createdAt));
    if (g.lastSeenAt) parts.push('最近 ' + ago(g.lastSeenAt));
    if (g.decidedAt) parts.push('处理于 ' + fmt(g.decidedAt));
    left.appendChild(el('div', 'meta', parts.join(' · ')));

    item.appendChild(left);

    var box = el('div', 'actions');
    actions.forEach(function (a) {
      var b = el('button', a.cls || '', a.text);
      b.addEventListener('click', function () {
        b.disabled = true;
        a.run().then(function () { load(); }).catch(function (e) {
          b.disabled = false;
          toast(e.message || '操作失败');
        });
      });
      box.appendChild(b);
    });
    item.appendChild(box);
    return item;
  }

  function fill(id, grants, render, emptyText) {
    var box = $(id);
    box.textContent = '';
    if (!grants.length) { box.appendChild(el('div', 'empty', emptyText)); return; }
    grants.forEach(function (g) { box.appendChild(render(g)); });
  }

  function load() {
    state.busy = true;
    return api('/devices').then(function (data) {
      state.info = { classId: data.classId, className: data.className, code: data.code, joinMode: data.joinMode };
      sessionStorage.setItem(KEY, JSON.stringify({ token: state.token, info: state.info }));

      $('className').textContent = data.className;
      $('classCode').textContent = '班级码 ' + data.code;
      $('stats').textContent = (data.joinMode === 'approval' ? '新模式：新设备需批准' : '当前模式：口令对了就能进')
        + ' · 在线设备 ' + data.activeDevices + ' 台';
      $('mode').checked = data.joinMode === 'approval';

      var pending = data.grants.filter(function (g) { return g.status === 'pending'; });
      var approved = data.grants.filter(function (g) { return g.status === 'approved'; });
      var blocked = data.grants.filter(function (g) { return g.status === 'blocked'; });

      $('pendingHint').textContent = pending.length
        ? '有 ' + pending.length + ' 台在等，确认是班里的机器再通过'
        : '';

      fill('pendingList', pending, function (g) {
        return grantRow(g, [
          { text: '通过', cls: 'good', run: function () { return api('/devices/' + g.id + '/approve', { method: 'POST' }).then(function () { toast('已通过：' + (g.label || g.display)); }); } },
          { text: '拒绝', cls: 'bad', run: function () { return api('/devices/' + g.id + '/reject', { method: 'POST' }).then(function () { toast('已拒绝'); }); } },
        ]);
      }, '没有设备在等审核。');

      fill('approvedList', approved, function (g) {
        return grantRow(g, [
          { text: '撤销', cls: 'bad', run: function () { return api('/devices/' + g.id + '/revoke', { method: 'POST' }).then(function () { toast('已撤销'); }); } },
          { text: '移出', cls: 'ghost', run: function () { return api('/devices/' + g.id, { method: 'DELETE' }).then(function () { toast('已移出白名单'); }); } },
        ]);
      }, '白名单还是空的。可以等设备来申请，也可以在下面手动添加。');

      fill('blockedList', blocked, function (g) {
        return grantRow(g, [
          { text: '恢复', cls: 'good', run: function () { return api('/devices/' + g.id + '/approve', { method: 'POST' }).then(function () { toast('已恢复'); }); } },
          { text: '移出', cls: 'ghost', run: function () { return api('/devices/' + g.id, { method: 'DELETE' }).then(function () { toast('已移出'); }); } },
        ]);
      }, '没有被拒绝的设备。');
    }).catch(function (e) {
      if (e.message !== 'unauthorized') toast(e.message || '刷新失败');
    }).then(function () {
      state.busy = false;
    });
  }

  $('loginBtn').addEventListener('click', function () {
    var code = $('code').value.trim().toUpperCase();
    var secret = $('secret').value;
    if (!code || !secret) { $('loginErr').textContent = '班级码和密码都要填。'; return; }
    $('loginBtn').disabled = true;
    $('loginErr').textContent = '';
    fetch('/api/v1/admin/session', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ code: code, secret: secret, label: '网页管理台' }),
    }).then(function (res) {
      return res.text().then(function (text) {
        var data = null;
        try { data = text ? JSON.parse(text) : null; } catch (e) { data = null; }
        if (!res.ok) throw new Error((data && data.message) || '登录失败');
        return data;
      });
    }).then(function (data) {
      $('secret').value = '';
      enter(data.access, {
        classId: data.classId, className: data.className, code: data.code, joinMode: data.joinMode,
      });
    }).catch(function (e) {
      $('loginErr').textContent = e.message || '登录失败';
    }).then(function () {
      $('loginBtn').disabled = false;
    });
  });

  $('secret').addEventListener('keydown', function (e) {
    if (e.key === 'Enter') $('loginBtn').click();
  });

  $('mode').addEventListener('change', function () {
    var want = $('mode').checked ? 'approval' : 'open';
    api('/settings', { method: 'PATCH', body: { joinMode: want } }).then(function () {
      toast(want === 'approval' ? '已切换：新设备需要批准' : '已切换：口令对了就能进');
      load();
    }).catch(function (e) {
      $('mode').checked = !$('mode').checked;
      toast(e.message || '切换失败');
    });
  });

  $('addBtn').addEventListener('click', function () {
    var fp = $('addFp').value.trim();
    var label = $('addLabel').value.trim();
    if (!fp) { $('addErr').textContent = '先填特征码。'; return; }
    $('addErr').textContent = '';
    $('addBtn').disabled = true;
    api('/devices', { method: 'POST', body: { fingerprint: fp, label: label || null } })
      .then(function () {
        $('addFp').value = '';
        $('addLabel').value = '';
        toast('已加入白名单');
        load();
      })
      .catch(function (e) { $('addErr').textContent = e.message || '添加失败'; })
      .then(function () { $('addBtn').disabled = false; });
  });

  $('logout').addEventListener('click', function () { signOut(''); });

  var saved = sessionStorage.getItem(KEY);
  if (saved) {
    try {
      var parsed = JSON.parse(saved);
      if (parsed && parsed.token && parsed.info) enter(parsed.token, parsed.info);
    } catch (e) {
      sessionStorage.removeItem(KEY);
    }
  }
})();
</script>
</body>
</html>`;
