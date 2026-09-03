# Releasing RoslynSense

The CI workflow builds the .NET tool and VS Code extension at the same version, tests both artifacts, publishes them, and creates a GitHub release. A release can start from a `v*` tag or from the workflow's **Run workflow** button.

## One-time repository setup

NuGet publishing requires the `NUGET_API_KEY` secret.

Visual Studio Marketplace publishing uses Microsoft Entra workload identity rather than a long-lived personal access token. This follows Microsoft's secure automated publishing guidance and avoids the global PAT flow that is scheduled for retirement on December 1, 2026.

1. Create or select a user-assigned managed identity in Azure.
2. Add a federated identity credential that trusts this repository's GitHub `release` environment.
3. Give the identity at least Reader access to the Azure subscription so `azure/login` can authenticate it.
4. After signing in as the identity, retrieve its Azure DevOps/Marketplace profile ID with
   `az rest -u https://app.vssps.visualstudio.com/_apis/profile/profiles/me --resource 499b84ac-1321-427f-aa17-267ca6975798`.
5. In the Visual Studio Marketplace publisher management page, add that returned `id` as a member of the `roslyn-sense` publisher and grant it the **Contributor** role. This profile ID is different from the managed identity's Azure resource ID.
6. Create a protected GitHub environment named `release` and add these secrets:
   - `AZURE_CLIENT_ID`: managed identity client ID
   - `AZURE_TENANT_ID`: Azure tenant ID
   - `AZURE_SUBSCRIPTION_ID`: Azure subscription ID
   - `NUGET_API_KEY`: scoped NuGet.org API key for the `RoslynSense` package

Require reviewers for the `release` environment if publication should have a manual approval gate.

## What the workflow verifies

- Builds and tests the solution on Windows and Linux.
- Runs extension unit tests and audits shipped npm dependencies.
- Runs VS Code extension-host tests on Windows stable, Linux stable, and Linux Insiders against a temporary published LSP server.
- Builds the NuGet package on Windows and verifies that its x64/x86 workers and tray payload are present.
- Installs the exact packed tool and verifies `--version` on Windows and in a Linux .NET SDK container.
- When a previous release exists, installs it as the update target, starts the newly packed version as a daemon, stops it with the same bootstrap mechanism used by the extension, updates the previous installation, and verifies the installed version.
- Publishes the already-tested `.nupkg` and `.vsix`; it does not rebuild during publication.

## Starting a release

For the normal path, open the **CI** workflow and choose **Run workflow**. Select `patch`, `minor`, or `major`, then choose the VS Code Marketplace channel. `prerelease` is the default for manual runs; it marks both the packaged VSIX and Marketplace publication as prerelease while keeping the required numeric extension version. `stable` publishes to the normal channel. The .NET tool remains a normal NuGet release in either case. The version is calculated from the latest `v*` tag. Publication occurs only after every build, integration test, package inspection, and install/update smoke test succeeds.

Marketplace extension versions cannot use SemVer suffixes. A prerelease `0.3.1` and stable `0.3.1` cannot both exist, so the later stable extension must use another version such as `0.3.2`. Tag-triggered releases publish the extension as stable; use the manual workflow selector for prereleases.

Maintainers can also push an annotated semantic version tag such as `v0.3.0`. In that case the tag is the version source of truth.

Both publishers use duplicate-safe commands, so a failed run can be retried after correcting credentials or service availability. Confirm that the NuGet and Marketplace publisher IDs in the workflow and `vscode-extension/package.json` still match before transferring ownership.

References:

- [Publishing Extensions](https://code.visualstudio.com/api/working-with-extensions/publishing-extension)
- [Secure automated publishing to Visual Studio Marketplace](https://code.visualstudio.com/api/working-with-extensions/publishing-extension#secure-automated-publishing-to-visual-studio-marketplace)
