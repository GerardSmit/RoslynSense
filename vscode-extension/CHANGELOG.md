# Changelog

All notable RoslynSense extension changes are documented here.

## Unreleased

- Added Roslyn-backed C# language services, solution and discovery explorers, testing, debugging,
  hot reload, NuGet management, settings, search, project properties, and the supporting language
  packs.
- Added managed installation and updating of the RoslynSense .NET tool.
- Added VS Code host integration tests and release packaging.
- Fixed the inheritance lens reporting itself out of date when clicked. The click now asks about
  the member at the lens's own position, and Show Inheritance (Ctrl+Alt+U) works from anywhere
  inside a member instead of only on its signature line.
