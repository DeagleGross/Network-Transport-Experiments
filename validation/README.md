# Signature and example compile check

This project contains nonfunctional stubs matching the illustrative surface in `..\api.md` and representative server, TLS, client, Pipelines, and Redis-style examples. It exists only to catch C# signature and usage inconsistencies. It is not a transport prototype and none of its members perform I/O.

It targets `net11.0` with C# 13 and a `Microsoft.AspNetCore.App` framework reference for `System.IO.Pipelines`. Build from this directory with `dotnet build ProposalSurface.Validation.csproj`.
