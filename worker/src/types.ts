/**
 * 服务端只读两种东西：环境绑定和请求。
 * 这里把两者写清楚，后面每个路由都靠它。
 */
export interface Env {
  DB: D1Database;
  TOKEN_SECRET: string;
  TOKEN_TTL_HOURS: string;
  REFRESH_TTL_DAYS: string;
  PBKDF2_ITERATIONS: string;

  /**
   * 建班口令。配了之后 /classes 必须带 x-bootstrap-secret 头才能建班。
   * 部署在公网就一定要配（wrangler secret put BOOTSTRAP_SECRET），
   * 否则只有数量上限和限流兜着 —— 那只能防手滑，防不住有心人。
   */
  BOOTSTRAP_SECRET?: string;
  /** 一个部署最多放几个班，默认 20。防止有人把这个地址当免费数据库用。 */
  MAX_CLASSES?: string;
  /**
   * 配对时最多扫几个成员的口令，默认 120。
   * 每个成员一次 PBKDF2，扫描量直接等于 CPU 时间，所以给个上限；
   * 超出的班级仍然可以用班主任密码配对，只是班委自己刷不进来。
   */
  PAIR_MEMBER_SCAN?: string;
  /** 允许跨域读取的来源，逗号分隔。不配就是全都不许，桌面客户端不受影响。 */
  ALLOWED_ORIGINS?: string;

  /**
   * 换周期时把旧周期的分数结转清零（默认开）。
   *
   * 这一个开关是为「总评只看当前周期」准备的过渡手段：客户端还没改口径之前，
   * 云端用等额负分把旧周期抹平，累计积分就等于当前周期。
   * 客户端改成"直接取当期总分"之后，把它设成 "0" 即可，不用改代码。
   */
  CARRYOVER_OLD_PERIODS?: string;

  /**
   * 平台管理员口令（至少 8 位）。配了之后管理台上可以跨班级查看/改名/清周期/注销，
   * 不配就整块关闭（503），绝不做一个"空口令也能进"的默认。
   *
   * 它和班主任密码是两套东西：这个管"这台部署上有哪些班"，
   * 班主任密码管"我这个班里谁能进来"。别把两者混用。
   */
  PLATFORM_PIN?: string;
}

/**
 * 班内角色。字面量与客户端 MemberRoleNames.ToDb() 一一对应：
 * 改这里就必须改 Domain.cs，否则两边对"班长能不能改名单"的判断会不一致。
 * owner = 班主任，co_owner = 班长，officer = 班委。
 */
export type Role = 'owner' | 'co_owner' | 'officer';

export interface Claims {
  /** 设备 id */
  sid: string;
  /** 班级 id */
  cid: string;
  /** board = 大屏（有班委卡），desk = 电脑端（免密） */
  kind: 'board' | 'desk';
  role: Role;
  /** 令牌代表的成员 id。空 = 用班主任密码或恢复码配的设备，没有具体成员。 */
  mid?: string | null;
  /**
   * 只读令牌：设备还在「待审核」，班主任没点通过。
   *
   * 它只值一件事 —— 能拉数据、能看，不能写。之所以给这张票，是因为
   * "口令对了但机器没批"卡死整台机器太粗暴：老师拿着对的口令，却连
   * 本班名单都打不开，只能干等。给只读票既让人能继续看，又挡住了写，
   * 还留了升级通道（refresh 时通了就换成全权令牌）。
   */
  ro?: boolean;
  /**
   * 平台管理员令牌（部署级，不对应任何班级）。
   *
   * 有它就不该走任何班级接口 —— 那种令牌能看所有班，万一哪条班级路由
   * 只判 role 不判 cid，就成了越权的口子。auth.requireAuth 会直接把它挡掉。
   */
  plat?: boolean;
  iat: number;
  exp: number;
}
