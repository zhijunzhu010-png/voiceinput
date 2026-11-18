namespace VoiceInput;

internal static class VideoSource
{
    public static bool TryParsePublicUri(string value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var candidate) ||
            (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps) ||
            candidate.UserInfo.Length != 0 || !IsSupportedHost(candidate))
            return false;

        uri = candidate;
        return true;
    }

    public static bool IsSupportedHost(Uri uri)
    {
        string host = uri.Host.TrimEnd('.').ToLowerInvariant();
        return host == "youtube.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal) ||
               host == "youtu.be" || host == "bilibili.com" ||
               host.EndsWith(".bilibili.com", StringComparison.Ordinal) || host == "b23.tv";
    }
}
