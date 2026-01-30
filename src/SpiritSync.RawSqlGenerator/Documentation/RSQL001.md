# RSQL001

Multiple [RawSql] attributes on '{0}' target the same variable '{1}'. Only one will be used.

## Solution

Override the `VariableName` parameter of the `RawSqlAttribute` to manually assign a variable name for one of the attributes to ensure no conflict occurs.

