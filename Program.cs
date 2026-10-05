using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

var b = WebApplication.CreateBuilder(args);
var du = b.Configuration["DATABASE_URL"];
var cs = !string.IsNullOrEmpty(du) ? X.Cs(du) : b.Configuration.GetConnectionString("Default") ?? "Host=localhost;Database=qrexam;Username=postgres;Password=postgres";
b.Services.AddDbContext<Db>(o => o.UseNpgsql(cs));
b.Services.AddSignalR();
b.Services.AddSingleton<Sess>();
b.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o => {
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
});
b.Services.AddAuthorization();
var app = b.Build();
using (var sc = app.Services.CreateScope()) {
    var d = sc.ServiceProvider.GetRequiredService<Db>();
    d.Database.EnsureCreated();
    if (!d.Cfg.Any()) { d.Cfg.Add(new Cfg()); d.SaveChanges(); }
    app.Services.GetRequiredService<Sess>().Id = d.Res.Max(r => (int?)r.Sess) ?? 0;
}
app.UseDefaultFiles(); app.UseStaticFiles(); app.UseAuthentication(); app.UseAuthorization();
app.MapHub<H>("/hub");
app.MapGet("/admin", () => Results.File(Path.Combine(app.Environment.WebRootPath, "admin.html"), "text/html"));
app.MapGet("/display", () => Results.File(Path.Combine(app.Environment.WebRootPath, "display.html"), "text/html"));

// ---------- عام ----------
app.MapGet("/api/public", async (Db d, Sess s) => { var c = await d.Cfg.FirstAsync();
    return new { c.Title, c.Sub, c.Foot1, c.Foot2, c.Min, c.Count, c.Pass, c.Vol, hasSound = c.Snd != null, branches = await d.Branches.OrderBy(x => x.Name).ToListAsync(), state = X.St(s) }; });
app.MapGet("/api/logo", async (Db d) => { var l = (await d.Cfg.FirstAsync()).Logo;
    if (l == null) return Results.File(Path.Combine(app.Environment.WebRootPath, "logo.png"), "image/png");
    return Results.File(Convert.FromBase64String(l[(l.IndexOf(',') + 1)..]), l[5..l.IndexOf(';')]); });
app.MapGet("/api/sound", async (Db d) => { var c = await d.Cfg.FirstAsync(); return c.Snd == null ? Results.NotFound() : Results.File(Convert.FromBase64String(c.Snd), c.SndMime ?? "audio/mpeg"); });
app.MapGet("/api/state", (Sess s) => X.St(s));
app.MapGet("/api/results", async (Db d, Sess s) => await d.Res.Where(r => r.Sess == s.Id).OrderByDescending(r => r.At).ToListAsync());
app.MapGet("/api/me", (HttpContext c) => new { role = c.User.FindFirstValue(ClaimTypes.Role) });
app.MapPost("/api/logout", async (HttpContext c) => { await c.SignOutAsync(); return Results.Ok(); });
app.MapPost("/api/enter", async (HttpContext c, Db d, N n) => {
    var name = (n.Name ?? "").Trim(); var br = await d.Branches.FindAsync(n.BranchId);
    if (name.Length < 2 || br == null) return Results.BadRequest(new { error = "اكتب اسمك واختر الفرع" });
    await X.In(c, "Emp", Random.Shared.Next(1, int.MaxValue), name.Length > 60 ? name[..60] : name, br.Name); return Results.Ok(); });
app.MapPost("/api/alogin", async (HttpContext c, A a) => {
    if (a.User != (b.Configuration["ADMIN_USER"] ?? "admin") || a.Pass != (b.Configuration["ADMIN_PASSWORD"] ?? "admin123")) return Results.BadRequest(new { error = "بيانات المدير غير صحيحة" });
    await X.In(c, "Admin"); return Results.Ok(); });

// ---------- الموظف ----------
var emp = app.MapGroup("/api/exam").RequireAuthorization(p => p.RequireRole("Emp"));
emp.MapPost("/start", async (HttpContext c, Db d, Sess s) => {
    if (!s.Running) return Results.BadRequest(new { error = "الامتحان غير متاح حالياً" });
    var id = X.Id(c);
    if (await d.Res.AnyAsync(r => r.Sess == s.Id && r.EmpId == id)) return Results.BadRequest(new { error = "لقد قدّمت الامتحان مسبقاً" });
    if (!s.Att.TryGetValue(id, out var qs)) {
        var all = await d.Qs.ToListAsync(); var cfg = await d.Cfg.FirstAsync();
        if (all.Count == 0) return Results.BadRequest(new { error = "لم تتم إضافة أسئلة بعد" });
        qs = all.OrderBy(_ => Random.Shared.Next()).Take(cfg.Count).ToList(); s.Att[id] = qs;
    }
    return Results.Ok(new { st = X.St(s), qs = qs.Select(q => new { q.Text, q.Opts }) }); });
