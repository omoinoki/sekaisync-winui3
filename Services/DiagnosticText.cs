using System.Text.RegularExpressions;

namespace SekaiSync.Desktop.Services;

/// <summary>
/// 展示与分享路径上的文本脱敏（ux-handbook §5.4：错误提示与外发日志默认移除
/// 用户名路径与凭证形态）。只影响界面显示与复制内容，
/// 用户在本机主动打开原始日志文件不受影响。
/// </summary>
public static class DiagnosticText
{
    private static readonly Regex UserHomePattern = new(
        @"([A-Za-z]:\\Users\\|/home/|/Users/)[^\\/\s""']+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CredentialPattern = new(
        @"(?i)(token|api[_-]?key|apikey|secret|password|authorization)(['""\s]*[:=]\s*)['""]?\S+",
        RegexOptions.Compiled);

    /// <summary>
    /// 未处理异常与错误提示里常见的两类泄漏：主目录路径、键值形态的凭证。
    /// 查询全文与私有端点不在此处处理——它们由各自的产生方负责裁剪。
    /// </summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = UserHomePattern.Replace(text, "$1<用户>");
        result = CredentialPattern.Replace(result, "$1$2<已隐去>");
        return result;
    }
}
