# fbtab

Runs `SELECT` statements from sql files against Firebird and writes real tab-separated files:
one line is one row, NULL is not the same as an empty string, and nothing depends on the locale.

Русская версия: [README.ru.md](README.ru.md).

```
fbtab --db server:alias --user REPORTS --out results  01_clients.sql 02_payments.sql
OK 01_clients rows=184203 sec=2.1
OK 02_payments rows=5120377 sec=48.9
```

## Why

`isql` output is made for reading on a screen: columns are padded or listed one per line, NULL looks
like text, a line break inside a value breaks the row, and numbers and dates come in isql's own
format. Turning that into data for pandas, DuckDB or a spreadsheet takes a parser per query.
fbtab writes a file those tools read as it is.

## What it does

- **Read only.** Each sql file runs on its own connection in a `READ ONLY` transaction that is rolled
  back at the end. Only `SELECT`, `WITH`, `EXECUTE BLOCK` and `EXECUTE PROCEDURE` are accepted; a file
  with anything else is refused before fbtab connects.
- **Scripts written for isql run as they are.** `SET TERM` is understood; `SET LIST`, `SET COUNT`,
  `SET HEADING` and other display settings are ignored.
- **All queries of a file are prepared before the first one runs**, so a typo in the last query
  shows up at once.
- **Streams.** Rows go from the server to the file one by one; the size of the result does not matter.
- **No half-written results.** Data goes to `<name>.tsv.part` and is renamed to `<name>.tsv` only
  when the query has finished.
- **Connects where isql connects.** By default fbtab goes through `fbclient`, so every authentication
  plugin, wire encryption and compression setting that works for `isql` works here. `--managed`
  connects without fbclient (Srp and Legacy_Auth only).
- **The password is never on the command line and never stored**: it comes from `FBTAB_PASSWORD` or
  `ISC_PASSWORD`, or is typed in.

Firebird 3, 4 and 5 (the tests run on 5). Windows and Linux, x64.

## Usage

```
fbtab --db <database> --user <user> [options] <file.sql> [<file.sql> ...]

  --db <database>        host:alias, host/port:path, inet://host:port/path or a local path
  --user <user>          user name (default: ISC_USER)
  --role <role>          role
  --charset <name>       connection character set (default: UTF8)
  --fbclient <path>      fbclient library to connect through (default: FBTAB_FBCLIENT,
                         then the one next to fbtab, then the one the system finds)
  --managed              connect without fbclient
  --isolation <mode>     read-committed (default) or snapshot
  --out <dir>            where to write (default: current directory)
  --version, --help
```

One line per sql file goes to standard output, progress goes to standard error:

```
OK <name> rows=<n> sec=<s>
ERROR <name> <sqlstate or -> <message>
CANCELLED <name> rows=<n> sec=<s>
```

| Exit code | Meaning |
|---|---|
| 0 | every file was exported |
| 1 | at least one file failed (bad sql, refused statement); the other files were still run |
| 2 | no connection or wrong credentials; the run stops at that file |
| 3 | bad arguments, missing file, no password, fbclient cannot be loaded |
| 4 | cancelled with Ctrl+C: the statement is cancelled on the server, the transaction rolled back |

### Isolation

The default is `READ COMMITTED READ ONLY`: a long export does not hold back garbage collection on a
busy server, but two queries of one file may see slightly different moments. When they must see the
same moment, put this in the first comment lines of the file (or pass `--isolation snapshot`):

```sql
-- isolation: snapshot
select ...;
select ...;
```

## Output

For `report.sql` fbtab writes `report.tsv` and `report.meta.json`. A second and third query in the
same file give `report_2.tsv`, `report_3.tsv`, each with its own `.meta.json`.

### The tsv

UTF-8 without BOM, lines end with LF, the first line holds the column names, fields are separated by
a tab. Inside a value:

| In the data | In the file |
|---|---|
| NULL | `\N` |
| empty string | nothing |
| backslash | `\\` |
| tab | `\t` |
| line feed | `\n` |
| carriage return | `\r` |

This is the text format of PostgreSQL `COPY`. A line of the file is always exactly one row, so
`wc -l`, `grep`, `awk` and `split` work on it.