emp.MapPost("/submit", async (HttpContext c, Db d, Sess s, IHubContext<H> h, int[] ans) => {
    var id = X.Id(c);
    if (!s.Att.TryRemove(id, out var qs)) return Results.BadRequest(new { error = "لا توجد محاولة نشطة" });
    if (s.End == null || DateTime.UtcNow > s.End.Value.AddSeconds(20)) ans = new int[0];
    var nm = c.User.FindFirstValue(ClaimTypes.Name); var br = c.User.FindFirstValue("br");
    int sc = 0; for (int i = 0; i < qs.Count; i++) if (i < ans.Length && ans[i] == qs[i].Correct) sc++;
    int pct = (int)Math.Round(sc * 100.0 / qs.Count); var cfg = await d.Cfg.FirstAsync();
    var r = new Res { Sess = s.Id, EmpId = id, Name = nm, Branch = br, Score = sc, Total = qs.Count, Pct = pct, Pass = pct >= cfg.Pass, At = DateTime.UtcNow };
    d.Res.Add(r); await d.SaveChangesAsync(); await h.Clients.All.SendAsync("res", r); return Results.Ok(r); });

// ---------- المدير ----------
var adm = app.MapGroup("/api/admin").RequireAuthorization(p => p.RequireRole("Admin"));
adm.MapGet("/cfg", async (Db d) => await d.Cfg.Select(c => new { c.Title, c.Sub, c.Foot1, c.Foot2, c.Min, c.Count, c.Pass, c.Vol, hasSound = c.Snd != null }).FirstAsync());
adm.MapPut("/cfg", async (Db d, C n) => { var c = await d.Cfg.FirstAsync();
    c.Title = n.Title; c.Sub = n.Sub; c.Foot1 = n.Foot1; c.Foot2 = n.Foot2;
    c.Min = Math.Clamp(n.Min, 1, 300); c.Count = Math.Clamp(n.Count, 1, 500); c.Pass = Math.Clamp(n.Pass, 0, 100); c.Vol = Math.Clamp(n.Vol, 0, 1);
    await d.SaveChangesAsync(); return Results.Ok(); });
adm.MapPut("/logo", async (HttpRequest r, Db d) => { var c = await d.Cfg.FirstAsync(); c.Logo = $"data:{r.ContentType};base64,{await X.Rd(r)}"; await d.SaveChangesAsync(); return Results.Ok(); });
adm.MapDelete("/logo", async (Db d) => { (await d.Cfg.FirstAsync()).Logo = null; await d.SaveChangesAsync(); return Results.Ok(); });
adm.MapPut("/sound", async (HttpRequest r, Db d) => { var c = await d.Cfg.FirstAsync(); c.Snd = await X.Rd(r); c.SndMime = r.ContentType; await d.SaveChangesAsync(); return Results.Ok(); });
adm.MapDelete("/sound", async (Db d) => { (await d.Cfg.FirstAsync()).Snd = null; await d.SaveChangesAsync(); return Results.Ok(); });

adm.MapPost("/branches", async (Db d, Branch x) => { if (string.IsNullOrWhiteSpace(x.Name)) return Results.BadRequest(new { error = "اكتب اسم الفرع" }); x.Id = 0; d.Branches.Add(x); await d.SaveChangesAsync(); return Results.Ok(); });
adm.MapDelete("/branches/{id}", async (Db d, int id) => { await d.Branches.Where(x => x.Id == id).ExecuteDeleteAsync(); return Results.Ok(); });

adm.MapGet("/qs", async (Db d) => await d.Qs.OrderBy(q => q.Id).ToListAsync());
adm.MapPost("/qs", async (Db d, Q x) => { var er = X.Chk(x); if (er != null) return Results.BadRequest(new { error = er }); x.Id = 0; d.Qs.Add(x); await d.SaveChangesAsync(); return Results.Ok(); });
adm.MapPut("/qs/{id}", async (Db d, int id, Q x) => { var er = X.Chk(x); if (er != null) return Results.BadRequest(new { error = er });
    var q = await d.Qs.FindAsync(id); if (q == null) return Results.NotFound(); q.Text = x.Text; q.Opts = x.Opts; q.Correct = x.Correct; await d.SaveChangesAsync(); return Results.Ok(); });
adm.MapDelete("/qs/{id}", async (Db d, int id) => { await d.Qs.Where(x => x.Id == id).ExecuteDeleteAsync(); return Results.Ok(); });

