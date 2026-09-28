using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SekaiSync.Desktop.Models;
using SekaiSync.Desktop.Services;

// Synthetic, disposable fixtures only. Never opens the user's live store.
var fixture = Path.Combine(Path.GetTempPath(), "sekaisync-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(fixture, "kb"));
var path = Path.Combine(fixture, "kb", "sekaisync.db");
var assertions = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception("FAIL: " + description);
    assertions++;
    Console.WriteLine("PASS: " + description);
}
var longId = new string('k', 300);
var body = "一歌：\0🙂" + new string('文', 1024 * 1024) + "\r\n咲希：结尾";
const string strangeTable = "odd]\"table";
try
{
    using (var write = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
    {
        write.Open();
        using var create = write.CreateCommand();
        create.CommandText = """
            CREATE TABLE web_pages(source TEXT, id TEXT, title TEXT, kind TEXT, language TEXT,
              url TEXT, crawled_at TEXT, text TEXT, untranslated INTEGER, translation_source TEXT,
              trust TEXT, canonical_key TEXT, overlay INTEGER, asset_mismatch TEXT,
              content_language_mismatch INTEGER, PRIMARY KEY(source,id));
            CREATE INDEX idx_pages_canonical ON web_pages(canonical_key);
            CREATE INDEX idx_pages_kind ON web_pages(source,kind);
            CREATE TABLE loose(value TEXT);
            INSERT INTO loose VALUES('first'),('needle'),('third');
            """;
        create.ExecuteNonQuery();
        create.CommandText = $"CREATE TABLE {SqliteAccess.Quote(strangeTable)} ({SqliteAccess.Quote("key]\"")} TEXT PRIMARY KEY, payload TEXT)";
        create.ExecuteNonQuery();
        create.CommandText = $"INSERT INTO {SqliteAccess.Quote(strangeTable)} VALUES ('safe','value')";
        create.ExecuteNonQuery();
        using var transaction = write.BeginTransaction();
        using var insert = write.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO web_pages VALUES(@s,@id,@title,@kind,'ja','','',CAST(@text AS TEXT),0,'','B',@key,0,'',0)";
        insert.Parameters.AddWithValue("@s", SourceModel.SekaiViewerPrimary);
        insert.Parameters.AddWithValue("@id", "");
        insert.Parameters.AddWithValue("@title", "");
        insert.Parameters.AddWithValue("@kind", SourceModel.StoryKinds[0]);
        insert.Parameters.AddWithValue("@text", System.Text.Encoding.UTF8.GetBytes(body));
        insert.Parameters.AddWithValue("@key", "");
        for (var i = 0; i < 64; i++)
        {
            insert.Parameters["@id"].Value = i == 0 ? longId : $"id{i:D3}";
            insert.Parameters["@title"].Value = i == 1 ? "literal %_\\ search" : "story " + i;
            insert.Parameters["@key"].Value = $"{SourceModel.StoryKinds[0]}:ja:asset{i}";
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    var env = new AppEnvironment(new AppSettings { StorePath = fixture });
    var database = new DatabaseService(env);
    await database.InitializeAsync();
    Check(database.IsAvailable, "read-only catalog opens synthetic database");
    var table = database.Tables.Single(t => t.Name == "web_pages");
    var content = new ContentQueryService(env);
    await database.QueryPageAsync(table, "", 0, 1); // warm runtime

    (long Bytes, long Ms) Baseline()
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        using var read = SqliteAccess.Open(path);
        using var command = read.CreateCommand();
        command.CommandText = "SELECT text FROM web_pages ORDER BY source,id LIMIT 64";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var text = reader.GetString(0);
            GC.KeepAlive(text[..Math.Min(240, text.Length)]);
        }
        return (GC.GetAllocatedBytesForCurrentThread() - before, timer.ElapsedMilliseconds);
    }
    var baseline = Baseline();
    var allocationStart = GC.GetTotalAllocatedBytes(true);
    var timer = Stopwatch.StartNew();
    var page = await database.QueryPageAsync(table, "", 0, 64);
    var bounded = (Bytes: GC.GetTotalAllocatedBytes(true) - allocationStart, Ms: timer.ElapsedMilliseconds);
    Check(page.Cells.Count == 64 && !page.HasMore, "exact final page has no phantom next page");
    Check(page.Cells.All(row => row.All(cell => cell.Length <= 241)), "list projections contain bounded previews");
    Check(page.Cells.All(row => row[7] == body[..240] + "…"), "raw previews preserve embedded NUL and Unicode like legacy C# slicing");
    Check(bounded.Bytes < baseline.Bytes / 10, "long-text preview allocates at least 90% less managed memory");
    Console.WriteLine($"BENCH raw preview: 64 x 1 MiB text; legacy {baseline.Ms} ms / {baseline.Bytes:N0} bytes; bounded {bounded.Ms} ms / {bounded.Bytes:N0} bytes");
    var key = page.Keys.Single(k => k[1] == longId);
    Check(key[1].Length == 300, "primary keys are never truncated with cell previews");
    var detail = await database.GetRowDetailAsync(table, key);
    Check(detail.Single(f => f.Name == "text").Value == body, "row details preserve the entire long body");
    var missing = await database.GetRowDetailAsync(table, [SourceModel.SekaiViewerPrimary, "removed"]);
    Check(missing.Length == 1 && missing[0].Name == "(提示)", "missing identity never falls back to an unrelated first row");

    var loose = database.Tables.Single(t => t.Name == "loose");
    var filtered = await database.QueryPageAsync(loose, "needle", 0, 50);
    var looseDetail = await database.GetRowDetailAsync(loose, filtered.Keys.Single());
    Check(looseDetail.Single().Value == "needle", "filtered tables without declared PK resolve details by actual rowid");
    var unusual = database.Tables.Single(t => t.Name == strangeTable);
    var unusualPage = await database.QueryPageAsync(unusual, "", 0, 50);
    Check(unusualPage.Cells.Single()[1] == "value", "quoted SQLite identifiers support brackets and embedded quotes");

    var search = new StoryQuery { Search = "%_\\" };
    Check(await content.CountStoriesAsync(search) == 1, "LIKE metacharacters remain literal user input");
    var storyPage = await content.QueryStoriesAsync(new StoryQuery(), 0, 64);
    Check(storyPage.Items.Count == 64 && !storyPage.HasMore, "story sentinel pagination is exact");
    Check((await content.QueryStoriesAsync(new StoryQuery(), 0, 63)).HasMore, "story sentinel preserves non-final pages");
    var previews = await content.LoadParallelAsync($"{SourceModel.StoryKinds[0]}:asset0", 40, InstanceFilter.Merged);
    Check(previews.Single(p => p.Language == "ja").Text.Length == 41, "parallel preview truncates in SQLite and retains ellipsis");
    Check(previews.Single(p => p.Language == "ja").Text == body[..40] + "…", "parallel previews preserve NUL and Unicode without changing character limits");
    var full = await content.LoadParallelAsync($"{SourceModel.StoryKinds[0]}:asset0", 0, InstanceFilter.Merged);
    Check(full.Single(p => p.Language == "ja").Text == body, "parallel full reading preserves complete text");
    foreach (var badLimit in new[] { -1, 0, 1001 })
    {
        try { await content.QueryStoriesAsync(new StoryQuery(), 0, badLimit); throw new Exception("unbounded page accepted"); }
        catch (ArgumentOutOfRangeException) { Check(true, $"page limit {badLimit} is rejected before SQL execution"); }
    }
    try { await database.QueryPageAsync(table, "", -1, 50); throw new Exception("negative offset accepted"); }
    catch (ArgumentOutOfRangeException) { Check(true, "negative raw-table offset is rejected"); }

    using (var read = SqliteAccess.Open(path))
    {
        using var command = read.CreateCommand();
        command.CommandText = "PRAGMA trusted_schema";
        Check(Convert.ToInt64(command.ExecuteScalar()) == 0, "untrusted database schema execution disabled");
        command.CommandText = "CREATE TABLE forbidden(x)";
        try { command.ExecuteNonQuery(); throw new Exception("write accepted"); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 8) { Check(true, "read-only connection rejects schema writes"); }
    }

    using (var cts = new CancellationTokenSource())
    using (var started = new ManualResetEventSlim())
    {
        var query = SqliteAccess.Run(() =>
        {
            using var read = SqliteAccess.Open(path, cts.Token);
            using var command = read.CreateCommand();
            command.CommandText = "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<1000000000) SELECT sum(x) FROM n";
            started.Set();
            return command.ExecuteScalar();
        }, cts.Token);
        Check(started.Wait(TimeSpan.FromSeconds(5)), "long running query starts");
        await Task.Delay(50);
        timer.Restart();
        cts.Cancel();
        try { await query.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("cancel ignored"); }
        catch (OperationCanceledException) { Check(true, "in-flight SQLite scan raises cancellation instead of query failure"); }
        Console.WriteLine($"BENCH SQLite cancellation: {timer.ElapsedMilliseconds} ms after cancel");
    }

    using (var fresh = SqliteAccess.Open(path))
    {
        using var command = fresh.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM web_pages";
        Check(Convert.ToInt64(command.ExecuteScalar()) == 64, "cancelled connection callback cannot affect a new read connection");
    }

    var blocks = TextRenderer.ParseDialogue("一歌：第一行\r\n续行\r咲希：第二行\n", false);
    Check(blocks.Count == 2 && blocks[0].Text == "第一行\n续行" && blocks[1].Speaker == "咲希", "streaming parser preserves CRLF, CR and LF dialogue semantics");
    var overlay = TextRenderer.ParseDialogue("一歌\r\n第一行。\r\n咲希\n第二行。", true);
    Check(overlay.Count == 2 && overlay[1].Text == "第二行。", "streaming parser preserves overlay speaker semantics");
    using (var cts = new CancellationTokenSource())
    {
        cts.Cancel();
        try { TextRenderer.ParseDialogue(body, false, cts.Token); throw new Exception("cancel ignored"); }
        catch (OperationCanceledException) { Check(true, "dialogue parsing honors cancellation"); }
    }
    var book = "一歌：第一行\r\n" + string.Concat(Enumerable.Repeat("长篇文本正文。\r\n", 500000));
    TextRenderer.FirstSpeaker("一歌：暖身");
    var beforeSpeaker = GC.GetAllocatedBytesForCurrentThread();
    timer.Restart();
    var speaker = TextRenderer.FirstSpeaker(book);
    var speakerBytes = GC.GetAllocatedBytesForCurrentThread() - beforeSpeaker;
    Check(speaker == "一歌" && speakerBytes < 4096, "FirstSpeaker stops at the first match without splitting a whole book");
    Console.WriteLine($"BENCH FirstSpeaker: {book.Length:N0} characters / {speakerBytes:N0} allocated bytes / {timer.ElapsedMilliseconds} ms");
    Console.WriteLine($"All {assertions} regression checks passed.");
    return 0;
}
finally
{
    SqliteConnection.ClearAllPools();
    // Only delete the exact disposable fixture this invocation created.
    Directory.Delete(fixture, recursive: true);
}
