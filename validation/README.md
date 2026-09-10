# Signature and example compile check

This project contains nonfunctional stubs matching the callback-first surface in `..\api.md`. The separate `..\examples\NetworkTransportExamples.csproj` references it, so the usage examples compile as external consumers and exercise the correct protected override rules. Neither project is a transport prototype and none of its members perform I/O.

It targets `net11.0` with C# 13 and a `Microsoft.AspNetCore.App` framework reference for `System.IO.Pipelines`. Build from this directory with `dotnet build ProposalSurface.Validation.csproj`.
