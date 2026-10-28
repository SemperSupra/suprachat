namespace SupraChat.Core;

internal static class NativeMetadataOriginPolicy
{
    internal static void Validate(Uri origin, TraceSurface surface)
    {
        if (!origin.IsAbsoluteUri) throw new UnauthorizedAccessException("Native observer origin refused.");
        var valid = origin.UserInfo.Length == 0 && origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0;
        valid &= surface == TraceSurface.Synthetic ? origin.IsLoopback && origin.Scheme is "http" or "https" :
            surface == TraceSurface.ChatGptWebView && origin.Scheme == "https" && origin.IdnHost == "chatgpt.com" && origin.Port == 443;
        if (!valid) throw new UnauthorizedAccessException("Native observer origin refused.");
    }
}
