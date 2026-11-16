using System.Text;
using System.Text.Json;

namespace ChestTracker.Tests;

/// <summary>
/// Compares two readings of the same replay and says, in words, where they stopped agreeing.
///
/// A golden file is only worth keeping if a failure is readable. "The two files differ" about
/// four megabytes of JSON says nothing and sends you to a diff tool; "openings[412].category:
/// cofre -> municion" is the whole bug report. So this walks both trees together and names each
/// place they part company, by the path you would use to look it up.
/// </summary>
public static class JsonDiff
{
    /// <summary>
    /// Every difference between two documents, as "path: before -> after". Reading stops once
    /// enough have been found: a parser change that moves one field moves it in every match, and
    /// forty lines of the same thing say no more than the first forty.
    /// </summary>
    public static List<string> Compare(JsonElement objExpected, JsonElement objActual, int intMax = 40)
    {
        var lstOut = new List<string>();
        Walk("", objExpected, objActual, lstOut, intMax);
        return lstOut;
    }

    private static void Walk(string strPath, JsonElement objLeft, JsonElement objRight, List<string> lstOut, int intMax)
    {
        if (lstOut.Count >= intMax)
        {
            return;
        }

        if (objLeft.ValueKind != objRight.ValueKind)
        {
            lstOut.Add($"{Name(strPath)}: {Describe(objLeft)} -> {Describe(objRight)}");
            return;
        }

        switch (objLeft.ValueKind)
        {
            case JsonValueKind.Object:
                // Both sides' properties, so a field that appeared and one that went away are
                // both reported rather than only the ones the expected file happens to know
                var setNames = new List<string>();
                foreach (var objProperty in objLeft.EnumerateObject()) setNames.Add(objProperty.Name);
                foreach (var objProperty in objRight.EnumerateObject())
                {
                    if (!setNames.Contains(objProperty.Name)) setNames.Add(objProperty.Name);
                }

                foreach (var strName in setNames)
                {
                    var bInLeft = objLeft.TryGetProperty(strName, out var objLeftValue);
                    var bInRight = objRight.TryGetProperty(strName, out var objRightValue);
                    var strChild = strPath.Length == 0 ? strName : $"{strPath}.{strName}";

                    if (!bInLeft) { lstOut.Add($"{Name(strChild)}: no estaba -> {Describe(objRightValue)}"); continue; }
                    if (!bInRight) { lstOut.Add($"{Name(strChild)}: {Describe(objLeftValue)} -> ya no está"); continue; }
                    Walk(strChild, objLeftValue, objRightValue, lstOut, intMax);
                    if (lstOut.Count >= intMax) return;
                }
                return;

            case JsonValueKind.Array:
                var intLeft = objLeft.GetArrayLength();
                var intRight = objRight.GetArrayLength();
                if (intLeft != intRight)
                {
                    // The count first: one opening more or less is the headline, and the
                    // element-by-element walk after it would be a shifted list reported whole
                    lstOut.Add($"{Name(strPath)}: {intLeft} elementos -> {intRight}");
                }

                var objLeftItems = objLeft.EnumerateArray().ToArray();
                var objRightItems = objRight.EnumerateArray().ToArray();
                for (var intIndex = 0; intIndex < Math.Min(intLeft, intRight); intIndex++)
                {
                    Walk($"{strPath}[{intIndex}]", objLeftItems[intIndex], objRightItems[intIndex], lstOut, intMax);
                    if (lstOut.Count >= intMax) return;
                }
                return;

            case JsonValueKind.Number:
                // Through the text, so a figure that is the same is the same however it is
                // written, and a figure that drifted in the last decimal is still a difference
                if (!objLeft.GetRawText().Equals(objRight.GetRawText(), StringComparison.Ordinal))
                {
                    lstOut.Add($"{Name(strPath)}: {objLeft.GetRawText()} -> {objRight.GetRawText()}");
                }
                return;

            case JsonValueKind.String:
                if (!string.Equals(objLeft.GetString(), objRight.GetString(), StringComparison.Ordinal))
                {
                    lstOut.Add($"{Name(strPath)}: {Describe(objLeft)} -> {Describe(objRight)}");
                }
                return;

            default:
                return;     // true, false and null: the kinds already matched, so they agree
        }
    }

    private static string Name(string strPath) => strPath.Length == 0 ? "(raíz)" : strPath;

    private static string Describe(JsonElement objValue) => objValue.ValueKind switch
    {
        JsonValueKind.String => $"\"{Shorten(objValue.GetString())}\"",
        JsonValueKind.Object => "{…}",
        JsonValueKind.Array => $"[{objValue.GetArrayLength()} elementos]",
        JsonValueKind.Undefined => "nada",
        _ => objValue.GetRawText()
    };

    private static string Shorten(string? strText)
    {
        if (strText == null) return "";
        return strText.Length <= 60 ? strText : strText[..57] + "…";
    }

    /// <summary>The differences as one block of text, ready to be the failure message.</summary>
    public static string Describe(List<string> lstDifferences, int intMax = 40)
    {
        var objText = new StringBuilder();
        foreach (var strLine in lstDifferences.Take(intMax))
        {
            objText.AppendLine($"  {strLine}");
        }
        if (lstDifferences.Count >= intMax)
        {
            objText.AppendLine($"  … y puede que más: la comparación para a las {intMax} diferencias.");
        }
        return objText.ToString();
    }
}
