using System.Windows.Controls;
using System.Windows.Threading;
using DepupCode.Models;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DepupCode.Services;

/// <summary>Provides approved, in-process browser actions for the active WebView2 preview.</summary>
public sealed class HtmlPreviewAgentTools(
    WebView2 webView,
    Dispatcher dispatcher,
    Func<bool> isPreviewVisible,
    Func<IReadOnlyList<string>> getDiagnostics)
{
    public AiExternalTool CreateTool() => new(
        "preview_page",
        "Inspect the active DepupCode web preview, take a screenshot, or interact with an element. Use inspect first. Actions are inspect, screenshot, click, fill, and refresh.",
        JObject.Parse("""
            {"type":"object","properties":{
              "action":{"type":"string","enum":["inspect","screenshot","click","fill","refresh"]},
              "target":{"type":"string","description":"Visible accessible name, or css:<selector>"},
              "value":{"type":"string","description":"Text to enter for fill"}
            },"required":["action"]}
            """),
        ReadOnly: false,
        IsAvailable: () => isPreviewVisible() && webView.CoreWebView2 is not null,
        ExecuteAsync);

    private Task<string> ExecuteAsync(AiChatSession session, JObject args,
        Func<AiToolRequest, Task<bool>> approveAction, CancellationToken cancellationToken) =>
        OnUiAsync(() => ExecuteOnUiAsync(session, args, approveAction, cancellationToken));

    private async Task<string> ExecuteOnUiAsync(AiChatSession session, JObject args,
        Func<AiToolRequest, Task<bool>> approveAction, CancellationToken cancellationToken)
    {
        var action = args["action"]?.Value<string>()?.Trim().ToLowerInvariant() ?? "inspect";
        if (!isPreviewVisible() || webView.CoreWebView2 is null)
            return "Preview tidak aktif. Tampilkan Preview lalu ulangi.";
        cancellationToken.ThrowIfCancellationRequested();

        if (action == "inspect")
        {
            var snapshot = await OnUiAsync(async () => await ReadSnapshotAsync(cancellationToken));
            var diagnostics = getDiagnostics();
            var report = DiagnosticSecretRedactor.Redact(snapshot + (diagnostics.Count == 0
                ? "\nRuntime diagnostics: none currently captured."
                : "\nRuntime diagnostics:\n" + string.Join("\n", diagnostics.TakeLast(8))));
            AddArtifact(session, "Preview", "Preview diperiksa", Summarize(report));
            return Trim(report, 10_000);
        }

        if (action == "screenshot")
            return await OnUiAsync(() => CaptureArtifactAsync(session, cancellationToken));

        var target = args["target"]?.Value<string>()?.Trim() ?? "";
        if (action is "click" or "fill")
        {
            if (target.Length == 0) return "Pilih target berdasarkan nama aksesibel atau gunakan css:<selector>.";
            var info = await OnUiAsync(() => ResolveTargetAsync(target, cancellationToken));
            if (info is null) return "Elemen tidak ditemukan pada halaman Preview.";
            var value = args["value"]?.Value<string>() ?? "";
            var secret = string.Equals(info["type"]?.Value<string>(), "password", StringComparison.OrdinalIgnoreCase);
            var details = action == "click"
                ? DiagnosticSecretRedactor.Redact($"Elemen: {info["label"]?.Value<string>()}\nURL: {webView.Source}")
                : $"Elemen: {info["label"]?.Value<string>()}\nTipe input: {info["type"]?.Value<string>() ?? info["tag"]?.Value<string>()}\nNilai: {(secret ? "[DISAMARKAN]" : DiagnosticSecretRedactor.Redact(value))}";
            if (!await approveAction(new AiToolRequest(action == "click" ? "Klik di Preview" : "Isi field Preview", target, details)))
                return "Pengguna menolak tindakan pada Preview.";
            var result = await OnUiAsync(() => ApplyInteractionAsync(action, target, value, cancellationToken));
            AddArtifact(session, "Browser action", action == "click" ? "Elemen diklik" : "Field diisi", result);
            return result;
        }

        if (action == "refresh")
        {
            if (!await approveAction(new AiToolRequest("Muat ulang Preview", DiagnosticSecretRedactor.Redact(webView.Source?.ToString() ?? "Preview"), "Memuat ulang halaman dapat menghapus state sementara yang belum disimpan.")))
                return "Pengguna menolak memuat ulang Preview.";
            await OnUiAsync(() =>
            {
                webView.CoreWebView2?.Reload();
                return Task.CompletedTask;
            });
            AddArtifact(session, "Browser action", "Preview dimuat ulang", webView.Source?.ToString() ?? "");
            return "Preview dimuat ulang. Gunakan inspect setelah navigasi selesai.";
        }

        return "Aksi preview tidak dikenal.";
    }

    private async Task<string> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(400, cancellationToken);
        var script = """
            JSON.stringify((() => {
              const label = el => (el.getAttribute('aria-label') || el.labels?.[0]?.innerText || el.innerText || el.getAttribute('placeholder') || el.id || el.name || '').trim().replace(/\s+/g, ' ').slice(0, 120);
              const controls = Array.from(document.querySelectorAll('button,a,input,textarea,select,[role="button"],[role="link"],[contenteditable="true"]')).slice(0, 80).map(el => ({tag:el.tagName.toLowerCase(),type:el.type || '',label:label(el),disabled:!!el.disabled,href:el.href || ''}));
              const images = Array.from(document.images).filter(el => el.complete && !el.naturalWidth).map(el => el.currentSrc).slice(0, 20);
              return {title:document.title,url:location.href,viewport:{width:innerWidth,height:innerHeight},text:(document.body?.innerText || '').slice(0,7000),controls,brokenImages:images};
            })())
            """;
        var raw = await webView.CoreWebView2.ExecuteScriptAsync(script);
        cancellationToken.ThrowIfCancellationRequested();
        return JsonConvert.DeserializeObject<string>(raw) ?? raw;
    }

    private async Task<JObject?> ResolveTargetAsync(string target, CancellationToken cancellationToken)
    {
        var targetJson = JsonConvert.SerializeObject(target);
        var script = $$"""
            (() => {
              const target = {{targetJson}};
              let el = null;
              if (target.toLowerCase().startsWith('css:')) {
                try { el = document.querySelector(target.slice(4)); } catch { return null; }
              } else {
                const controls = Array.from(document.querySelectorAll('button,a,input,textarea,select,[role="button"],[role="link"],[contenteditable="true"]'));
                const norm = value => (value || '').trim().replace(/\s+/g, ' ').toLowerCase();
                const labels = item => [item.getAttribute('aria-label'),item.labels?.[0]?.innerText,item.innerText,item.getAttribute('placeholder'),item.id,item.name].filter(Boolean);
                el = controls.find(item => labels(item).some(value => norm(value) === norm(target))) || controls.find(item => labels(item).some(value => norm(value).includes(norm(target))));
              }
              if (!el) return null;
              const label = (el.getAttribute('aria-label') || el.labels?.[0]?.innerText || el.innerText || el.getAttribute('placeholder') || el.id || el.name || el.tagName).trim().replace(/\s+/g, ' ').slice(0,120);
              return JSON.stringify({tag:el.tagName.toLowerCase(),type:el.type || '',label,disabled:!!el.disabled});
            })()
            """;
        var raw = await webView.CoreWebView2.ExecuteScriptAsync(script);
        cancellationToken.ThrowIfCancellationRequested();
        var decoded = JsonConvert.DeserializeObject<string>(raw);
        return string.IsNullOrWhiteSpace(decoded) || decoded == "null" ? null : JObject.Parse(decoded);
    }

    private async Task<string> ApplyInteractionAsync(string action, string target, string value, CancellationToken cancellationToken)
    {
        var targetJson = JsonConvert.SerializeObject(target);
        var valueJson = JsonConvert.SerializeObject(value);
        var script = $$"""
            (() => {
              const target = {{targetJson}}, value = {{valueJson}};
              let el = null;
              if (target.toLowerCase().startsWith('css:')) { try { el = document.querySelector(target.slice(4)); } catch { return 'Selector CSS tidak valid.'; } }
              else {
                const controls = Array.from(document.querySelectorAll('button,a,input,textarea,select,[role="button"],[role="link"],[contenteditable="true"]'));
                const norm = item => (item || '').trim().replace(/\s+/g, ' ').toLowerCase();
                const labels = item => [item.getAttribute('aria-label'),item.labels?.[0]?.innerText,item.innerText,item.getAttribute('placeholder'),item.id,item.name].filter(Boolean);
                el = controls.find(item => labels(item).some(label => norm(label) === norm(target))) || controls.find(item => labels(item).some(label => norm(label).includes(norm(target))));
              }
              if (!el) return 'Elemen tidak ditemukan.';
              if (el.disabled) return 'Elemen sedang disabled.';
              if ({{JsonConvert.SerializeObject(action)}} === 'click') { el.scrollIntoView({block:'center'}); el.click(); return 'Klik dipicu pada ' + (el.getAttribute('aria-label') || el.innerText || el.tagName).trim().slice(0,120); }
              if (el.isContentEditable) { el.focus(); el.textContent = value; }
              else if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) {
                const proto = el instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
                Object.getOwnPropertyDescriptor(proto,'value').set.call(el,value);
                el.focus();
              } else if (el instanceof HTMLSelectElement) { el.value = value; el.dispatchEvent(new Event('change',{bubbles:true})); }
              else return 'Target bukan field teks, select, atau contenteditable.';
              el.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:'insertText',data:value}));
              el.dispatchEvent(new Event('change',{bubbles:true}));
              return 'Field berhasil diisi.';
            })()
            """;
        var raw = await webView.CoreWebView2.ExecuteScriptAsync(script);
        cancellationToken.ThrowIfCancellationRequested();
        return JsonConvert.DeserializeObject<string>(raw) ?? raw;
    }

    private async Task<string> CaptureArtifactAsync(AiChatSession session, CancellationToken cancellationToken)
    {
        using var image = new MemoryStream();
        await webView.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Jpeg, image);
        cancellationToken.ThrowIfCancellationRequested();
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
#if DEBUG
            "DepupCode-Development",
