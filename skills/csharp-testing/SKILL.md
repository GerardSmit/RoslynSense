---
name: csharp-testing
description: C#/.NET testing conventions plus the RoslynSense tools for discovering, running, and analyzing tests — test structure and naming, mocking, code coverage, and running only the tests your changes affect. Use when writing or fixing tests, investigating test failures, or measuring coverage in a C# project.
---
# C# Testing with RoslynSense

## Test structure

- Separate test project: **`[ProjectName].Tests`**.
- Mirror classes: `CatDoor` -> `CatDoorTests`.
- Name tests by behavior: `WhenCatMeowsThenCatDoorOpens`.
- Follow existing naming conventions.
- Use **public instance** classes; avoid **static** fields.
- No branching/conditionals inside tests.

## Unit tests

- One behavior per test;
- Avoid Unicode symbols.
- Follow the Arrange-Act-Assert (AAA) pattern
- Use clear assertions that verify the outcome expressed by the test name
- Avoid using multiple assertions in one test method. In this case, prefer multiple tests.
- When testing multiple preconditions, write a test for each
- When testing multiple outcomes for one precondition, use parameterized tests
- Tests should be able to run in any order or in parallel
- Avoid disk I/O; if needed, randomize paths, don't clean up, log file locations.
- Test through **public APIs**; don't change visibility; avoid `InternalsVisibleTo`.
- Require tests for new/changed **public APIs**.
- Assert specific values and edge cases, not vague outcomes.

## Test workflow

- **Work test-driven.** For new features, write the test first. For bugs, write a failing test that reproduces the issue before fixing it.
- Work on only one test until it passes. Then run other tests to ensure nothing has been broken.

## Test framework

- **Use the framework already in the solution** (xUnit/NUnit/MSTest) for new tests. The test project's `.csproj` names it in its package references.

## Mocking

- Use NSubstitute
- Avoid mocks/Fakes if possible
- External dependencies can be mocked. Never mock code whose implementation is part of the solution under test.
- Try to verify that the outputs (e.g. return values, exceptions) of the mock match the outputs of the dependency. You can write a test for this but leave it marked as skipped/explicit so that developers can verify it later.

## Finding the tests that cover something

- **FindUsages** on the member you are about to change: its references from a test project are the tests that exercise it, and they are the ones to run first.

## Running tests

- **RunTests** — run tests with an optional filter expression. Examples:
  - `filter: "ClassName.MethodName"` — run a specific test
  - `filter: "FullyQualifiedName~MyTest"` — substring match
  - `filter: "Category=Unit"` — by category
  - No filter runs all tests in the project.
  - Set `background: true` to run tests in the background and continue working. Check results later with **GetBackgroundTaskResult**.
- **GetTestFailures** — the failures from the last run, each resolved to the file and line of the failing assertion. Use it after RunTests instead of re-reading run output or re-running with verbose logging.
- Work on one failing test at a time until it passes, then run the full suite.
- If a test is failing and the cause isn't clear from the error message, narrow it with a `filter` down to the one test and read **GetTestFailures**, which resolves each failure to the assertion's own file and line.

## Tool selection

| Task | Preferred Tool | Avoid |
|------|---------------|-------|
| Run a specific test | **RunTests** with `filter` | `dotnet test --filter` (use RunTests instead) |
| See why a test failed | **GetTestFailures** | Re-running with verbose logging |
| Re-run tests after an edit | **RunTests** with a `filter` covering what you touched | Running the whole suite for a one-file change |
| Run tests while doing other work | **RunTests** with `background: true` + **GetBackgroundTaskResult** | — |