| Firebird type | Written as |
|---|---|
| SMALLINT, INTEGER, BIGINT, INT128 | digits |
| NUMERIC, DECIMAL | exact, with a point and all digits of the scale: `12.50` |
| DOUBLE PRECISION, FLOAT | shortest text that reads back to the same value: `0.1`, `1E+300` |
| DECFLOAT | plain number: `1.5` |
| DATE | `2026-11-22` |
| TIME | `09:10:00.1234` |
| TIMESTAMP | `2026-11-22 09:10:00.1234` |
| TIME / TIMESTAMP WITH TIME ZONE | the same followed by the zone as stored: `2026-11-22 10:00:00.1234 Europe/Budapest`, `10:00:00.0000 +03:00` |
| BOOLEAN | `true` / `false` |
| CHAR(n) | without the trailing spaces Firebird pads it with |
| VARCHAR, BLOB SUB_TYPE TEXT | the text |
| binary BLOB, CHARACTER SET OCTETS | hex: `0x0001FF` |

### Reading it

DuckDB:

```sql
select * from read_csv('report.tsv', delim = '\t', header = true, nullstr = '\N', quote = '', escape = '');
```

pandas:

```python
import csv, pandas as pd
df = pd.read_csv('report.tsv', sep='\t', na_values=['\\N'], keep_default_na=False, quoting=csv.QUOTE_NONE)
```

DuckDB guesses column types from the values and takes a hex value such as `0x0001FF` for a number:
for binary columns add `types = {'BIN': 'VARCHAR'}` (or `all_varchar = true`). In pandas `dtype=str`
does the same.

Both leave `\t`, `\n`, `\r` and `\\` inside text values as written. If the texts can contain tabs,
line breaks or backslashes, unescape those columns:

```python
import re
unescape = lambda s: re.sub(r'\\(.)', lambda m: {'t': '\t', 'n': '\n', 'r': '\r'}.get(m[1], m[1]), s)
```

### The meta.json

What was run and what came back: the sql text and its line in the file, row count, start and finish
time, server time, engine version, user, isolation, and for every column its name, Firebird type,
length, scale and whether it can be NULL.

## What protects the database

1. The transaction is `READ ONLY`: Firebird itself refuses `INSERT`, `UPDATE`, `DELETE` and DDL in it,
   also inside `EXECUTE BLOCK` and inside procedures the query calls.
2. Statements are checked by their first words before anything is sent, so a script with `UPDATE` or
   `COMMIT` in it does not get as far as a connection.

One thing a read-only transaction still allows is writing to global temporary tables declared
`ON COMMIT DELETE ROWS`; such rows live until the end of the transaction, which fbtab rolls back.

## Limits

- No parameters: the sql is run as written.
- NUMERIC with more than 28 significant digits is beyond what the driver reads into a .NET decimal.
- CSV and Parquet output are planned, not done.

## Build

Needs the .NET 10 SDK.

```sh
dotnet build
dotnet test
scripts/publish.sh     # out/win-x64/fbtab.exe and out/linux-x64/fbtab: one file, no .NET needed
```

Without the variables below `dotnet test` runs the unit tests and skips the ones that need a server.

| Variable | For |
|---|---|
| `FBTAB_TEST_DB` | database the tests create and fill, e.g. `localhost/3055:/tmp/fbtab_test.fdb` (Firebird 4+) |
| `FBTAB_TEST_USER`, `FBTAB_TEST_PASSWORD` | default `SYSDBA` / `masterkey` |
| `FBTAB_TEST_FBCLIENT` | path to fbclient, for the tests that connect through it |

fbclient is not part of fbtab: take `fbclient.dll` (64-bit) and the files it needs from your Firebird
installation or from the Firebird zip kit, and put them next to `fbtab.exe` or point `--fbclient` at them.

## Authors

Written by [vmzubov](https://github.com/vmzubov) together with Claude (Anthropic), which is listed as
co-author in the commits.

## License

[MIT](LICENSE). fbtab uses [FirebirdSql.Data.FirebirdClient](https://github.com/FirebirdSQL/NETProvider) (MIT).
