using System.Text.RegularExpressions;

namespace DepupCode.Services;

public static class DiagnosticSecretRedactor
{
    private static readonly Regex KeyValue = new(
        @"(?i)([""']?\b(?:api[_-]?key|access[_-]?token|refresh[_-]?token|token|secret|password|client[_-]?secret|authorization|credential|private[_-]?key)\b[""']?\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\s&;,}\]]+)",
        RegexOptions.Compiled);
    private static readonly Regex SecretFlag = new(
        @"(?i)(--?(?:api[-_]?key|access[-_]?token|refresh[-_]?token|token|secret|password)\s+)[^\s]+",
        RegexOptions.Compiled);
    private static readonly Regex Bearer = new(@"(?i)\bBearer\s+[^\s,;]+", RegexOptions.Compiled);
    private static readonly Regex BasicAuth = new(@"(?i)\bBasic\s+[A-Za-z0-9+/=]{12,}", RegexOptions.Compiled);
    private static readonly Regex PrivateKeyBlock = new(
        @"(?is)-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----.*?-----END [A-Z0-9 ]*PRIVATE KEY-----",
        RegexOptions.Compiled);
    private static readonly Regex TokenShapes = new(
        @"\b(?:sk-(?:proj-|ant-)?[A-Za-z0-9_-]{16,}|(?:sk|rk)_live_[A-Za-z0-9]{16,}|whsec_[A-Za-z0-9]{16,}|AIza[0-9A-Za-z_-]{30,}|gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|glpat-[A-Za-z0-9_-]{20,}|xox[baprs]-[A-Za-z0-9-]{16,}|npm_[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|ASIA[0-9A-Z]{16})\b",
        RegexOptions.Compiled);
    private static readonly Regex Jwt = new(
        @"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{8,}\b",
        RegexOptions.Compiled);

    public static string Redact(string value)
    {
        var safe = KeyValue.Replace(value, "$1[REDACTED]");
        safe = SecretFlag.Replace(safe, "$1[REDACTED]");
        safe = Bearer.Replace(safe, "Bearer [REDACTED]");
        safe = BasicAuth.Replace(safe, "Basic [REDACTED]");
        safe = PrivateKeyBlock.Replace(safe, "[PRIVATE KEY REDACTED]");
        safe = TokenShapes.Replace(safe, "[REDACTED]");
        return Jwt.Replace(safe, "[REDACTED]");
    }
}
