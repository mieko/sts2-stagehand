using System.Reflection;
using System.Text.RegularExpressions;

namespace Stagehand.Sts2;

// Read native properties and flatten the game's localized display text.
internal static class NativeData
{
    internal static object? Get(object? target, string name)
    {
        if (target == null)
            return null;

        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        try
        {
            return target.GetType().GetProperty(name, flags)?.GetValue(target)
            ?? target.GetType().GetField(name, flags)?.GetValue(target);
        }
        catch (TargetInvocationException) { return null; }
    }
    internal static string Text(object? value)
    {
        if (value == null)
            return "";

        var formatted = value.GetType().GetMethod("GetFormattedText", Type.EmptyTypes);
        var text = formatted?.Invoke(value, null)?.ToString() ?? value.ToString() ?? "";
        text = Regex.Replace(text, @"\[img[^\]]*\](.*?)\[/img\]", m => m.Groups[1].Value.Contains("energy_icon")
            ? " «Energy» " : " " + Path.GetFileNameWithoutExtension(m.Groups[1].Value).Replace('_', ' ') + " ");
        text = Regex.Replace(text, @"\[[^\]]*\]", "").Replace("«Energy»", "[Energy]");
        text = Regex.Replace(text, @"[ \t]+", " ");
        return Regex.Replace(text, @" +([.,!?;:])", "$1").Trim();
    }

}
