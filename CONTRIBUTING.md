# Contributing

Contributions are welcome. This repository is public. Bug reports, fixes,
and documentation improvements are all useful.

## Issues

Open a GitHub issue for a bug, a question about behavior, or a change you
want to discuss before writing it. Search open issues first.

Include the SDK version you are using, the runtime, and the steps that
show the problem. Do not paste access tokens, passwords, or private
endpoints.

## Pull requests

1. Fork the repository and branch from `main`.
2. Keep the change focused.
3. Use a [Conventional Commits](https://www.conventionalcommits.org/)
   subject:

   `type(scope): subject`

   `type` is one of `feat`, `fix`, `docs`, `deps`, `style`, `refactor`,
   `perf`, `test`, `build`, `ci`, `chore`, or `revert`. `scope` is
   optional.
4. Open the pull request against `main`. Say what changed and how you
   checked it.

Do not commit access tokens, passwords, private endpoints, or local
credentials.

## Checks

`global.json` pins the .NET SDK and sets `rollForward` to `disable`. From
the repository root:

```sh
dotnet run --project Haiven.Sdk.Tests/Haiven.Sdk.Tests.csproj
```

The test project is a console runner. A non-zero exit is a failure.
