using ClassPoints.Core;
using Microsoft.Data.Sqlite;

namespace ClassPoints.Data;

/// <summary>一场考试的概况（成绩页列表用，不加载全部分数）。</summary>
public sealed record ExamSummary(Exam Exam, int PresentCount, int AbsentCount, int MissingCount)
{
    public string DateText => string.IsNullOrWhiteSpace(Exam.ExamDate) ? "—" : Exam.ExamDate!;

    public string FullScoreText => Exam.FullScore.ToString("0.#");

    public string ImportedText => Clock.ToLocal(Exam.ImportedAt).ToString("yyyy-MM-dd HH:mm");
}

/// <summary>成绩导入的落库结果。</summary>
public sealed record ExamImportResult(
    string ExamId,
    int Written,
    int Absent,
    IReadOnlyList<string> Unmatched,
    IReadOnlyList<string> Ignored);

public sealed class ExamRepository(Database db)
{
    // ---------------- 考试 ----------------

    public List<Exam> ListExams(string periodId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM exams WHERE period_id = $p ORDER BY COALESCE(exam_date,'') DESC, imported_at DESC";
        cmd.Parameters.AddWithValue("$p", periodId);
        using var r = cmd.ExecuteReader();
        var list = new List<Exam>();
        while (r.Read()) list.Add(Mappers.Exam(r));
        return list;
    }

