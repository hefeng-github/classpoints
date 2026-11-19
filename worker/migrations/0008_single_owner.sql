/**
 * 一个班只留一个班主任（1.3.12）。
 *
 * 旧客户端（≤1.3.11）的电脑端开机逻辑是"本机没有班主任行就自建一行「班主任」"。
 * 新配对的机器在第一轮拉取之前正是这个状态：成员表是空的。于是两台设备先后开机，
 * 各自建一行、各自推上来，云端就有两个 owner；再同步回所有设备，谁的认人列表里
 * 都是两个班主任，而且成员行没有彻底删除入口，多出来的那个不会自己消失。
 *
 * 处理原则：**创建最早的那行是正本**（建班时写的那一条），其余软删。
 * 并列时按 id 取小，保证多端、多次执行的结果一致。
 *
 * 为什么软删而不是 DELETE：identities.member_id 有外键，硬删会连带弄坏卡；
 * 而且设备白名单里可能还留着 member_id。软删之后 Owner() 只认最早那行，
 * 界面上自然只剩一个班主任。
 *
 * 客户端同一条规则在 Database.Migrate 的 v9 段（各设备本地收一遍，不必等同步）。
 * 新写入由 routes/entities.ts 的 settleOwner 拦住，老客户端也长不出第二个。
 */
UPDATE members
   SET deleted_at = CAST(strftime('%s', 'now') AS INTEGER) * 1000,
       updated_at = CAST(strftime('%s', 'now') AS INTEGER) * 1000,
       rev = rev + 1
 WHERE role = 'owner'
   AND deleted_at IS NULL
   AND EXISTS (
         SELECT 1 FROM members o
          WHERE o.class_id = members.class_id
            AND o.role = 'owner'
            AND o.deleted_at IS NULL
            AND (o.created_at < members.created_at
                 OR (o.created_at = members.created_at AND o.id < members.id))
       );
