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
  /** 允许跨域读取的来源，逗号分隔。不配就是全都不许，桌面客户端不受影响。 */
  ALLOWED_ORIGINS?: string;
}

export interface Claims {
  /** 设备 id */
  sid: string;
  /** 班级 id */
  cid: string;
  /** board = 大屏（有班委卡），desk = 电脑端（免密） */
  kind: 'board' | 'desk';
  role: 'owner' | 'co_owner' | 'officer';
  iat: number;
  exp: number;
}
