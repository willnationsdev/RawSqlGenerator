# RSQL004

Only static methods with a single return statement are permitted within interpolated strings evaluated by the RawSqlGenerator. Only the first one will be used.

## Solution

Remove extraneous return statements from the static method you are embedding within your interpolated string.

