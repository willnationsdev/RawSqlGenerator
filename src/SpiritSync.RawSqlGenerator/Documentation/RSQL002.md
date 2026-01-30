# RSQL002

Multiple [RawSql] attributes on '{0}' would produce the same constant '{1}'. Constant generation skipped.

## Solution

Override the `ConstantName` parameter of the `RawSqlAttribute` to manually assign a constant name for one of the variables to ensure no conflict occurs.

