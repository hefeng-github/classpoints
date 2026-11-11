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
  iat: number;
  exp: number;
}
