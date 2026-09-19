You need a real browser to do this job — use whatever browser-automation MCP tool this session has
available (e.g. a Playwright/Puppeteer/Chrome MCP server) to load the app and take screenshots or
inspect the rendered DOM; a `Bash`/`PowerShell` tool alone (curl, grep) cannot show you what a person
actually sees. If no browser tool is available in this session, say so explicitly in your report
instead of reviewing from the diff alone — that would silently defeat the point of this role.
