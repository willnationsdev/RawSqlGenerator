# RSQL003

[RawSql] can only parse interpolated strings that embed invocation calls with initialized variable declarations in the body. Variable '{0}' may be interpolated properly.

## Solution

If you are conditionally populating a variable using an if/else block, replace it with a ternary operator.
If using a try/catch block instead, then reformat your use of the variable to not require that.

