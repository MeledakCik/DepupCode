# Depup Code bundled formatters

`Shift+Alt+F` selects a formatter using the active file extension.

- Prettier 3.9.10 formats JavaScript, TypeScript, HTML, CSS, JSON, Markdown, YAML, GraphQL, Vue, Svelte, and Astro files. Its upstream MIT license is included with the package in `Prettier/node_modules/prettier/LICENSE`. It runs using the installed Node.js runtime.
- Ruff 0.17.3 formats Python files. The Windows x64 executable and its upstream MIT license are in `Ruff/`.
- Roslyn 5.9.0 formats C# files through `Microsoft.CodeAnalysis.CSharp.Workspaces`. Third party notices are in `Roslyn-ThirdPartyNotices.rtf`.
- Depup Code formats JSON and XML/XAML internally when a more specific Prettier formatter is not selected.
