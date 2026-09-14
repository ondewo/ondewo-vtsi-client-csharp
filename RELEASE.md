# Release History

*****************

## Release ONDEWO VTSI Csharp Client 8.7.0

### New Features

* Initial release of the ONDEWO VTSI C# client. The package `Ondewo.VTSI.Client` ships the
  gRPC message and client stubs generated from the [ONDEWO VTSI API](https://github.com/ondewo/ondewo-vtsi-api)
  by the `ondewo-csharp-proto-compiler` image of
  [ondewo-proto-compiler 5.15.1](https://github.com/ondewo/ondewo-proto-compiler): one
  `<Message>.cs` per proto plus one `<Service>Grpc.cs` per proto that declares a service, nested by C# namespace
  under `api/` - 48 files in this release.
* The package targets `netstandard2.0`, so it is consumable from .NET Framework 4.6.1+, .NET Core 2.0+ and every
  modern .NET, and carries `Google.Protobuf`, `Grpc.Net.Client`, `Grpc.Core.Api` and `Google.Api.CommonProtos` as
  its only dependencies. The `google/api`, `google/rpc` and `google/type` descriptors the API imports come from
  `Google.Api.CommonProtos` rather than being generated a second time into this assembly.
* `make build` is the whole pipeline — submodule checkout at the pinned versions, compiler image build, stub
  generation and `dotnet pack` — and `make ondewo_release` cuts the GitHub and NuGet release from the same
  version number.
* The generated stubs are **committed** under `api/`, like every other ONDEWO client SDK, together with the
  generated `Ondewo.VTSI.Client.csproj` and a `Directory.Build.props` carrying a fallback for every MSBuild pin
  that project file reads — which is what lets a clone without the `ondewo-proto-compiler` submodule build.
  `make check_dotnet_properties` fails the build if the two ever disagree.
* `auth/OndewoAuth.cs` is the hand-written bearer-token surface of the package: call metadata, an auth
  interceptor and call credentials for the `authorization: Bearer <token>` entry an ONDEWO server expects.
* `tests/` is an xUnit suite (2137 tests) over the committed stubs — a reflection-driven half that
  round-trips every generated message through its wire format, checks every enum starts at its zero value and
  binds every generated service client to a channel asserting it exposes every RPC its descriptor declares, plus
  concrete ONDEWO VTSI cases and full coverage of `auth/`. `make test` gates on 100% line, branch and method
  coverage of the hand-written sources; `api/` is excluded from the metric but exercised by the suite.
* The VTSI API imports the NLU, QA, S2T, SIP and T2S APIs, so the assembly ships their stubs too
  (23 services in total). That is deliberate: a VTSI consumer reaches them without a second package
  reference.
* `.github/workflows/ci.yml` builds, tests and packs the committed stubs on `ubuntu-latest` without the
  submodules and without the compiler image. Nothing in it is guarded by a directory check that could turn an
  empty repository into a green run: a missing `Ondewo.VTSI.Client.csproj` or an empty `api/` fails the job.

*****************
