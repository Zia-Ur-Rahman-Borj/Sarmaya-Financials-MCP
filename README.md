# Sarmaaya Index MCP Server

A .NET 8 MCP server for public Sarmaaya index analysis pages. It currently
implements only index analysis; other Sarmaaya features are intentionally out
of scope.

## MCP tool

`get_index_analysis(indexId)` loads `https://sarmaaya.pk/indexes/{indexId}` and
returns the displayed index quote, summary, performance periods, key statistics,
and constituent table, with the source URL, retrieval timestamp, and a data
disclaimer. For example, use `KSE100` or `KMI30`.

The scraper uses a headless Chromium browser because Sarmaaya renders its index
analysis in the browser. Google Chrome must be installed. To use another
Chromium-based executable, set `SARMAAYA_CHROME_EXECUTABLE` to its full path.
The scraper reads only publicly accessible pages and does not attempt to access
authenticated or premium-only data. A page that does not render its analysis
will result in an explicit tool error.

## Build and run

```sh
dotnet restore
dotnet build
```

Run the stdio MCP server:

```sh
dotnet run
```

Configure your MCP client to launch `dotnet run` from this project directory.
For clients that require an absolute executable, build first and invoke
`dotnet` with the project DLL as the argument.
