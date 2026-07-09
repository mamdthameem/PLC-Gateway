// Shared SQL fragments used by both the pipeline (DatabaseService) and the dashboard API
// services, so the typed-column mapping lives in exactly one place.
public static class SqlExpressions
{
    // Reconstructs the legacy string value from the typed columns (value_text / value_num /
    // value_bool), falling back to the frozen TEXT `value` column for rows written before the
    // typed-column migration. Pass a table alias (e.g. "cv") when the query joins tables.
    public static string TypedValue(string alias = "")
    {
        string p = string.IsNullOrEmpty(alias) ? "" : alias + ".";
        return $"COALESCE({p}value_text, {p}value_num::text, " +
               $"CASE WHEN {p}value_bool IS TRUE THEN '1' WHEN {p}value_bool IS FALSE THEN '0' END, {p}value)";
    }
}
