# Generator package verification

Run the package matrix from the repository root:

```sh
tests/Mugi.Generators.Tests/packaging/verify.sh
```

The script packs `Mugi.Json`, `Mugi`, and `Mugi.Generators` into a local feed. It verifies the analyzer payload, the absence of Roslyn package dependencies, a direct package reference, a project-reference consumer that receives the generator transitively, JIT execution, and a self-contained NativeAOT publish. Generated compiler files are checked in both consumer projects.

Temporary packages, restored dependencies, and publish output are written under `tests/Mugi.Generators.Tests/packaging/artifacts/`.
