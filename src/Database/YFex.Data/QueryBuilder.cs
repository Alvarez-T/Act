using System.Data;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;

namespace Hino.Integrador.Infra;

/// <summary>
/// A powerful, simple, and maintainable SQL query builder.
/// It uses StringBuilder and Dapper's DynamicParameters for ease of use and maintenance.
/// This class provides a fluent API for building and modifying complex SQL queries.
/// </summary>
public sealed class QueryBuilder
{
    // A helper class to manage the content of each SQL clause.
    private class Clause
    {
        public StringBuilder Content { get; } = new StringBuilder();
        public string Keyword { get; }
        public string Separator { get; }

        public Clause(string keyword, string separator)
        {
            Keyword = keyword;
            Separator = separator;
        }
    }

    public enum ClauseType { SELECT, FROM, WHERE, GROUPBY, HAVING, ORDERBY }

    private readonly List<(string UnionType, QueryBuilder OtherQuery)> _unionQueries = new();

    // Stores the StringBuilder for each clause. We use a Dictionary for easy lookup.
    private Dictionary<ClauseType, Clause> _clauses;
    public ParameterBuilder ParametersBuilder { get; private set; }

    // For handling subqueries
    private string _prefix = "";
    private string _suffix = "";

    private int? _offset;
    private int? _fetch;

    public QueryBuilder()
    {
        _clauses = new Dictionary<ClauseType, Clause>
        {
            { ClauseType.SELECT,   new Clause("SELECT", ", ") },
            { ClauseType.FROM,     new Clause("FROM", ", ") },
            { ClauseType.WHERE,    new Clause("WHERE", " AND ") },
            { ClauseType.GROUPBY,  new Clause("GROUP BY", ", ") },
            { ClauseType.HAVING,   new Clause("HAVING", " AND ") },
            { ClauseType.ORDERBY,  new Clause("ORDER BY", ", ") }
        };
        ParametersBuilder = new ParameterBuilder();
    }

    #region Properties and Converters

    /// <summary>
    /// Gets the final, generated SQL string by calling ToString().
    /// </summary>
    public string Sql => ToString();

    /// <summary>
    /// Implicitly converts a string to a new Query object by parsing it.
    /// </summary>
    public static implicit operator QueryBuilder(string sql) => new QueryBuilder(sql);

    #endregion

    #region Constructors and Factories

    /// <summary>
    /// Creates a new query by parsing an existing SQL string.
    /// </summary>
    public QueryBuilder(string sql) : this()
    {
        Parse(sql);
    }

    /// <summary>
    /// Creates a new query by parsing an existing SQL string.
    /// </summary>
    public static QueryBuilder Select(string sql)
    {
        return new QueryBuilder(sql);
    }

    /// <summary>
    /// Creates a new sub-select query with an alias.
    /// </summary>
    public static QueryBuilder SubSelect(string sql, string alias)
    {
        return new QueryBuilder(sql).AsSubSelect(alias);
    }

    #endregion

    #region Core Manipulation Methods (Insert, Remove, Append)

    private void AppendElement(ClauseType clauseType, string element, string? separatorOverride = null)
    {
        if (string.IsNullOrWhiteSpace(element)) return;

        var clause = _clauses[clauseType];
        if (clause.Content.Length > 0)
        {
            clause.Content.Append(separatorOverride ?? clause.Separator);
        }
        clause.Content.Append(element);
    }