adm.MapGet("/results", async (Db d) => await d.Res.OrderByDescending(r => r.At).Take(500).ToListAsync());
adm.MapDelete("/results/{id}", async (Db d, int id) => { await d.Res.Where(x => x.Id == id).ExecuteDeleteAsync(); return Results.Ok(); });
adm.MapDelete("/results", async (Db d) => { await d.Res.ExecuteDeleteAsync(); return Results.Ok(); });

adm.MapPost("/session/start", async (Sess s, Db d, IHubContext<H> h) => { var c = await d.Cfg.FirstAsync();
    s.Id++; s.Start = DateTime.UtcNow; s.End = s.Start.Value.AddMinutes(c.Min); s.Att.Clear(); await h.Clients.All.SendAsync("state", X.St(s)); return Results.Ok(); });
adm.MapPost("/session/stop", async (Sess s, IHubContext<H> h) => { if (s.Running) s.End = DateTime.UtcNow; await h.Clients.All.SendAsync("state", X.St(s)); return Results.Ok(); });
adm.MapPost("/session/reset", async (Sess s, IHubContext<H> h) => { s.Start = null; s.End = null; s.Att.Clear(); await h.Clients.All.SendAsync("state", X.St(s)); return Results.Ok(); });
app.Run();

record N(string Name, int BranchId);
record A(string User, string Pass);
record C(string Title, string Sub, string Foot1, string Foot2, int Min, int Count, int Pass, double Vol);
class H : Hub { }
class Sess { public int Id; public DateTime? Start, End; public ConcurrentDictionary<int, List<Q>> Att = new(); public bool Running => Start != null && End > DateTime.UtcNow; }
class Branch { public int Id { get; set; } public string Name { get; set; } = ""; }
class Q { public int Id { get; set; } public string Text { get; set; } = ""; public List<string> Opts { get; set; } = new(); public int Correct { get; set; } }
class Res { public int Id { get; set; } public int Sess { get; set; } public int EmpId { get; set; } public string Name { get; set; } = ""; public string Branch { get; set; } = ""; public int Score { get; set; } public int Total { get; set; } public int Pct { get; set; } public bool Pass { get; set; } public DateTime At { get; set; } }
class Cfg {
    public int Id { get; set; } = 1;
    public string Title { get; set; } = "قسم الابلاغ عن غسل الاموال وتمويل الارهاب";
    public string Sub { get; set; } = "منصة متكاملة لتقييم الموظفين وتثقيفهم في مجال غسل الاموال وتمويل الارهاب";
    public string Foot1 { get; set; } = "نظام الاختبارات الشهرية لموظفي الارتباط في فروع مصرف الرافدين";
    public string Foot2 { get; set; } = "إعداد وتصميم / قسم الإبلاغ عن غسل الأموال وتمويل الإرهاب - وحدة IT";
    public int Min { get; set; } = 10; public int Count { get; set; } = 10; public int Pass { get; set; } = 60; public double Vol { get; set; } = 0.5;
    public string Logo { get; set; } public string Snd { get; set; } public string SndMime { get; set; }
}
class Db(DbContextOptions<Db> o) : DbContext(o) {
    public DbSet<Branch> Branches { get; set; } public DbSet<Q> Qs { get; set; } public DbSet<Res> Res { get; set; } public DbSet<Cfg> Cfg { get; set; }
}
static class X {
    public static string Cs(string u) { var x = new Uri(u); var up = x.UserInfo.Split(':');
        return $"Host={x.Host};Port={(x.Port > 0 ? x.Port : 5432)};Database={x.AbsolutePath.TrimStart('/')};Username={up[0]};Password={Uri.UnescapeDataString(up[1])};SSL Mode=Require;Trust Server Certificate=true"; }
    public static long Ms(DateTime d) => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    public static object St(Sess s) => new { state = s.Start == null ? "idle" : s.Running ? "running" : "done", end = s.End == null ? 0 : Ms(s.End.Value), now = Ms(DateTime.UtcNow) };
    public static int Id(HttpContext c) => int.Parse(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public static Task In(HttpContext c, string role, int id = 0, string name = "", string br = "") => c.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, role), new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Name, name), new Claim("br", br) }, CookieAuthenticationDefaults.AuthenticationScheme)));
    public static string Chk(Q q) => string.IsNullOrWhiteSpace(q.Text) || q.Opts == null || q.Opts.Count(z => !string.IsNullOrWhiteSpace(z)) < 2 || q.Correct < 0 || q.Correct >= q.Opts.Count || string.IsNullOrWhiteSpace(q.Opts[q.Correct]) ? "أكمل السؤال (إجابتان على الأقل مع تحديد الصحيحة)" : null;
    public static async Task<string> Rd(HttpRequest r) { using var ms = new MemoryStream(); await r.Body.CopyToAsync(ms); return Convert.ToBase64String(ms.ToArray()); }
}