#else
            "DepupCode",
#endif
            "artifacts");
        Directory.CreateDirectory(folder);
        var sessionPrefix = new string(session.Id.Where(char.IsLetterOrDigit).Take(8).ToArray());
        if (sessionPrefix.Length == 0) sessionPrefix = "session";
        var artifactId = Guid.NewGuid().ToString("N")[..8];
        var path = Path.Combine(folder, $"preview-{sessionPrefix}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{artifactId}.jpg");
        await File.WriteAllBytesAsync(path, image.ToArray(), cancellationToken);
        AddArtifact(session, "Screenshot", "Screenshot Preview", webView.Source?.ToString() ?? "Preview", path);
        return $"Screenshot tersimpan sebagai artefak lokal: {path}";
    }

    private Task<T> OnUiAsync<T>(Func<Task<T>> action) => dispatcher.CheckAccess()
        ? action()
        : dispatcher.InvokeAsync(action).Task.Unwrap();

    private Task OnUiAsync(Func<Task> action) => dispatcher.CheckAccess()
        ? action()
        : dispatcher.InvokeAsync(action).Task.Unwrap();

    private static void AddArtifact(AiChatSession session, string type, string title, string detail, string? path = null)
    {
        session.Artifacts ??= [];
        session.Artifacts.Add(new AiChatArtifact
        {
            Type = type,
            Title = title,
            Detail = Trim(DiagnosticSecretRedactor.Redact(detail), 900),
            FilePath = path,
            CreatedUtc = DateTime.UtcNow
        });
        if (session.Artifacts.Count > 80) session.Artifacts.RemoveAt(0);
    }

    private static string Summarize(string report)
    {
        var obj = JObject.Parse(report);
        return $"{obj["title"]?.Value<string>()} · {obj["url"]?.Value<string>()} · {obj["controls"]?.Count() ?? 0} kontrol · {obj["brokenImages"]?.Count() ?? 0} gambar rusak";
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
