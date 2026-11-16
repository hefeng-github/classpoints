namespace ClassPoints.Data.Sync;

/// <summary>一条推送被服务端拒掉之后该怎么办。</summary>
public enum PushVerdict
{
    /// <summary>留着退避，下一轮再来。</summary>
    Retry,

    /// <summary>这条永远推不上去，从队列里丢掉 —— 但要把原因说出来，不能静默消失。</summary>
    Drop,

    /// <summary>令牌过期，先续期再重推。</summary>
    Auth,
}

/// <summary>
/// 推送失败的分类。
///
/// 以前所有非 2xx 都当成"网络抖了一下"塞回队列退避：一条永远推不上去的记录（比如分值超限）
/// 会让同一批的每一条都跟着重试，队列永远清不空，界面上只有一句「待同步 N 条」——
/// 这正是 2026-11-16 报障「本机的数据一直待同步」的形态。
///
/// 反过来也不能一律丢掉：外键还没同步上来（unknown_period / unknown_student）是**会自己好**的，
/// 身份不够（403）换回班主任就能补推，周期正在重置（423）过一会儿就行。
/// 分界线是"等一等会不会变好"，不是状态码看起来严不严重。
/// </summary>
public static class PushVerdicts
{
    public static PushVerdict Classify(int status, string? code)
    {
        if (status == 401) return PushVerdict.Auth;

        // 423 正在重置周期；403 当前身份不够（换回班主任/班长就能补推）；408/429 稍后再来
        if (status is 403 or 408 or 423 or 429) return PushVerdict.Retry;
        if (status >= 500) return PushVerdict.Retry;

        // 周期/学生/事件还没同步上来：另一台设备刚建，过一会儿拉一轮就有了
        if (code is "unknown_period" or "unknown_student" or "unknown_counter") return PushVerdict.Retry;

        // 其余 4xx 是这条记录本身有问题（分值不是整数、缺字段、超出合理性上限），重试一万次也一样
        return status >= 400 ? PushVerdict.Drop : PushVerdict.Retry;
    }
}