    /// <summary>
    /// Extracts the final column name or alias from a full column definition string.
    /// Examples:
    /// "T1.Name" -> "Name"
    /// "T1.FullName AS ClientName" -> "ClientName"
    /// "COUNT(T1.Id)" -> "Id" (Best guess if no alias)
    /// </summary>
    /// <returns>The final alias or name, or null if it cannot be determined.</returns>
    private string? GetAliasOrNameFromColumnDefinition(string columnDefinition)
    {
        string trimmedDef = columnDefinition.Trim();
        if (string.IsNullOrEmpty(trimmedDef)) return null;

        // First, prioritize an explicit "AS" alias, as it's the most definitive.
        // We use LastIndexOf to correctly handle cases like `CAST(...) AS ...`.
        int asIndex = trimmedDef.LastIndexOf(" AS ", StringComparison.OrdinalIgnoreCase);
        if (asIndex > 0)
        {
            // Simple check to avoid matching "AS" inside a string literal, though unlikely.
            // A more robust solution would check parenthesis level, but this is good for 99% of cases.
            string potentialAlias = trimmedDef.Substring(asIndex + 4).Trim();
            // Aliases can be quoted
            if (potentialAlias.StartsWith("[") && potentialAlias.EndsWith("]"))
                return potentialAlias.Substring(1, potentialAlias.Length - 2);
            if (potentialAlias.StartsWith("\"") && potentialAlias.EndsWith("\""))
                return potentialAlias.Substring(1, potentialAlias.Length - 2);

            return potentialAlias;
        }

        // If no alias, fall back to finding the last "word" in the definition.
        // This regex finds the last standalone word, which is usually the column name.
        Match nameMatch = Regex.Match(trimmedDef, @"\b\w+$");
        if (nameMatch.Success)
        {
            return nameMatch.Value;
        }

        return null; // Could not determine a name.
    }

    public List<string> GetSelectColumns()
    {
        var clause = _clauses[ClauseType.SELECT];
        if (clause.Content.Length == 0)
        {
            return new List<string>();
        }
        // Reuse your existing helper to correctly split the columns
        return SplitByTopLevelDelimiter(clause.Content.ToString(), ',')
               .Select(c => c.Trim())
               .Where(c => !string.IsNullOrEmpty(c))
               .ToList();
    }

    /// <summary>
    /// Insere uma nova coluna no SELECT <u>ANTES</u> da coluna indicada.
    /// </summary>f
    public QueryBuilder SelectBefore(string existingColumn, string newColumn)
    {
        var clause = _clauses[ClauseType.SELECT];
        string content = clause.Content.ToString();
        int index = content.IndexOf(existingColumn, StringComparison.OrdinalIgnoreCase);

        if (index == -1)
        {
            throw new ArgumentException($"Column '{existingColumn}' not found in the SELECT clause.", nameof(existingColumn));
        }

        clause.Content.Insert(index, $"{newColumn}{clause.Separator}");
        return this;
    }

    /// <summary>
    /// Inserts a new column into the SELECT clause after an existing column.
    /// </summary>
    public QueryBuilder SelectAfter(string newColumn, string existingColumn)
    {
        var clause = _clauses[ClauseType.SELECT];
        string content = clause.Content.ToString();
        int index = content.IndexOf(existingColumn, StringComparison.OrdinalIgnoreCase);

        if (index == -1)
        {
            throw new ArgumentException($"Column '{existingColumn}' not found in the SELECT clause.", nameof(existingColumn));
        }

        clause.Content.Insert(index + existingColumn.Length, $"{clause.Separator}{newColumn}");
        return this;
    }

    // <summary>
    /// Adds one or more columns to the SELECT clause, preventing duplicates based on the final column alias or name.
    /// If a column with the same resulting alias already exists, it will be skipped.
    /// The check is case-insensitive.
    /// </summary>
    /// <example>
    /// query.Select("T1.Id");
    /// query.SelectUnique("T2.Name AS Id"); // This will be SKIPPED because the alias "Id" already exists.
    /// </example>
    public QueryBuilder SelectUnique(params string[] columns)
    {
        var selectClause = _clauses[ClauseType.SELECT];

        // Optimization: If the select clause is empty, there are no existing aliases to check.
        if (selectClause.Content.Length == 0)
        {
            return Select(columns);
        }

        // Build a HashSet of all existing aliases for fast, case-insensitive lookups.
        var existingColumns = SplitByTopLevelDelimiter(selectClause.Content.ToString(), ',');
        var existingAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var existingColDef in existingColumns)
        {
            var alias = GetAliasOrNameFromColumnDefinition(existingColDef);
            if (alias != null)
            {
                existingAliases.Add(alias);
            }
        }

        // Iterate through the new columns to be added.
        foreach (var newColumnDef in columns)
        {
            if (string.IsNullOrWhiteSpace(newColumnDef)) continue;

            var newAlias = GetAliasOrNameFromColumnDefinition(newColumnDef);

            // If we couldn't determine an alias, we can't check for uniqueness, so we add it as a fallback.
            // Or, if the alias is not in our set of existing aliases, add it.
            if (newAlias == null || !existingAliases.Contains(newAlias))
            {
                AppendElement(ClauseType.SELECT, newColumnDef);

                // Important: If we added the column, we must also add its alias to our set
                // to prevent adding duplicates from within the same method call.
                if (newAlias != null)
                {
                    existingAliases.Add(newAlias);
                }
            }
        }

