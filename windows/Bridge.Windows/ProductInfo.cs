namespace RemoteVoiceInput.Windows;

internal static class ProductInfo
{
    internal static string Name => Application.ProductName ?? "Remote VoiceIME";
    internal static string VersionLabel
    {
        get
        {
            var version = typeof(ProductInfo).Assembly.GetName().Version!;
            return $"{version.ToString(3)}（构建 {version.Revision}）";
        }
    }
}
