namespace PreciseSavestates.Utils;

public static class StringExtensions {
    public static (string, string)? SplitOnce(this string str, char sep) {
        var i = str.LastIndexOf(sep);
        if (i < 0) {
            return null;
        }

        var a = str[..i];
        var b = str[(i + 1)..];
        return (a, b);
    }
}
