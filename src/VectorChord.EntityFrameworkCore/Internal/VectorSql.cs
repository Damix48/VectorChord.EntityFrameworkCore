using System.Text;

namespace VectorChord.EntityFrameworkCore.Internal;

internal static class VectorSql
{
    public const string ArrayFunctionPrefix = "__vectorchord_array_";

    public static string Convert(string input, string function, bool array)
    {
        var builder = new StringBuilder();
        AppendConversion(s => builder.Append(s), () => builder.Append(input), function, array);
        return builder.ToString();
    }

    public static void AppendConversion(Action<string> append, Action operand, string function, bool array)
    {
        if (!array)
        {
            append(function + "(");
            operand();
            append(")");
            return;
        }

        // ARRAY preserves empty arrays and null elements; the guard preserves a null array.
        append("(CASE WHEN ");
        operand();
        append(" IS NULL THEN NULL ELSE ARRAY(SELECT " + function +
            "(__vchord.value) FROM unnest(");
        operand();
        append(") WITH ORDINALITY AS __vchord(value, ordinal) ORDER BY __vchord.ordinal) END)");
    }
}
