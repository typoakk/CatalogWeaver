using System.Text.Json;

namespace MediaImporter.Database;

public sealed record DatabaseDestination
{
    public string Connection { get; init; } = "";
    public string? LocalUrlTemplate { get; init; }
    public DatabaseValue? MediaId { get; init; }
    public DatabaseTableMapping[] Tables { get; init; } = [];
}

public sealed record DatabaseTableMapping
{
    public string Name { get; init; } = "";
    public string Schema { get; init; } = "public";
    public string Table { get; init; } = "";
    public string Scope { get; init; } = "item";
    public string Operation { get; init; } = "upsert";
    public string? Role { get; init; }
    public string[] States { get; init; } = [];
    public string[] PreserveOnStates { get; init; } = [];
    public string[] MatchColumns { get; init; } = [];
    public Dictionary<string, DatabaseValue> Columns { get; init; } = [];
    public string? Returning { get; init; }
    public Dictionary<string, DatabaseValue> ReplaceScope { get; init; } = [];
}

public sealed record DatabaseValue
{
    public string? Source { get; init; }
    public JsonElement? Constant { get; init; }
    public JsonElement? Default { get; init; }
    public string OnNull { get; init; } = "write";
    public string OnUpdate { get; init; } = "write";
    public string? WhenRole { get; init; }
    public DatabaseValue[] Coalesce { get; init; } = [];
    public DatabaseValue[] Merge { get; init; } = [];
    public DatabaseLookup? Lookup { get; init; }
    public Dictionary<string, JsonElement>? Map { get; init; }
    public string MapMissing { get; init; } = "original";
    public string? Template { get; init; }
    public string? Join { get; init; }
    public int? Limit { get; init; }
    public string? Role { get; init; }
    public bool First { get; init; }
    public Dictionary<string, DatabaseValue>? Select { get; init; }
    public DatabaseValue? SelectValue { get; init; }
    public Dictionary<string, DatabaseValue>? Object { get; init; }
    public DatabaseCondition? Condition { get; init; }
    public DatabaseValue? Then { get; init; }
    public DatabaseValue? Else { get; init; }
}

public sealed record DatabaseLookup
{
    public string Schema { get; init; } = "public";
    public string Table { get; init; } = "";
    public string Column { get; init; } = "";
    public Dictionary<string, DatabaseValue> Where { get; init; } = [];
}
public sealed record DatabaseCondition
{
    public DatabaseValue Left { get; init; } = new();
    public string Operator { get; init; } = "eq";
    public DatabaseValue? Right { get; init; }
}
public sealed record DatabaseExecutionContext(string AdapterVersion = "",
    IReadOnlyDictionary<string, string>? AssetStatus = null, IReadOnlyList<string>? Warnings = null);

public sealed record DatabaseSchema(DatabaseTableSchema[] Tables)
{
    public string[] Warnings { get; init; } = [];
}
public sealed record DatabaseTableSchema(string Schema, string Name, string Kind,
    DatabaseColumnSchema[] Columns, string[] PrimaryKey, DatabaseUniqueKey[] UniqueKeys,
    DatabaseForeignKey[] ForeignKeys, DatabaseIndex[] Indexes);
public sealed record DatabaseColumnSchema(string Name, string DataType, string TypeName, bool Nullable,
    string? Default, bool Identity, bool Generated, int? MaximumLength);
public sealed record DatabaseUniqueKey(string Name, string[] Columns);
public sealed record DatabaseForeignKey(string Name, string[] Columns, string TargetSchema,
    string TargetTable, string[] TargetColumns);
public sealed record DatabaseIndex(string Name, string[] Columns, bool Unique, string Definition);
public sealed record DatabaseWriteResult(DatabaseTableWrite[] Tables, Dictionary<string, object?> Returned)
{
    public string? MediaId { get; init; }
}
public sealed record DatabaseTableWrite(string Name, int RowsWritten, int RowsRemoved);
public sealed record DatabaseChangePlan(string MediaId, DatabaseTablePlan[] Tables, string[] Warnings);
public sealed record DatabaseTablePlan(string Name, string Schema, string Table, int Inserts, int Updates,
    int Deletes, int NoOps, int Unresolved, DatabaseRowPlan[] Rows, string? SkippedReason = null);
public sealed record DatabaseRowPlan(string Operation, Dictionary<string, object?> Match, string[] Columns,
    string? Reason = null, int Count = 1);
