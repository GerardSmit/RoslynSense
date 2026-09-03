# Vendored MSBuild documentation

`elements.json`, `items.json` and `properties.json` are taken verbatim from
[tintoy/msbuild-project-tools-server](https://github.com/tintoy/msbuild-project-tools-server),
MIT licensed — see `LICENSE`, which is that project's, copied alongside them.

They are what the pack's completion and hover know about MSBuild itself: what a property means, what
values it takes, which metadata an item type carries. Vendored rather than hand-written because the
alternative is a table that starts at fifteen properties and never catches up; vendored rather than
referenced because a NuGet dependency on a language server to read four JSON files is the wrong
trade.

| File | What it carries |
| --- | --- |
| `properties.json` | Property descriptions, and `defaultValues` where a property takes a fixed set — which is what makes value completion general rather than one hand-written case per property. |
| `items.json` | Item types and their metadata. The `"*"` key is the metadata common to every item type. |
| `elements.json` | Elements and attributes. `*.Condition` and `*.Label` are wildcards matching those attributes on any element. |

`tasks.json` is deliberately **not** vendored. It is 179 KB describing every MSBuild task and its
parameters, and it exists to serve task and task-parameter completion inside `<Target>` — which this
pack does not implement, and which the upstream project marks experimental. Shipping it would be
178 KB of dead weight in the tool package.

Upstream extracted these from the `MSBuild.*.xsd` schemas. Two consequences worth knowing before
editing them: a property Microsoft adds tomorrow is absent until upstream refreshes, and anything
edited here is a local fork that the next refresh has to be merged against. Prefer adding
RoslynSense-specific entries to `MsBuildWellKnownValues` instead, which is layered on top precisely
so these stay pristine.