        return this;
    }

    /// <summary>
    /// Removes content from a clause.
    /// For the SELECT clause, this method can intelligently remove a column by its alias or name,
    /// even if it's part of a larger expression (e.g., "Table.Column" or "FUNC(Column) AS Alias").
    /// For other clauses, it performs a direct text removal.
    /// </summary>
    /// <param name="clauseType">The clause to modify.</param>
    /// <param name="contentToRemove">The content to remove. For SELECT, this can be a column name or alias.</param>
    public QueryBuilder Remove(ClauseType clauseType, string contentToRemove)
    {
        if (clauseType == ClauseType.SELECT)
        {
            return RemoveSelectColumnByAliasOrName(contentToRemove);
        }

        return BasicRemove(clauseType, contentToRemove);
    }

    // NEW: The original "Remove" logic is now in a private method for direct use.
    /// <summary>
    /// The basic engine for removing a literal string from a clause.
    /// </summary>
    private QueryBuilder BasicRemove(ClauseType clauseType, string contentToRemove)
    {
        var clause = _clauses[clauseType];
        string content = clause.Content.ToString();
        int index = content.IndexOf(contentToRemove, StringComparison.OrdinalIgnoreCase);

        if (index == -1)
        {
            // Allow failing silently if not found, can be more convenient
            return this;
            // Or throw if strictness is required:
            // throw new ArgumentException($"Content '{contentToRemove}' not found in the {clauseType} clause.", nameof(contentToRemove));
        }

        // Smart separator removal logic.
        string searchWithTrailing = contentToRemove + clause.Separator;
        if (content.Contains(searchWithTrailing))
        {
            clause.Content.Replace(searchWithTrailing, "");
        }
        else
        {
            string searchWithLeading = clause.Separator + contentToRemove;
            if (content.Contains(searchWithLeading))
            {
                clause.Content.Replace(searchWithLeading, "");
            }
            else
            {
                clause.Content.Replace(contentToRemove, "");
            }
        }

        return this;
    }

    /// <summary>
    /// Intelligently finds and removes a column from the SELECT clause by its alias or final column name.
    /// </summary>
    private QueryBuilder RemoveSelectColumnByAliasOrName(string aliasOrColumnName)
    {
        var clause = _clauses[ClauseType.SELECT];
        if (clause.Content.Length == 0) return this;

        var columns = SplitByTopLevelDelimiter(clause.Content.ToString(), ',');
        string? fullColumnDefinitionToRemove = null;

        foreach (string columnDef in columns)
        {
            string trimmedDef = columnDef.Trim();
            if (string.IsNullOrEmpty(trimmedDef)) continue;

            string finalName;

            // Try to find an alias first. Use a robust " LastIndexOf " check for " AS ".
            int asIndex = trimmedDef.LastIndexOf(" AS ", StringComparison.OrdinalIgnoreCase);
            if (asIndex > 0)
            {
                // Check that "AS" is not inside parentheses
                string potentialAlias = trimmedDef.Substring(asIndex + 4).Trim();
                if (potentialAlias.Equals(aliasOrColumnName, StringComparison.OrdinalIgnoreCase))
                {
                    fullColumnDefinitionToRemove = columnDef;
                    break;
                }
            }

            // If no alias matched, find the base column name.
            // This regex finds the last "word" in the string, which is typically the column name.
            Match nameMatch = Regex.Match(trimmedDef, @"\b\w+$");
            if (nameMatch.Success)
            {
                finalName = nameMatch.Value;
                if (finalName.Equals(aliasOrColumnName, StringComparison.OrdinalIgnoreCase))
                {
                    fullColumnDefinitionToRemove = columnDef;
                    break;
                }
            }
        }

        if (fullColumnDefinitionToRemove != null)
        {
            // Use the basic removal logic with the *full* text of the column we found.
            // We Trim() because the BasicRemove expects the core content.
            return BasicRemove(ClauseType.SELECT, fullColumnDefinitionToRemove.Trim());
        }

        // Optional: Throw if not found. You could also have it fail silently.
        // throw new ArgumentException($"Column or alias '{aliasOrColumnName}' not found in the SELECT clause.", nameof(aliasOrColumnName));
        return this; // Fail silently
    }

    // NEW: Helper to split a string by a delimiter, respecting nested parentheses.
    private List<string> SplitByTopLevelDelimiter(string text, char delimiter)
    {
        var parts = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return parts;

        int lastSplitIndex = 0;
        int parenLevel = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '(') parenLevel++;
            else if (c == ')') parenLevel = Math.Max(0, parenLevel - 1);
            else if (c == delimiter && parenLevel == 0)
            {
                parts.Add(text.Substring(lastSplitIndex, i - lastSplitIndex));
                lastSplitIndex = i + 1;
            }
        }

        // Add the final part of the string after the last delimiter.
        parts.Add(text.Substring(lastSplitIndex));

        return parts;
    }

    #endregion

    #region Fluent API (Select, From, Where, etc.)

    public QueryBuilder Select(params string[] columns)
    {
        foreach (var column in columns) AppendElement(ClauseType.SELECT, column);
        return this;
    }

    /// <summary>
    /// Overrides the entire SELECT clause with a new set of columns.
    /// </summary>
    public QueryBuilder OverrideSelect(params string[] columns)
    {
        _clauses[ClauseType.SELECT].Content.Clear();
        return Select(columns);
    }

    public QueryBuilder Copy()
    {
        return QueryBuilder.Select(this.ToString());
    }

    public QueryBuilder From(params string[] tables)
    {
        foreach (var table in tables)
        {
            if (!HasInClause(ClauseType.FROM, table))
            {
                AppendElement(ClauseType.FROM, table);
            }
        }

        return this;
    }

    public QueryBuilder FromSubSelect(QueryBuilder subSelect, string alias)
    {
        subSelect.AsSubSelect(alias);
        AppendElement(ClauseType.FROM, subSelect.ToString());
        ParametersBuilder.Merge(subSelect.ParametersBuilder);
        return this;
    }

    public QueryBuilder Where(params string[] conditions)
    {
        foreach (var condition in conditions) AppendElement(ClauseType.WHERE, condition);
        return this;
    }

    public QueryBuilder WhereOr(params string[] conditions)
    {
        foreach (var condition in conditions) AppendElement(ClauseType.WHERE, condition, " OR ");
        return this;
    }

    public QueryBuilder WhereExists(QueryBuilder subSelect)
    {
        AppendElement(ClauseType.WHERE, $"EXISTS ({subSelect})");
        ParametersBuilder.Merge(subSelect.ParametersBuilder);
        return this;
    }

    public QueryBuilder GroupBy(params string[] columns)
    {
        foreach (var column in columns) AppendElement(ClauseType.GROUPBY, column);
        return this;
    }

    public QueryBuilder Having(string condition, object? param = null)
    {
        AppendElement(ClauseType.HAVING, condition);
        if (param != null) ParametersBuilder.Add(param);
        return this;
    }

    public QueryBuilder OrderBy(params string[] columns)
    {
        foreach (var column in columns) AppendElement(ClauseType.ORDERBY, column);
        return this;
    }

    public QueryBuilder OrderByDesc(params string[] columns)
    {
        foreach (var column in columns) AppendElement(ClauseType.ORDERBY, $"{column} DESC");
        return this;
    }

    public DynamicParameters GetParameters()
    {
        return ParametersBuilder.ToDynamicParameters();
    }

    public QueryBuilder AddOutputParameter(string name, OracleMappingType dbType, int? size = null)
    {
        ParametersBuilder.AddOutput(name, dbType, size);
        return this;
    }

    private QueryBuilder Join(string joinType, string table, string onCondition, object? param = null)
    {
        var fromClause = _clauses[ClauseType.FROM];
        fromClause.Content.Append($" {joinType} JOIN {table} ON {onCondition}");
        if (param != null) ParametersBuilder.Add(param);
        return this;
    }

    public QueryBuilder LeftJoin(string table, string onCondition, object? param = null) => Join("LEFT", table, onCondition, param);
    public QueryBuilder RightJoin(string table, string onCondition, object? param = null) => Join("RIGHT", table, onCondition, param);
    public QueryBuilder InnerJoin(string table, string onCondition, object? param = null) => Join("INNER", table, onCondition, param);


    /// <summary>
    /// Unifica duas consultas SQL em uma só
    /// </summary>
    /// <param name="other"></param>
    /// <returns></returns>
    public QueryBuilder Apply(QueryBuilder other)
    {
        foreach (var clauseType in _clauses.Keys)
        {
            if (clauseType == ClauseType.SELECT)
            {
                // Intelligently merge columns, respecting uniqueness
                var otherColumns = other.GetSelectColumns();
                this.SelectUnique(otherColumns.ToArray());
                continue; // Skip the old logic for SELECT
            }
            // === END OF NEW LOGIC ===

            var otherClauseContent = other._clauses[clauseType].Content.ToString();
            if (!string.IsNullOrEmpty(otherClauseContent))
            {
                var separator = clauseType == ClauseType.WHERE || clauseType == ClauseType.HAVING
                    ? other._clauses[clauseType].Separator
                    : _clauses[clauseType].Separator;

                AppendElement(clauseType, otherClauseContent, separator);
            }
        }

        ParametersBuilder.Merge(other.ParametersBuilder);
        this._unionQueries.AddRange(other._unionQueries);

        return this;
    }

    public QueryBuilder AsSubSelect(string alias)
    {
        _prefix = "(";
        _suffix = $") {alias.ToUpperInvariant()}";
        return this;
    }

    public QueryBuilder AsSubSelect()
    {
        _prefix = "(";
        _suffix = $")";
        return this;
    }

    public QueryBuilder AddParameter(object parameters)
    {
        ParametersBuilder.Add(parameters);
        return this;
    }

    public QueryBuilder AddParameter(IDictionary<string, object> parameters)
    {
        ParametersBuilder.Add(parameters);
        return this;
    }

    public QueryBuilder Paginar(int pageNumber, int pageSize)
    {
        if (pageNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "Page number must be 1 or greater.");
        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be 1 or greater.");

        _offset = (pageNumber - 1) * pageSize;
        _fetch = pageSize;
        return this;
    }

    #endregion

    #region Parsing Logic

    /// <summary>
    /// A robust state-machine parser that correctly handles nested subqueries by tracking parenthesis depth.
    /// It only splits the SQL by keywords that are at the top level (parenthesis depth of 0).
    /// </summary>
    private void Parse(string sql)
    {
        // Order by length descending to match "GROUP BY" before "BY".
        var keywords = new[] { "SELECT", "FROM", "WHERE", "GROUP BY", "HAVING", "ORDER BY" }
            .OrderByDescending(k => k.Length)
            .ToList();

        var topLevelClauses = new List<(string Keyword, int Index)>();
        int parenLevel = 0;

        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];
            if (c == '(')
            {
                parenLevel++;
            }
            else if (c == ')')
            {
                parenLevel = Math.Max(0, parenLevel - 1); // Decrement, but not below zero
            }
            // Only look for keywords at the top level.
            else if (parenLevel == 0)
            {
                // Find the longest keyword that matches at the current position.
                var matchedKeyword = keywords.FirstOrDefault(kw =>
                {
                    if (i + kw.Length > sql.Length) return false;
                    // Case-insensitive comparison
                    if (string.Compare(sql, i, kw, 0, kw.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
                    // Ensure it is a whole word. The character after the keyword must not be a letter or digit.
                    char nextChar = (i + kw.Length < sql.Length) ? sql[i + kw.Length] : ' ';
                    return !char.IsLetterOrDigit(nextChar);
                });

                if (matchedKeyword != null)
                {
                    topLevelClauses.Add((matchedKeyword.ToUpperInvariant(), i));
                    // Advance the loop counter past the keyword to avoid re-matching parts of it.
                    i += matchedKeyword.Length - 1;
                }
            }
        }

        // If no clauses were found, assume the entire string is a SELECT body
        if (topLevelClauses.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(sql))
            {
                // Heuristic: If it starts with 'SELECT', strip it off before appending.
                if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    var tempSql = sql.TrimStart();
                    _clauses[ClauseType.SELECT].Content.Append(tempSql.Substring(6).Trim());
                }
                else
                {
                    _clauses[ClauseType.SELECT].Content.Append(sql);
                }
            }
            return;
        }

        // Slice the SQL string based on the top-level keyword positions
        for (int i = 0; i < topLevelClauses.Count; i++)
        {
            var currentClause = topLevelClauses[i];
            var clauseType = _clauses.First(c => c.Value.Keyword == currentClause.Keyword).Key;

            int contentStartIndex = currentClause.Index + currentClause.Keyword.Length;
            int contentEndIndex = (i + 1 < topLevelClauses.Count)
                ? topLevelClauses[i + 1].Index // Content ends where the next clause begins
                : sql.Length;                   // Or at the end of the string for the last clause

            string content = sql.Substring(contentStartIndex, contentEndIndex - contentStartIndex).Trim();

            if (!string.IsNullOrEmpty(content))
            {
                _clauses[clauseType].Content.Append(content);
            }
        }
    }

    #region Analysis Methods

    /// <summary>
    /// Checks if a specific token (like a table name, column, or alias) exists in a clause.
    /// This search is intelligent: it looks for whole words and ignores occurrences inside nested parentheses (subqueries),
    /// effectively checking only the "top level" of the specified clause.
    /// <example>
    /// <code>
    /// Example: Check the FROM clause for a table alias, ignoring the contents of the subquery.
    /// var query = new Query("SELECT T1.Id, T1.Name FROM (SELECT Id, Name FROM Users WHERE IsActive = 1) AS T1");
    /// bool hasT1 = query.HasInClause(ClauseType.FROM, "T1");          // Returns true
    /// bool hasUsers = query.HasInClause(ClauseType.FROM, "Users");      // Returns false
    ///
    /// Example: Check the SELECT clause for a top-level column.
    /// bool hasId = query.HasInClause(ClauseType.SELECT, "T1.Id");     // Returns true
    /// bool hasIsActive = query.HasInClause(ClauseType.SELECT, "IsActive"); // Returns false
    /// </code>
    /// </example>
    /// </summary>
    /// <param name="clauseType">The clause to search within (e.g., ClauseType.FROM).</param>
    /// <param name="tokenToFind">The exact whole-word token to find (e.g., a table name like "MyTable").</param>
    /// <param name="ignoreCase">If true, the search will be case-insensitive. Defaults to true.</param>
    /// <returns>True if the token is found at the top level of the clause; otherwise, false.</returns>

    public bool HasInClause(ClauseType clauseType, string tokenToFind, bool ignoreCase = true)
    {
        // 1. Get the content of the target clause.
        if (!_clauses.TryGetValue(clauseType, out var clause) || clause.Content.Length == 0)
        {
            return false; // Clause doesn't exist or is empty.
        }
        string content = clause.Content.ToString();

        // 2. Use a regular expression to find all whole-word matches of the token.
        // \b is a word boundary, ensuring we don't match substrings (e.g., "User" doesn't match "Users").
        // Regex.Escape handles any special characters in the token itself.
        var regexOptions = ignoreCase ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant : RegexOptions.None;
        var regex = new Regex(@"\b" + Regex.Escape(tokenToFind) + @"\b", regexOptions);

        var matches = regex.Matches(content);

        if (matches.Count == 0)
        {
            return false; // No matches found at all, so we can exit early.
        }

        // 3. For each match, check if it's at the top level (parenthesis depth of 0).
        foreach (Match match in matches.Cast<Match>())
        {
            int parenLevel = 0;
            // Scan the string *leading up to the match* to calculate the parenthesis depth.
            for (int i = 0; i < match.Index; i++)
            {
                if (content[i] == '(')
                {
                    parenLevel++;
                }
                else if (content[i] == ')')
                {
                    // Decrement, but don't go below zero (handles malformed SQL snippets gracefully).
                    parenLevel = Math.Max(0, parenLevel - 1);
                }
            }

            // If the parenthesis level is 0 at the point of the match, we found it at the top level.
            if (parenLevel == 0)
            {
                return true;
            }
        }

        // 4. If we iterated through all matches and none were at the top level, return false.
        return false;
    }

    #endregion

    /// <summary>
    /// Adds another query to this one using a UNION ALL clause.
    /// The provided query's SELECT statement will be appended, wrapped in parentheses.
    /// Parameters from the other query will be merged.
    /// </summary>
    /// <param name="otherQuery">The query to append with UNION ALL.</param>
    /// <returns>The current Query instance for fluent chaining.</returns>
    public QueryBuilder UnionAll(QueryBuilder otherQuery)
    {
        return AddUnion("UNION ALL", otherQuery);
    }

    /// <summary>
    /// Adds another query to this one using a UNION clause.
    /// The provided query's SELECT statement will be appended, wrapped in parentheses.
    /// Parameters from the other query will be merged.
    /// </summary>
    /// <param name="otherQuery">The query to append with UNION.</param>
    /// <returns>The current Query instance for fluent chaining.</returns>
    public QueryBuilder Union(QueryBuilder otherQuery)
    {
        return AddUnion("UNION", otherQuery);
    }

    /// <summary>
    /// Private helper to add a query to the union list.
    /// </summary>
    private QueryBuilder AddUnion(string unionType, QueryBuilder otherQuery)
    {
        if (otherQuery == null)
        {
            throw new ArgumentNullException(nameof(otherQuery), "The query for the UNION clause cannot be null.");
        }

        _unionQueries.Add((unionType, otherQuery));

        _offset = null;
        _fetch = null;
        return this;
    }

    /// <summary>
    /// Get union by index (0 based)
    /// </summary>
    /// <returns></returns>
    public QueryBuilder GetUnionByIndex(int index)
    {
        return _unionQueries[index].OtherQuery;
    }
    #endregion

    #region Final Assembly (ToString)

    /// <summary>
    /// Assembles the final, complete query string from all its clauses and any unions.
    /// </summary>
    public override string ToString()
    {
        // Build the main part of the query (the first SELECT statement)
        var sb = new StringBuilder(BuildCurrentQueryString());

        // Pagination is not configured or is meaningless without an ORDER BY clause.
        if (_offset is not null && _fetch is not null && _clauses[ClauseType.ORDERBY].Content.Length != 0)
        {
            sb.AppendLine($"OFFSET {_offset} ROWS FETCH NEXT {_fetch} ROWS ONLY");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Assembles the query string for the current instance's clauses, without any unions.
    /// This is a helper method for the main ToString() method.
    /// </summary>
    private string BuildCurrentQueryString()
    {
        var finalSql = new StringBuilder();

        finalSql.Append(_prefix);

        bool hasGroupBy = _clauses[ClauseType.GROUPBY].Content.Length > 0;

        foreach (var clauseType in (ClauseType[])Enum.GetValues(typeof(ClauseType)))
        {
            var clause = _clauses[clauseType];
            if (clause.Content.Length > 0)
            {
                if (clauseType == ClauseType.HAVING && !hasGroupBy)
                {
                    continue;
                }

                finalSql.Append(clause.Keyword).Append(' ').Append(clause.Content).Append(' ');
            }
        }

        // Append any unioned queries
        foreach (var (unionType, otherQuery) in _unionQueries)
        {
            // The otherQuery.ToString() will recursively handle its own clauses and unions.
            // Wrapping the unioned query in parentheses is best practice for complex queries.
            finalSql.Append($" {unionType} ").Append(otherQuery.ToString());
        }

        if (finalSql.Length > 0 && finalSql[^1] == ' ')
        {
            finalSql.Length--;
        }

        finalSql.Append(_suffix);

        return finalSql.ToString();
    }

    public void Clear()
    {
        _fetch = null;
        _offset = null;
        _prefix = "";
        _suffix = "";
        ParametersBuilder.Clear();
        _unionQueries.Clear();
        _clauses.Clear();
        _clauses = new Dictionary<ClauseType, Clause>
        {
            { ClauseType.SELECT,   new Clause("SELECT", ", ") },
            { ClauseType.FROM,     new Clause("FROM", ", ") },
            { ClauseType.WHERE,    new Clause("WHERE", " AND ") },
            { ClauseType.GROUPBY,  new Clause("GROUP BY", ", ") },
            { ClauseType.HAVING,   new Clause("HAVING", " AND ") },
            { ClauseType.ORDERBY,  new Clause("ORDER BY", ", ") }
        };
    }

    #endregion
}