    /// <summary>
    /// 本机录入过的全部考试，不分周期。
    ///
    /// 考试与总评页必须列全部场次（旧周期也要能删、能查）：
    /// 只列当前周期的话，开过一次新周期之后，成绩卡片会说"还没有成绩"，
    /// 而下面的排名预览却仍然把这些考试算进去了。
    /// </summary>
    public List<Exam> ListAllExams()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM exams ORDER BY COALESCE(exam_date,'') DESC, imported_at DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<Exam>();
        while (r.Read()) list.Add(Mappers.Exam(r));
        return list;
    }

    public Exam? GetExam(string examId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM exams WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", examId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Mappers.Exam(r) : null;
    }

    /// <summary>成绩页那四行概况：参考、缺考、未录入。</summary>
    public List<ExamSummary> ListSummaries(string periodId, int studentCount)
    {
        var exams = ListExams(periodId);
        var result = new List<ExamSummary>(exams.Count);
        using var conn = db.Open();
        foreach (var ex in exams)
        {
            var present = Convert.ToInt32(Database.Scalar(conn,
                "SELECT COUNT(*) FROM exam_scores WHERE exam_id = $e AND absent = 0", ("$e", ex.Id)));
            var absent = Convert.ToInt32(Database.Scalar(conn,
                "SELECT COUNT(*) FROM exam_scores WHERE exam_id = $e AND absent = 1", ("$e", ex.Id)));
            var missing = System.Math.Max(0, studentCount - present - absent);
            result.Add(new ExamSummary(ex, present, absent, missing));
        }
        return result;
    }

    public List<ExamScore> Scores(string examId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM exam_scores WHERE exam_id = $e";
        cmd.Parameters.AddWithValue("$e", examId);
        using var r = cmd.ExecuteReader();
        var list = new List<ExamScore>();
        while (r.Read())
            list.Add(new ExamScore
            {
                ExamId = r.GetStr("exam_id"),
                StudentId = r.GetStr("student_id"),
                Score = r.GetDoubleOrNull("score"),
                Absent = r.GetBool("absent"),
            });
        return list;
    }

    /// <summary>指定场次的分数（<paramref name="termKey"/> 已不再参与过滤，保留参数只为零改动调用点）。</summary>
    public List<ExamScore> ScoresForTerm(string? termKey, IReadOnlyList<string> examIds)
    {
        if (examIds.Count == 0) return new List<ExamScore>();
        var placeholders = string.Join(",", examIds.Select((_, i) => $"$e{i}"));
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT sc.* FROM exam_scores sc
            JOIN exams e ON e.id = sc.exam_id
            WHERE e.id IN ({placeholders})
            """;
        for (var i = 0; i < examIds.Count; i++) cmd.Parameters.AddWithValue($"$e{i}", examIds[i]);
        using var r = cmd.ExecuteReader();
        var list = new List<ExamScore>();
        while (r.Read())
            list.Add(new ExamScore
            {
                ExamId = r.GetStr("exam_id"),
                StudentId = r.GetStr("student_id"),
                Score = r.GetDoubleOrNull("score"),
                Absent = r.GetBool("absent"),
            });
        return list;
    }

    /// <summary>
    /// 导入一场考试。重名导入同一场考试时按 id 覆盖（同一场考试重导是修数据，不是新增）。
    /// 已存在该 examId 时先清空分数再写，避免上一版多出来的行残留。
    /// </summary>
    public ExamImportResult ImportScores(string periodId, string examName, double fullScore, string? examDate,
        IEnumerable<(string StudentId, double? Score, bool Absent)> rows, string? sourceFile)
    {
        var now = Clock.NowMs();
        var matched = rows.ToList();
        var exam = new Exam
        {
            Id = Ids.NewUuid7(),
            PeriodId = periodId,
            Name = examName,
            FullScore = fullScore <= 0 ? 750 : fullScore,
            ExamDate = examDate,
            ImportedAt = now,
            SourceFile = sourceFile,
        };

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        // 同名即同一场：总评把全部场次一起算，若按周期各存一份，
        // 新学期重导同一张成绩单就会变成两场，权重和分数都被算两遍。
        var existing = Database.ScalarTx(conn, tx,
            "SELECT id FROM exams WHERE name = $n ORDER BY imported_at LIMIT 1",
            ("$n", examName));
        if (existing is string existId)
        {
            exam.Id = existId;
            Database.Exec(conn, tx, "DELETE FROM exam_scores WHERE exam_id = $e", ("$e", exam.Id));
            Database.Exec(conn, tx,
                "UPDATE exams SET full_score = $f, exam_date = $d, imported_at = $at, source_file = $src WHERE id = $id",
                ("$id", exam.Id), ("$f", exam.FullScore), ("$d", examDate), ("$at", now), ("$src", sourceFile));
        }
        else
        {
            Database.Exec(conn, tx, """
                INSERT INTO exams (id, period_id, name, full_score, exam_date, imported_at, source_file)
                VALUES ($id, $p, $n, $f, $d, $at, $src)
                """,
                ("$id", exam.Id), ("$p", periodId), ("$n", examName), ("$f", exam.FullScore),
                ("$d", examDate), ("$at", now), ("$src", sourceFile));
        }

        var written = 0;
        var absent = 0;
        foreach (var row in matched)
        {
            Database.Exec(conn, tx,
                "INSERT OR REPLACE INTO exam_scores (exam_id, student_id, score, absent) VALUES ($e, $s, $sc, $a)",
                ("$e", exam.Id), ("$s", row.StudentId),
                ("$sc", row.Absent ? null : row.Score), ("$a", row.Absent ? 1 : 0));
            if (row.Absent) absent++; else written++;
        }

        // 一场考试 + 全部成绩一起入队：云端 /exams 是整批一个事务，
        // 逐条推 48 个分数跨境要等十几秒，整批只要一次往返。
        var scores = matched
            .Select(r => new { studentId = r.StudentId, score = r.Absent ? (double?)null : r.Score, absent = r.Absent })
            .ToList();
        SyncOutbox.Enqueue(conn, tx, $"exams:{exam.Id}", "exam", new { action = "upsert", exam, scores });
        tx.Commit();

        return new ExamImportResult(exam.Id, written, absent, Array.Empty<string>(), Array.Empty<string>());
    }

    public void UpdateFullScore(string examId, double fullScore)
    {
        PushExam(examId, conn =>
        {
            Database.Exec(conn, "UPDATE exams SET full_score = $f WHERE id = $id",
                ("$id", examId), ("$f", fullScore <= 0 ? 750 : fullScore));
        });
    }

    public void Rename(string examId, string name)
    {
        PushExam(examId, conn =>
        {
            Database.Exec(conn, "UPDATE exams SET name = $n WHERE id = $id", ("$id", examId), ("$n", name));
        });
    }

    /// <summary>
    /// 改名、改满分这类小改动也要入队 —— 只改本地不入队，另一端就永远看不到。
    /// 这里只推考试本身，不带 scores：云端按 id upsert，没给的分数保持原样。
    /// </summary>
    private void PushExam(string examId, Action<SqliteConnection> mutate)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        mutate(conn);
        var exam = ReadExam(conn, tx, examId);
        if (exam is not null)
            SyncOutbox.Enqueue(conn, tx, $"exams:{examId}", "exam", new { action = "upsert", exam });
        tx.Commit();
    }

    private static Exam? ReadExam(SqliteConnection conn, SqliteTransaction tx, string examId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT * FROM exams WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", examId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Mappers.Exam(r) : null;
    }

    public void Delete(string examId)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        Database.Exec(conn, tx, "DELETE FROM exam_scores WHERE exam_id = $e", ("$e", examId));
        Database.Exec(conn, tx, "DELETE FROM scheme_weights WHERE exam_id = $e", ("$e", examId));
        Database.Exec(conn, tx, "DELETE FROM exams WHERE id = $e", ("$e", examId));
        SyncOutbox.Enqueue(conn, tx, $"exams:{examId}", "exam", new { action = "delete", id = examId });
        tx.Commit();
    }

    // ---------------- 方案与权重 ----------------

    public Scheme ActiveScheme()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM schemes WHERE is_active = 1 LIMIT 1";
        using var r = cmd.ExecuteReader();
        if (r.Read()) return Mappers.Scheme(r);

        var fallback = new Scheme { Id = "sch_default", Name = "默认方案" };
        return fallback;
    }

    public void SaveScheme(Scheme s)
    {
        var examWeight = System.Math.Clamp(s.ExamWeight, 0, 1);
        var dailyWeight = 1 - examWeight;

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        Database.Exec(conn, tx, """
            INSERT INTO schemes (id, name, exam_weight, daily_weight, normalize, is_active, created_at)
            VALUES ($id, $name, $ew, $dw, $norm, 0, $now)
            ON CONFLICT(id) DO UPDATE SET name = $name, exam_weight = $ew, daily_weight = $dw, normalize = $norm
            """,
            ("$id", s.Id), ("$name", s.Name), ("$ew", examWeight), ("$dw", dailyWeight),
            ("$norm", s.Normalize ? 1 : 0), ("$now", Clock.NowMs()));
        Database.Exec(conn, tx, "UPDATE schemes SET is_active = 0");
        Database.Exec(conn, tx, "UPDATE schemes SET is_active = 1 WHERE id = $id", ("$id", s.Id));

        // 规整值要先回写到 s 再入队：outbox 的载荷是对 s 的**即时序列化**，
        // 以前排在入队之后，推上云端的是入参原值（可能超出 0–1），
        // 本地库里存的却是钳制过的 —— 两台设备从此算出两套成绩。
        s.ExamWeight = examWeight;
        s.DailyWeight = dailyWeight;
        s.IsActive = true;
        SyncOutbox.Enqueue(conn, tx, $"schemes:{s.Id}", "scheme", new { action = "upsert", scheme = s });
        tx.Commit();
    }

    /// <summary>
    /// 读本地 scheme_weights。**1.3.3 起不再影响计算**：各场考试按场次平分，
    /// 这里留着只为诊断与旧库兼容（另一台老设备推上来的值仍会落表，但不再被读）。
    /// </summary>
    public Dictionary<string, double> Weights(string schemeId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT exam_id, weight FROM scheme_weights WHERE scheme_id = $s";
        cmd.Parameters.AddWithValue("$s", schemeId);
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, double>();
        while (r.Read()) map[r.GetString(0)] = r.GetDouble(1);
        return map;
    }

    public static AbsencePolicy ParsePolicy(string? raw) => raw switch
    {
        "zero" => AbsencePolicy.Zero,
        "average" => AbsencePolicy.ClassAverage,
        _ => AbsencePolicy.Redistribute,
    };

    public static string PolicyToDb(AbsencePolicy p) => p switch
    {
        AbsencePolicy.Zero => "zero",
        AbsencePolicy.ClassAverage => "average",
        _ => "redistribute",
    };

    /// <summary>
    /// 总评排名。平时分与考试场次都由调用方给定：现在是"本周期总分 + 本周期场次"
    /// （见 DeskExamsPage / BuildExportBundle），旧周期的数据只作历史。
    /// </summary>
    public RankingResult BuildRanking(
        IReadOnlyList<Student> students,
        IReadOnlyDictionary<string, int> cumulativePoints,
        Scheme scheme,
        AbsencePolicy policy) =>
        BuildRanking(students, cumulativePoints, scheme, policy, exams: null);

    /// <summary>
    /// 指定考试范围的排名。给"历史周期导出"用：那时只该算那个周期的场次，
    /// 不能把之后才考的成绩带回过去的榜里。
    /// </summary>
    public RankingResult BuildRanking(
        IReadOnlyList<Student> students,
        IReadOnlyDictionary<string, int> cumulativePoints,
        Scheme scheme,
        AbsencePolicy policy,
        IReadOnlyList<Exam>? exams)
    {
        var scope = exams ?? LoadExamsAscending();
        var scores = ScoresForTerm(null, scope.Select(e => e.Id).ToList());

        // 各场考试不再有"手填百分比"这回事：BuildExamWeights 拿到空表就会按场次平分
        // 并归一到 100%。以前这里读 scheme_weights，删掉界面之后旧库里的值会变成
        // "看不见但仍在生效"的暗状态，那比不让人填更糟，所以整条链路都不再读它。
        return RankingCalculator.Compute(students, cumulativePoints, scope, scores,
            new Dictionary<string, double>(), scheme, policy);
    }

    private List<Exam> LoadExamsAscending()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM exams ORDER BY COALESCE(exam_date,'') , imported_at";
        using var r = cmd.ExecuteReader();
        var exams = new List<Exam>();
        while (r.Read()) exams.Add(Mappers.Exam(r));
        return exams;
    }
}
