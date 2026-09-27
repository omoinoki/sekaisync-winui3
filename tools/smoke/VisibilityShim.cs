// 冒烟工程不带 WinUI 依赖，这里补两个声明让 GUI 的源码能原样编译：
//   1) `Microsoft.UI.Xaml` 空命名空间——Models/ContentModels.cs 顶部有 `using Microsoft.UI.Xaml;`，
//      只为拿 Visibility；空声明让该 using 成立。
//   2) 同命名空间的 Visibility 枚举——文件里未限定的 `Visibility` 因此解析到这里。
// 这样冒烟工程直接链接真实的 Models/ContentModels.cs，不再维护一份会悄悄漂移的副本。
namespace Microsoft.UI.Xaml
{
}

namespace SekaiSync.Desktop.Models
{
    public enum Visibility
    {
        Visible,
        Collapsed,
    }
}
